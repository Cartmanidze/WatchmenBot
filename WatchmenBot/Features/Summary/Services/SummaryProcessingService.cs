using System.Diagnostics;
using System.Text.RegularExpressions;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using WatchmenBot.Features.Messages.Services;
using WatchmenBot.Features.Admin.Services;

namespace WatchmenBot.Features.Summary.Services;

/// <summary>
/// Core processing service for /summary command.
/// Extracted from BackgroundSummaryWorker to enable direct testing and Hangfire integration.
/// </summary>
public partial class SummaryProcessingService(
    MessageStore messageStore,
    SmartSummaryService smartSummary,
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

        var messages = await messageStore.GetMessagesAsync(item.ChatId, startUtc, nowUtc);

        if (messages.Count == 0)
        {
            await bot.SendMessage(
                chatId: item.ChatId,
                text: $"За последние {item.Hours} часов сообщений не найдено.",
                replyParameters: new ReplyParameters { MessageId = item.ReplyToMessageId },
                cancellationToken: ct);

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

        // Send result
        await SendSummaryResponseAsync(item.ChatId, item.ReplyToMessageId, report, ct);

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
        try
        {
            await bot.SendMessage(
                chatId: chatId,
                text: report,
                parseMode: ParseMode.Html,
                linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true },
                replyParameters: new ReplyParameters { MessageId = replyToMessageId },
                cancellationToken: ct);
        }
        catch (Telegram.Bot.Exceptions.ApiRequestException ex) when (ex.Message.Contains("can't parse entities"))
        {
            logger.LogWarning("[SummaryProcessing] HTML parsing failed, sending as plain text");
            var plainText = HtmlTagRegex().Replace(report, "");
            await bot.SendMessage(
                chatId: chatId,
                text: plainText,
                linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true },
                replyParameters: new ReplyParameters { MessageId = replyToMessageId },
                cancellationToken: ct);
        }
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

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex HtmlTagRegex();
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