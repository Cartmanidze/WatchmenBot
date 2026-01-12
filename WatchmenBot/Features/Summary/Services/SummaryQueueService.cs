using Dapper;
using Telegram.Bot.Types;
using WatchmenBot.Infrastructure.Database;

namespace WatchmenBot.Features.Summary.Services;

/// <summary>
/// Request for background summary generation
/// </summary>
public class SummaryQueueItem
{
    public int Id { get; init; }
    public required long ChatId { get; init; }
    public required int ReplyToMessageId { get; init; }
    public required int Hours { get; init; }
    public required string RequestedBy { get; init; }
    public DateTimeOffset RequestedAt { get; init; } = DateTimeOffset.UtcNow;
    public int AttemptCount { get; init; }
}

/// <summary>
/// PostgreSQL-backed queue service for background summary generation.
/// Persists requests to survive restarts and ensures reliable delivery.
/// </summary>
public class SummaryQueueService(
    IDbConnectionFactory connectionFactory,
    ILogger<SummaryQueueService> logger)
{
    /// <summary>Maximum retry attempts before marking task as dead.</summary>
    public const int MaxAttempts = 3;

    /// <summary>How long a task can be "in progress" before considered stale.
    /// Set to 10 minutes to accommodate long-running summary generation.</summary>
    public static readonly TimeSpan LeaseTimeout = TimeSpan.FromMinutes(10);
    /// <summary>
    /// Enqueue summary request for background processing (persisted to DB)
    /// </summary>
    public async Task<bool> EnqueueAsync(SummaryQueueItem item)
    {
        try
        {
            using var connection = await connectionFactory.CreateConnectionAsync();

            await connection.ExecuteAsync("""
                INSERT INTO summary_queue (chat_id, reply_to_message_id, hours, requested_by)
                VALUES (@ChatId, @ReplyToMessageId, @Hours, @RequestedBy)
                """,
                new
                {
                    item.ChatId,
                    item.ReplyToMessageId,
                    item.Hours,
                    item.RequestedBy
                });

            logger.LogInformation("[SummaryQueue] Enqueued summary request for chat {ChatId}, {Hours}h, by @{User}",
                item.ChatId, item.Hours, item.RequestedBy);

            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[SummaryQueue] Failed to enqueue request");
            return false;
        }
    }

    /// <summary>
    /// Enqueue from Telegram Message
    /// </summary>
    public Task<bool> EnqueueFromMessageAsync(Message message, int hours)
    {
        var userName = message.From?.Username ?? message.From?.FirstName ?? "unknown";

        return EnqueueAsync(new SummaryQueueItem
        {
            ChatId = message.Chat.Id,
            ReplyToMessageId = message.MessageId,
            Hours = hours,
            RequestedBy = userName
        });
    }

    /// <summary>
    /// Atomically pick next pending task from queue.
    /// Single UPDATE...RETURNING ensures no race conditions between concurrent workers.
    /// </summary>
    public async Task<SummaryQueueItem?> PickNextAsync()
    {
        try
        {
            using var connection = await connectionFactory.CreateConnectionAsync();

            // Atomic pick: find ready task, set started_at, increment attempt
            // FOR UPDATE SKIP LOCKED inside subquery ensures lock until UPDATE completes
            var item = await connection.QueryFirstOrDefaultAsync<SummaryQueueItem>("""
                UPDATE summary_queue
                SET started_at = NOW(),
                    picked_at = NOW(),
                    attempt_count = attempt_count + 1
                WHERE id = (
                    SELECT id FROM summary_queue
                    WHERE processed = FALSE
                      AND (started_at IS NULL OR started_at < NOW() - @LeaseTimeout)
                      AND attempt_count < @MaxAttempts
                    ORDER BY created_at
                    LIMIT 1
                    FOR UPDATE SKIP LOCKED
                )
                RETURNING
                    id as Id,
                    chat_id as ChatId,
                    reply_to_message_id as ReplyToMessageId,
                    hours as Hours,
                    requested_by as RequestedBy,
                    created_at as RequestedAt,
                    attempt_count as AttemptCount
                """,
                new { LeaseTimeout, MaxAttempts });

            if (item != null)
            {
                logger.LogDebug("[SummaryQueue] Picked task {Id} for chat {ChatId}", item.Id, item.ChatId);
            }

            return item;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[SummaryQueue] Failed to pick next task");
            return null;
        }
    }

    /// <summary>
    /// Recover stale tasks that were started but never completed (worker crash).
    /// Uses LeaseTimeout and MaxAttempts constants.
    /// </summary>
    public async Task<int> RecoverStaleTasksAsync()
    {
        try
        {
            using var connection = await connectionFactory.CreateConnectionAsync();

            // Reset tasks that have been "in progress" for too long (but still have attempts left)
            var recovered = await connection.ExecuteAsync("""
                UPDATE summary_queue
                SET started_at = NULL, picked_at = NULL
                WHERE processed = FALSE
                  AND started_at IS NOT NULL
                  AND started_at < NOW() - @LeaseTimeout
                  AND attempt_count < @MaxAttempts
                """,
                new { LeaseTimeout, MaxAttempts });

            if (recovered > 0)
            {
                logger.LogWarning("[SummaryQueue] Recovered {Count} stale tasks", recovered);
            }

            // Mark exhausted tasks as dead (too many attempts)
            var dead = await connection.ExecuteAsync("""
                UPDATE summary_queue
                SET processed = TRUE,
                    completed_at = NOW(),
                    error = '[DEAD] Max attempts exceeded after worker crash'
                WHERE processed = FALSE
                  AND started_at IS NOT NULL
                  AND started_at < NOW() - @LeaseTimeout
                  AND attempt_count >= @MaxAttempts
                """,
                new { LeaseTimeout, MaxAttempts });

            if (dead > 0)
            {
                logger.LogError("[SummaryQueue] Marked {Count} tasks as DEAD (max attempts)", dead);
            }

            return recovered;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[SummaryQueue] Failed to recover stale tasks");
            return 0;
        }
    }

    /// <summary>
    /// [DEPRECATED] Use PickNextAsync instead for atomic picking.
    /// </summary>
    [Obsolete("Use PickNextAsync for atomic task picking")]
    public async Task MarkAsStartedAsync(int id)
    {
        try
        {
            using var connection = await connectionFactory.CreateConnectionAsync();

            await connection.ExecuteAsync("""
                UPDATE summary_queue
                SET started_at = NOW(),
                    picked_at = NOW(),
                    attempt_count = attempt_count + 1
                WHERE id = @Id
                """,
                new { Id = id });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[SummaryQueue] Failed to mark request {Id} as started", id);
        }
    }

    /// <summary>
    /// Mark request as completed
    /// </summary>
    public async Task MarkAsCompletedAsync(int id)
    {
        try
        {
            using var connection = await connectionFactory.CreateConnectionAsync();

            await connection.ExecuteAsync("""
                UPDATE summary_queue
                SET processed = TRUE, completed_at = NOW()
                WHERE id = @Id
                """,
                new { Id = id });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[SummaryQueue] Failed to mark request {Id} as completed", id);
        }
    }

    /// <summary>
    /// Mark request as failed with error message
    /// </summary>
    public async Task MarkAsFailedAsync(int id, string error)
    {
        try
        {
            using var connection = await connectionFactory.CreateConnectionAsync();

            await connection.ExecuteAsync("""
                UPDATE summary_queue
                SET processed = TRUE, completed_at = NOW(), error = @Error
                WHERE id = @Id
                """,
                new { Id = id, Error = error });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[SummaryQueue] Failed to mark request {Id} as failed", id);
        }
    }

    /// <summary>
    /// Cleanup old processed requests (call periodically)
    /// </summary>
    public async Task CleanupOldAsync(int daysToKeep = 7)
    {
        try
        {
            using var connection = await connectionFactory.CreateConnectionAsync();

            var deleted = await connection.ExecuteAsync("""
                DELETE FROM summary_queue
                WHERE processed = TRUE AND created_at < NOW() - @Days * INTERVAL '1 day'
                """,
                new { Days = daysToKeep });

            if (deleted > 0)
            {
                logger.LogInformation("[SummaryQueue] Cleaned up {Count} old processed requests", deleted);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[SummaryQueue] Failed to cleanup old requests");
        }
    }
}
