using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using WatchmenBot.Features.Messages.Services;
using WatchmenBot.Features.Admin.Services;
using WatchmenBot.Infrastructure.Database;

namespace WatchmenBot.Features.Summary.Services;

/// <summary>
/// Background worker for summary generation.
/// Uses PostgreSQL LISTEN/NOTIFY for instant notifications with polling fallback.
/// </summary>
public partial class BackgroundSummaryWorker(
    SummaryQueueService queue,
    PostgresNotificationService notifications,
    ITelegramBotClient bot,
    IServiceProvider serviceProvider,
    LogCollector logCollector,
    ILogger<BackgroundSummaryWorker> logger)
    : BackgroundService
{
    private static readonly TimeSpan NotificationTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(6);
    private DateTime _lastCleanup = DateTime.UtcNow;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("[BackgroundSummary] Worker started (LISTEN/NOTIFY + polling fallback)");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Strategy: Wait for notification OR timeout, then check DB
                // This ensures we don't miss items even if notification is lost
                await WaitForNotificationOrTimeoutAsync(stoppingToken);

                // Get pending requests from DB (notification is just a hint)
                var items = await queue.GetPendingAsync(limit: 5);

                if (items.Count == 0)
                {
                    await PeriodicCleanupAsync();
                    continue;
                }

                // Process each request
                foreach (var item in items)
                {
                    if (stoppingToken.IsCancellationRequested) break;

                    try
                    {
                        await queue.MarkAsStartedAsync(item.Id);
                        await ProcessSummaryRequestAsync(item, stoppingToken);
                        await queue.MarkAsCompletedAsync(item.Id);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "[BackgroundSummary] Failed to process summary for chat {ChatId}", item.ChatId);

                        await queue.MarkAsFailedAsync(item.Id, ex.Message);

                        try
                        {
                            await bot.SendMessage(
                                chatId: item.ChatId,
                                text: "Произошла ошибка при генерации выжимки. Попробуйте позже.",
                                replyParameters: new ReplyParameters { MessageId = item.ReplyToMessageId },
                                cancellationToken: stoppingToken);
                        }
                        catch (Exception sendEx)
                        {
                            logger.LogWarning(sendEx, "[BackgroundSummary] Failed to send error notification to chat {ChatId}", item.ChatId);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[BackgroundSummary] Error in worker loop");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }

        logger.LogInformation("[BackgroundSummary] Worker stopped");
    }

    /// <summary>
    /// Wait for a notification or timeout (whichever comes first).
    /// Notification provides instant response, timeout ensures polling fallback.
    /// </summary>
    private async Task WaitForNotificationOrTimeoutAsync(CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(NotificationTimeout);

        try
        {
            // Wait for notification (instant) or timeout (30s fallback)
            await notifications.SummaryQueueNotifications.WaitToReadAsync(timeoutCts.Token);

            // Drain all pending notifications (we'll fetch from DB anyway)
            while (notifications.SummaryQueueNotifications.TryRead(out var itemId))
            {
                logger.LogDebug("[BackgroundSummary] Received notification for item {ItemId}", itemId);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timeout - this is normal, we'll poll the DB
        }
    }

    private async Task PeriodicCleanupAsync()
    {
        if (DateTime.UtcNow - _lastCleanup > CleanupInterval)
        {
            await queue.CleanupOldAsync(daysToKeep: 7);
            _lastCleanup = DateTime.UtcNow;
        }
    }

    private async Task ProcessSummaryRequestAsync(SummaryQueueItem item, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        logger.LogInformation("[BackgroundSummary] Processing summary for chat {ChatId}, {Hours}h, requested by @{User}",
            item.ChatId, item.Hours, item.RequestedBy);

        using var scope = serviceProvider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<MessageStore>();
        var smartSummary = scope.ServiceProvider.GetRequiredService<SmartSummaryService>();

        var nowUtc = DateTimeOffset.UtcNow;
        var startUtc = nowUtc.AddHours(-item.Hours);

        var messages = await store.GetMessagesAsync(item.ChatId, startUtc, nowUtc);

        if (messages.Count == 0)
        {
            await bot.SendMessage(
                chatId: item.ChatId,
                text: $"За последние {item.Hours} часов сообщений не найдено.",
                replyParameters: new ReplyParameters { MessageId = item.ReplyToMessageId },
                cancellationToken: ct);
            return;
        }

        // Generate summary (may take 30-120 seconds)
        var periodText = GetPeriodText(item.Hours);
        var report = await smartSummary.GenerateSmartSummaryAsync(
            item.ChatId, messages, startUtc, nowUtc, periodText, ct);

        // Send result
        try
        {
            await bot.SendMessage(
                chatId: item.ChatId,
                text: report,
                parseMode: ParseMode.Html,
                linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true },
                replyParameters: new ReplyParameters { MessageId = item.ReplyToMessageId },
                cancellationToken: ct);
        }
        catch (Telegram.Bot.Exceptions.ApiRequestException ex) when (ex.Message.Contains("can't parse entities"))
        {
            logger.LogWarning("[BackgroundSummary] HTML parsing failed, sending as plain text");
            var plainText = MyRegex().Replace(report, "");
            await bot.SendMessage(
                chatId: item.ChatId,
                text: plainText,
                linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true },
                replyParameters: new ReplyParameters { MessageId = item.ReplyToMessageId },
                cancellationToken: ct);
        }

        sw.Stop();
        logCollector.IncrementSummaries();

        logger.LogInformation("[BackgroundSummary] Summary sent to chat {ChatId} ({MessageCount} messages) in {Elapsed:F1}s",
            item.ChatId, messages.Count, sw.Elapsed.TotalSeconds);
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

    [System.Text.RegularExpressions.GeneratedRegex("<[^>]+>")]
    private static partial System.Text.RegularExpressions.Regex MyRegex();
}
