using Telegram.Bot;
using Telegram.Bot.Types;
using WatchmenBot.Infrastructure.Database;

namespace WatchmenBot.Features.Search.Services;

/// <summary>
/// Background worker for /ask and /smart commands.
/// Uses PostgreSQL LISTEN/NOTIFY for instant notifications with polling fallback.
/// Delegates actual processing to AskProcessingService.
/// </summary>
public class BackgroundAskWorker(
    AskQueueService queue,
    PostgresNotificationService notifications,
    ITelegramBotClient bot,
    IServiceProvider serviceProvider,
    ILogger<BackgroundAskWorker> logger)
    : BackgroundService
{
    private static readonly TimeSpan NotificationTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(6);
    private DateTime _lastCleanup = DateTime.UtcNow;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("[BackgroundAsk] Worker started (LISTEN/NOTIFY + polling fallback)");

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
                        await ProcessAskRequestAsync(item, stoppingToken);
                        await queue.MarkAsCompletedAsync(item.Id);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "[BackgroundAsk] Failed to process /{Command} for chat {ChatId}",
                            item.Command, item.ChatId);

                        await queue.MarkAsFailedAsync(item.Id, ex.Message);

                        try
                        {
                            await bot.SendMessage(
                                chatId: item.ChatId,
                                text: "Произошла ошибка при обработке вопроса. Попробуйте позже.",
                                replyParameters: new ReplyParameters { MessageId = item.ReplyToMessageId },
                                cancellationToken: stoppingToken);
                        }
                        catch (Exception sendEx)
                        {
                            logger.LogWarning(sendEx, "[BackgroundAsk] Failed to send error notification to chat {ChatId}", item.ChatId);
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
                logger.LogError(ex, "[BackgroundAsk] Error in worker loop");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }

        logger.LogInformation("[BackgroundAsk] Worker stopped");
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
            await notifications.AskQueueNotifications.WaitToReadAsync(timeoutCts.Token);

            // Drain all pending notifications (we'll fetch from DB anyway)
            while (notifications.AskQueueNotifications.TryRead(out var itemId))
            {
                logger.LogDebug("[BackgroundAsk] Received notification for item {ItemId}", itemId);
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

    /// <summary>
    /// Process /ask request by delegating to AskProcessingService.
    /// </summary>
    private async Task ProcessAskRequestAsync(AskQueueItem item, CancellationToken ct)
    {
        logger.LogInformation("[BackgroundAsk] Processing /{Command}: {Question} from @{User}",
            item.Command, item.Question.Length > 50 ? item.Question[..50] + "..." : item.Question,
            item.AskerUsername ?? item.AskerName);

        // Create scoped service to process request
        using var scope = serviceProvider.CreateScope();
        var processingService = scope.ServiceProvider.GetRequiredService<AskProcessingService>();

        // Delegate all processing to AskProcessingService
        var result = await processingService.ProcessAsync(item, ct);

        logger.LogInformation("[BackgroundAsk] /{Command} completed in {Elapsed:F1}s (confidence: {Conf}, success: {Success})",
            item.Command, result.ElapsedSeconds, result.Confidence, result.Success);
    }
}