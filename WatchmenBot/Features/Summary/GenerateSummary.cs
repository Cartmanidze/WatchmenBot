using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using WatchmenBot.Extensions;
using WatchmenBot.Features.Summary.Services;
using WatchmenBot.Features.Messages.Services;
using WatchmenBot.Features.Admin.Services;

namespace WatchmenBot.Features.Summary;

public class GenerateSummaryRequest
{
    public required Message Message { get; init; }
    public int Hours { get; init; } = 24;
}

public class GenerateSummaryResponse
{
    public bool IsSuccess { get; init; }
    public string? ErrorMessage { get; init; }
    public int MessageCount { get; init; }
    public bool ChatDeactivated { get; init; }

    public static GenerateSummaryResponse Success(int messageCount) => new()
    {
        IsSuccess = true,
        MessageCount = messageCount
    };

    public static GenerateSummaryResponse Error(string errorMessage) => new()
    {
        IsSuccess = false,
        ErrorMessage = errorMessage
    };

    public static GenerateSummaryResponse Deactivated() => new()
    {
        IsSuccess = false,
        ChatDeactivated = true,
        ErrorMessage = "Chat was deactivated"
    };
}

/// <summary>
/// Handler for /summary command.
/// Generates chat summary for specified time period.
/// Handles 403 errors by deactivating chat.
/// </summary>
public class GenerateSummaryHandler(
    ITelegramBotClient bot,
    MessageStore store,
    SmartSummaryService smartSummary,
    ChatStatusService chatStatusService,
    ILogger<GenerateSummaryHandler> logger)
{
    public async Task<GenerateSummaryResponse> HandleAsync(GenerateSummaryRequest request, CancellationToken ct)
    {
        var message = request.Message;
        var chatId = message.Chat.Id;
        var hours = request.Hours;

        try
        {
            // Send "typing" indicator (safe: deactivates chat on 403)
            if (!await bot.TrySendChatActionAsync(chatStatusService, chatId, ChatAction.Typing, logger, ct))
            {
                // Chat was deactivated - no point continuing
                return GenerateSummaryResponse.Deactivated();
            }

            var nowUtc = DateTimeOffset.UtcNow;
            var startUtc = nowUtc.AddHours(-hours);

            logger.LogInformation("Generating summary for chat {ChatId}, last {Hours} hours", chatId, hours);

            var messages = await store.GetMessagesAsync(chatId, startUtc, nowUtc);

            if (messages.Count == 0)
            {
                try
                {
                    await bot.SendMessageSafeAsync(
                        chatStatusService,
                        chatId,
                        $"За последние {hours} часов сообщений не найдено.",
                        logger,
                        replyToMessageId: message.MessageId,
                        ct: ct);
                }
                catch (ChatDeactivatedException)
                {
                    return GenerateSummaryResponse.Deactivated();
                }

                return GenerateSummaryResponse.Success(0);
            }

            // Build and send the report using smart summary
            var periodText = GetPeriodText(hours);
            var report = await smartSummary.GenerateSmartSummaryAsync(
                chatId, messages, startUtc, nowUtc, periodText, ct);

            // Send response (safe: handles 403 and HTML fallback)
            try
            {
                await bot.SendHtmlMessageSafeAsync(
                    chatStatusService,
                    chatId,
                    report,
                    logger,
                    replyToMessageId: message.MessageId,
                    ct: ct);
            }
            catch (ChatDeactivatedException)
            {
                return GenerateSummaryResponse.Deactivated();
            }

            logger.LogInformation("Sent summary to chat {ChatId} ({MessageCount} messages)", chatId, messages.Count);

            return GenerateSummaryResponse.Success(messages.Count);
        }
        catch (ChatDeactivatedException)
        {
            // Catch any other deactivation that might have been thrown
            return GenerateSummaryResponse.Deactivated();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to generate summary for chat {ChatId}", chatId);

            try
            {
                await bot.SendMessageSafeAsync(
                    chatStatusService,
                    chatId,
                    "Произошла ошибка при генерации выжимки. Попробуйте позже.",
                    logger,
                    replyToMessageId: message.MessageId,
                    ct: ct);
            }
            catch (ChatDeactivatedException)
            {
                return GenerateSummaryResponse.Deactivated();
            }
            catch (Exception sendEx)
            {
                logger.LogWarning(sendEx, "[GenerateSummary] Failed to send error notification to chat {ChatId}", chatId);
            }

            return GenerateSummaryResponse.Error(ex.Message);
        }
    }

    private static string GetPeriodText(int hours)
    {
        return hours switch
        {
            24 => "за сутки",
            < 24 => $"за {hours} час{GetHourSuffix(hours)}",
            _ => $"за {hours / 24} дн{GetDaySuffix(hours / 24)}"
        };
    }

    private static string GetHourSuffix(int hours)
    {
        if (hours % 100 >= 11 && hours % 100 <= 14) return "ов";
        return (hours % 10) switch
        {
            1 => "",
            2 or 3 or 4 => "а",
            _ => "ов"
        };
    }

    private static string GetDaySuffix(int days)
    {
        if (days % 100 >= 11 && days % 100 <= 14) return "ей";
        return (days % 10) switch
        {
            1 => "ь",
            2 or 3 or 4 => "я",
            _ => "ей"
        };
    }

    /// <summary>
    /// Parse the /summary command to extract hours parameter
    /// </summary>
    public static int ParseHoursFromCommand(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 24;

        // /summary 48  or /summary 48h or /summary 2d
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return 24;

        var param = parts[1].ToLowerInvariant().Trim();

        // Days: "2d" or "2д"
        if (param.EndsWith("d") || param.EndsWith("д"))
        {
            if (int.TryParse(param.TrimEnd('d', 'д'), out var days) && days > 0 && days <= 30)
                return days * 24;
        }
        // Hours: "48h" or "48ч" or just "48"
        else if (param.EndsWith("h") || param.EndsWith("ч"))
        {
            if (int.TryParse(param.TrimEnd('h', 'ч'), out var h) && h > 0 && h <= 720)
                return h;
        }
        else if (int.TryParse(param, out var hours) && hours > 0 && hours <= 720)
        {
            return hours;
        }

        return 24;
    }
}
