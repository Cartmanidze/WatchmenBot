using System.Diagnostics;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using WatchmenBot.Features.Search.Services;
using WatchmenBot.Models;
using WatchmenBot.Infrastructure.Settings;
using WatchmenBot.Features.Messages.Services;
using WatchmenBot.Features.Admin.Services;

namespace WatchmenBot.Features.Summary.Services;

public class DailySummaryService(
    ITelegramBotClient bot,
    IServiceProvider serviceProvider,
    LogCollector logCollector,
    ILogger<DailySummaryService> logger,
    IConfiguration configuration)
    : BackgroundService
{
    private TimeSpan GetTimezoneOffset()
    {
        var offsetStr = configuration["Admin:TimezoneOffset"] ?? "+06:00";
        var clean = offsetStr.TrimStart('+');
        return TimeSpan.TryParse(clean, out var offset) ? offset : TimeSpan.FromHours(6);
    }

    private TimeSpan GetSummaryTime()
    {
        var timeStr = configuration["DailySummary:TimeOfDay"] ?? "21:00";
        return TimeSpan.TryParse(timeStr, out var parsed) ? parsed : new TimeSpan(21, 0, 0);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tz = GetTimezoneOffset();
        var summaryTime = GetSummaryTime();

        logger.LogInformation("[DailySummary] Service STARTED. Scheduled at {Time} (UTC+{Offset})",
            summaryTime, tz);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Re-read settings each iteration (allows dynamic updates via DB)
                using var scope = serviceProvider.CreateScope();
                var settings = scope.ServiceProvider.GetRequiredService<AdminSettingsStore>();

                tz = await settings.GetTimezoneOffsetAsync();
                var timeStr = await settings.GetSummaryTimeAsync();
                summaryTime = TimeSpan.TryParse(timeStr, out var t) ? t : new TimeSpan(21, 0, 0);

                var nextRun = GetNextRunTime(summaryTime, tz);
                var delay = nextRun - DateTimeOffset.UtcNow;
                if (delay < TimeSpan.Zero) delay = TimeSpan.FromMinutes(1);

                var nextRunInTz = nextRun.ToOffset(tz);
                logger.LogInformation("[DailySummary] Next run at {NextRun} UTC+{Offset} (in {Hours:F1}h)",
                    nextRunInTz.ToString("yyyy-MM-dd HH:mm"), tz, delay.TotalHours);

                await Task.Delay(delay, stoppingToken);

                if (stoppingToken.IsCancellationRequested) break;

                await RunSummaryForToday(tz, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[DailySummary] Error in summary loop");
                logCollector.LogError("DailySummary", "Error in summary loop", ex);
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }

        logger.LogInformation("[DailySummary] Service STOPPED");
    }

    private static DateTimeOffset GetNextRunTime(TimeSpan targetTime, TimeSpan timezoneOffset)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var nowInTz = nowUtc.ToOffset(timezoneOffset);

        var todayTarget = new DateTimeOffset(
            nowInTz.Year, nowInTz.Month, nowInTz.Day,
            targetTime.Hours, targetTime.Minutes, 0, timezoneOffset);

        if (todayTarget <= nowUtc)
            todayTarget = todayTarget.AddDays(1);

        return todayTarget.ToUniversalTime();
    }

    private async Task RunSummaryForToday(TimeSpan timezoneOffset, CancellationToken ct)
    {
        using var scope = serviceProvider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<MessageStore>();
        var smartSummary = scope.ServiceProvider.GetRequiredService<SmartSummaryService>();
        var embeddingService = scope.ServiceProvider.GetRequiredService<EmbeddingService>();

        var sw = Stopwatch.StartNew();
        var chatIds = await store.GetDistinctChatIdsAsync();

        // Calculate "today" in the configured timezone (from midnight to now)
        var nowUtc = DateTimeOffset.UtcNow;
        var nowInTz = nowUtc.ToOffset(timezoneOffset);
        var todayStart = new DateTimeOffset(
            nowInTz.Year, nowInTz.Month, nowInTz.Day, 0, 0, 0, timezoneOffset);
        var startUtc = todayStart.ToUniversalTime();

        logger.LogInformation("[DailySummary] Starting for {Date}, {ChatCount} chats to process",
            todayStart.ToString("yyyy-MM-dd"), chatIds.Count);

        var successCount = 0;
        var totalMessages = 0;

        foreach (var chatId in chatIds)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var messages = await store.GetMessagesAsync(chatId, startUtc, nowUtc);
                if (messages.Count == 0)
                {
                    logger.LogDebug("[DailySummary] Chat {ChatId}: no messages yesterday", chatId);
                    continue;
                }

                logger.LogInformation("[DailySummary] Chat {ChatId}: processing {Count} messages...",
                    chatId, messages.Count);

                // Store embeddings for new messages (for RAG)
                await StoreEmbeddingsForNewMessages(embeddingService, chatId, messages, ct);

                // Generate smart summary using embeddings
                var report = await smartSummary.GenerateSmartSummaryAsync(
                    chatId, messages, startUtc, nowUtc, "за сегодня", ct);

                // Try HTML first, fallback to plain text if parsing fails
                try
                {
                    await bot.SendMessage(
                        chatId: chatId,
                        text: report,
                        parseMode: ParseMode.Html,
                        linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true },
                        cancellationToken: ct);
                }
                catch (Telegram.Bot.Exceptions.ApiRequestException ex) when (ex.Message.Contains("can't parse entities"))
                {
                    logger.LogWarning("[DailySummary] HTML parsing failed for chat {ChatId}, sending as plain text", chatId);
                    var plainText = System.Text.RegularExpressions.Regex.Replace(report, "<[^>]+>", "");
                    await bot.SendMessage(
                        chatId: chatId,
                        text: plainText,
                        linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true },
                        cancellationToken: ct);
                }

                successCount++;
                totalMessages += messages.Count;
                logCollector.IncrementSummaries();
                logger.LogInformation("[DailySummary] Chat {ChatId}: summary SENT ({Count} messages)",
                    chatId, messages.Count);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[DailySummary] Chat {ChatId}: FAILED", chatId);
                logCollector.LogError("DailySummary", $"Chat {chatId} failed", ex);
            }
        }

        sw.Stop();
        logger.LogInformation("[DailySummary] Complete: {Success}/{Total} chats, {Messages} messages, {Elapsed:F1}s",
            successCount, chatIds.Count, totalMessages, sw.Elapsed.TotalSeconds);
    }

    private async Task StoreEmbeddingsForNewMessages(EmbeddingService embeddingService, long chatId, List<MessageRecord> messages, CancellationToken ct)
    {
        try
        {
            // Filter messages that don't have embeddings yet
            var newMessages = new List<MessageRecord>();
            foreach (var msg in messages.Where(m => !string.IsNullOrWhiteSpace(m.Text)))
            {
                if (!await embeddingService.HasEmbeddingAsync(chatId, msg.Id, ct))
                {
                    newMessages.Add(msg);
                }
            }

            if (newMessages.Count > 0)
            {
                await embeddingService.StoreMessageEmbeddingsBatchAsync(newMessages, ct);
                logCollector.IncrementEmbeddings(newMessages.Count);
                logger.LogDebug("Stored {Count} new embeddings for chat {ChatId}", newMessages.Count, chatId);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to store embeddings for chat {ChatId}", chatId);
        }
    }
}
