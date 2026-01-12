using Telegram.Bot;
using Telegram.Bot.Types;
using WatchmenBot.Infrastructure.Database;
using WatchmenBot.Infrastructure.Queue;

namespace WatchmenBot.Features.Search.Services;

/// <summary>
/// Background worker for /ask and /smart commands.
/// Uses PostgreSQL LISTEN/NOTIFY for instant notifications with polling fallback.
/// Delegates actual processing to AskProcessingService.
///
/// ★ Insight ─────────────────────────────────────
/// Uses ResilientQueueService for:
/// - Atomic task picking with lease (prevents duplicates)
/// - Automatic retry with exponential backoff
/// - Stale task recovery (worker crash protection)
/// ─────────────────────────────────────────────────
/// </summary>
public class BackgroundAskWorker(
    ResilientQueueService resilientQueue,
    PostgresNotificationService notifications,
    ITelegramBotClient bot,
    IServiceProvider serviceProvider,
    ILogger<BackgroundAskWorker> logger)
    : BackgroundService
{
    private static readonly TimeSpan NotificationTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan StaleCheckInterval = TimeSpan.FromMinutes(1);
    private DateTime _lastCleanup = DateTime.UtcNow;
    private DateTime _lastStaleCheck = DateTime.UtcNow;

    /// <summary>
    /// Queue configuration for ask_queue.
    /// </summary>
    private static readonly QueueConfig AskQueueConfig = new()
    {
        TableName = "ask_queue",
        QueueName = "ask",
        MaxAttempts = 3,
        BaseRetryDelay = TimeSpan.FromSeconds(30),
        MaxRetryDelay = TimeSpan.FromMinutes(5),
        LeaseTimeout = TimeSpan.FromMinutes(5) // Ask processing can take up to 5 min
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("[BackgroundAsk] Worker started with resilient queue (lease + retry)");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Periodically recover stale tasks
                await RecoverStaleTasksAsync();

                // Pick task with atomic lease acquisition (drain pattern)
                var item = await resilientQueue.PickAsync<AskQueueItem>(AskQueueConfig, stoppingToken);

                if (item == null)
                {
                    // Queue empty — wait for notification OR timeout before next check
                    // This enables instant response to new items while avoiding busy-loop
                    await WaitForNotificationOrTimeoutAsync(stoppingToken);
                    await PeriodicCleanupAsync();
                    continue;
                }

                // Process with retry-aware error handling
                try
                {
                    await ProcessAskRequestAsync(item, stoppingToken);
                    await resilientQueue.CompleteAsync(AskQueueConfig, item.Id, item.CreatedAt);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "[BackgroundAsk] Failed /{Command} (attempt {Attempt}) for chat {ChatId}",
                        item.Command, item.AttemptCount, item.ChatId);

                    var willRetry = await resilientQueue.FailAsync(
                        AskQueueConfig,
                        item.Id,
                        item.AttemptCount,
                        $"{ex.GetType().Name}: {ex.Message}");

                    // Only notify user on final failure
                    if (!willRetry)
                    {
                        await SendErrorNotificationAsync(item, stoppingToken);
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
    /// Recover tasks that were picked but never completed (worker crash scenario).
    /// </summary>
    private async Task RecoverStaleTasksAsync()
    {
        if (DateTime.UtcNow - _lastStaleCheck < StaleCheckInterval)
            return;

        _lastStaleCheck = DateTime.UtcNow;
        await resilientQueue.RecoverStaleTasksAsync(AskQueueConfig);
    }

    /// <summary>
    /// Send error notification to user on final failure.
    /// </summary>
    private async Task SendErrorNotificationAsync(AskQueueItem item, CancellationToken ct)
    {
        try
        {
            await bot.SendMessage(
                chatId: item.ChatId,
                text: "⚠️ Не удалось обработать вопрос после нескольких попыток. Пожалуйста, попробуйте позже.",
                replyParameters: new ReplyParameters { MessageId = item.ReplyToMessageId },
                cancellationToken: ct);
        }
        catch (Exception sendEx)
        {
            logger.LogWarning(sendEx, "[BackgroundAsk] Failed to send error notification to chat {ChatId}", item.ChatId);
        }
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
            await resilientQueue.CleanupAsync(AskQueueConfig, daysToKeep: 7);
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