using System.Diagnostics;
using Telegram.Bot;
using Telegram.Bot.Types;
using WatchmenBot.Extensions;
using WatchmenBot.Features.Messages.Services;
using WatchmenBot.Features.Admin.Services;

namespace WatchmenBot.Features.Summary.Services;

/// <summary>
/// Core processing service for /summary command.
/// Extracted from BackgroundSummaryWorker to enable direct testing and Hangfire integration.
/// </summary>
public class SummaryProcessingService(
    MessageStore messageStore,
    SmartSummaryService smartSummary,
    ChatStatusService chatStatusService,
    LogCollector logCollector,
    ITelegramBotClient bot,
    ILogger<SummaryProcessingService> logger)
{
    /// <summary>
    /// Process summary generation request and send response.
    /// </summary>
    public async Task<SummaryProcessingResult> ProcessAsync(SummaryQueueItem item, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        logger.LogInformation("[SummaryProcessing] Processing summary for chat {ChatId}, {Hours}h, requested by @{User}",
            item.ChatId, item.Hours, item.RequestedBy);

        var nowUtc = DateTimeOffset.UtcNow;
        var startUtc = nowUtc.AddHours(-item.Hours);

        // Send typing action (safe: deactivates chat on 403)
        if (!await bot.TrySendChatActionAsync(chatStatusService, item.ChatId, Telegram.Bot.Types.Enums.ChatAction.Typing, logger, ct))
        {
            // Chat was deactivated - no point continuing
            sw.Stop();
            return new SummaryProcessingResult
            {
                Success = false,
                MessageCount = 0,
                ElapsedSeconds = sw.Elapsed.TotalSeconds
            };
        }

        var messages = await messageStore.GetMessagesAsync(item.ChatId, startUtc, nowUtc);

        if (messages.Count == 0)
        {
            try
            {
                await bot.SendMessageSafeAsync(
                    chatStatusService,
                    item.ChatId,
                    $"За последние {item.Hours} часов сообщений не найдено.",
                    logger,
                    replyToMessageId: item.ReplyToMessageId,
                    ct: ct);
            }
            catch (ChatDeactivatedException)
            {
                // Chat deactivated - return without error
            }

            sw.Stop();
            return new SummaryProcessingResult
            {
                Success = true,
                MessageCount = 0,
                ElapsedSeconds = sw.Elapsed.TotalSeconds
            };
        }

        // Generate summary (may take 30-120 seconds)
        var periodText = GetPeriodText(item.Hours);
        var report = await smartSummary.GenerateSmartSummaryAsync(
            item.ChatId, messages, startUtc, nowUtc, periodText, ct);

        // Send result (safe: handles 403 and HTML fallback)
        try
        {
            await SendSummaryResponseAsync(item.ChatId, item.ReplyToMessageId, report, ct);
        }
        catch (ChatDeactivatedException)
        {
            // Chat was deactivated - job should not retry
            sw.Stop();
            return new SummaryProcessingResult
            {
                Success = false,
                MessageCount = messages.Count,
                ElapsedSeconds = sw.Elapsed.TotalSeconds
            };
        }

        sw.Stop();
        logCollector.IncrementSummaries();

        logger.LogInformation("[SummaryProcessing] Summary sent to chat {ChatId} ({MessageCount} messages) in {Elapsed:F1}s",
            item.ChatId, messages.Count, sw.Elapsed.TotalSeconds);

        return new SummaryProcessingResult
        {
            Success = true,
            MessageCount = messages.Count,
            ElapsedSeconds = sw.Elapsed.TotalSeconds
        };
    }

    private async Task SendSummaryResponseAsync(long chatId, int replyToMessageId, string report, CancellationToken ct)
    {
        // Use safe extension (handles 403 and HTML fallback)
        await bot.SendHtmlMessageSafeAsync(
            chatStatusService,
            chatId,
            report,
            logger,
            replyToMessageId: replyToMessageId,
            ct: ct);
    }

    private static string GetPeriodText(int hours)
    {
        return hours switch
        {
            24 => "за сутки",
            _ when hours < 24 => $"за {hours} час{GetHourSuffix(hours)}",
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

}

/// <summary>
/// Result of processing /summary request.
/// </summary>
public class SummaryProcessingResult
{
    public bool Success { get; init; }
    public int MessageCount { get; init; }
    public double ElapsedSeconds { get; init; }
}