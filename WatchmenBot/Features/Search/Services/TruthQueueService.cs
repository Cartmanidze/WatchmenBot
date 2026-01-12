using Dapper;
using Telegram.Bot.Types;
using WatchmenBot.Infrastructure.Database;

namespace WatchmenBot.Features.Search.Services;

/// <summary>
/// Queue item for /truth fact-checking
/// </summary>
public class TruthQueueItem
{
    public int Id { get; init; }
    public required long ChatId { get; init; }
    public required int ReplyToMessageId { get; init; }
    public int MessageCount { get; init; } = 5;
    public required string RequestedBy { get; init; }
    public DateTimeOffset RequestedAt { get; init; } = DateTimeOffset.UtcNow;
    public int AttemptCount { get; init; }
}

/// <summary>
/// PostgreSQL-backed queue service for background /truth processing.
/// Persists requests to survive restarts and ensures reliable delivery.
/// </summary>
public class TruthQueueService(
    IDbConnectionFactory connectionFactory,
    ILogger<TruthQueueService> logger)
{
    /// <summary>Maximum retry attempts before marking task as dead.</summary>
    public const int MaxAttempts = 3;

    /// <summary>How long a task can be "in progress" before considered stale.
    /// Set to 10 minutes to accommodate long-running fact-checking.</summary>
    public static readonly TimeSpan LeaseTimeout = TimeSpan.FromMinutes(10);
    /// <summary>
    /// Enqueue /truth request for background processing (persisted to DB)
    /// </summary>
    public async Task<bool> EnqueueAsync(TruthQueueItem item)
    {
        try
        {
            using var connection = await connectionFactory.CreateConnectionAsync();

            await connection.ExecuteAsync("""
                INSERT INTO truth_queue (chat_id, reply_to_message_id, message_count, requested_by)
                VALUES (@ChatId, @ReplyToMessageId, @MessageCount, @RequestedBy)
                """,
                new
                {
                    item.ChatId,
                    item.ReplyToMessageId,
                    item.MessageCount,
                    item.RequestedBy
                });

            logger.LogInformation("[TruthQueue] Enqueued fact-check for chat {ChatId}, {Count} messages",
                item.ChatId, item.MessageCount);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[TruthQueue] Failed to enqueue fact-check for chat {ChatId}", item.ChatId);
            return false;
        }
    }

    /// <summary>
    /// Convenience method to enqueue from Telegram message
    /// </summary>
    public async Task<bool> EnqueueFromMessageAsync(Message message, int messageCount)
    {
        var requester = message.From?.Username ?? message.From?.FirstName ?? "unknown";

        return await EnqueueAsync(new TruthQueueItem
        {
            ChatId = message.Chat.Id,
            ReplyToMessageId = message.MessageId,
            MessageCount = messageCount,
            RequestedBy = requester
        });
    }

    /// <summary>
    /// Atomically pick next pending task from queue.
    /// Single UPDATE...RETURNING ensures no race conditions between concurrent workers.
    /// </summary>
    public async Task<TruthQueueItem?> PickNextAsync()
    {
        try
        {
            using var connection = await connectionFactory.CreateConnectionAsync();

            // Atomic pick: find ready task, set started_at, increment attempt
            // FOR UPDATE SKIP LOCKED inside subquery ensures lock until UPDATE completes
            var item = await connection.QueryFirstOrDefaultAsync<TruthQueueItem>("""
                UPDATE truth_queue
                SET started_at = NOW(),
                    picked_at = NOW(),
                    attempt_count = attempt_count + 1
                WHERE id = (
                    SELECT id FROM truth_queue
                    WHERE processed = FALSE
                      AND (started_at IS NULL OR started_at < NOW() - @LeaseTimeout)
                      AND attempt_count < @MaxAttempts
                    ORDER BY created_at
                    LIMIT 1
                    FOR UPDATE SKIP LOCKED
                )
                RETURNING
                    id AS Id,
                    chat_id AS ChatId,
                    reply_to_message_id AS ReplyToMessageId,
                    message_count AS MessageCount,
                    requested_by AS RequestedBy,
                    created_at AS RequestedAt,
                    attempt_count AS AttemptCount
                """,
                new { LeaseTimeout, MaxAttempts });

            if (item != null)
            {
                logger.LogDebug("[TruthQueue] Picked task {Id} for chat {ChatId}", item.Id, item.ChatId);
            }

            return item;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[TruthQueue] Failed to pick next task");
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
                UPDATE truth_queue
                SET started_at = NULL, picked_at = NULL
                WHERE processed = FALSE
                  AND started_at IS NOT NULL
                  AND started_at < NOW() - @LeaseTimeout
                  AND attempt_count < @MaxAttempts
                """,
                new { LeaseTimeout, MaxAttempts });

            if (recovered > 0)
            {
                logger.LogWarning("[TruthQueue] Recovered {Count} stale tasks", recovered);
            }

            // Mark exhausted tasks as dead (too many attempts)
            var dead = await connection.ExecuteAsync("""
                UPDATE truth_queue
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
                logger.LogError("[TruthQueue] Marked {Count} tasks as DEAD (max attempts)", dead);
            }

            return recovered;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[TruthQueue] Failed to recover stale tasks");
            return 0;
        }
    }

    /// <summary>
    /// [DEPRECATED] Use PickNextAsync instead for atomic picking.
    /// </summary>
    [Obsolete("Use PickNextAsync for atomic task picking")]
    public async Task MarkAsStartedAsync(int id)
    {
        using var connection = await connectionFactory.CreateConnectionAsync();

        await connection.ExecuteAsync("""
            UPDATE truth_queue
            SET started_at = NOW(),
                picked_at = NOW(),
                attempt_count = attempt_count + 1
            WHERE id = @Id
            """,
            new { Id = id });
    }

    /// <summary>
    /// Mark item as completed successfully
    /// </summary>
    public async Task MarkAsCompletedAsync(int id)
    {
        using var connection = await connectionFactory.CreateConnectionAsync();

        await connection.ExecuteAsync("""
            UPDATE truth_queue SET processed = TRUE, completed_at = NOW() WHERE id = @Id
            """,
            new { Id = id });
    }

    /// <summary>
    /// Mark item as failed with error message
    /// </summary>
    public async Task MarkAsFailedAsync(int id, string error)
    {
        using var connection = await connectionFactory.CreateConnectionAsync();

        await connection.ExecuteAsync("""
            UPDATE truth_queue SET processed = TRUE, completed_at = NOW(), error = @Error WHERE id = @Id
            """,
            new { Id = id, Error = error.Length > 500 ? error[..500] : error });
    }

    /// <summary>
    /// Cleanup old completed items
    /// </summary>
    public async Task CleanupOldAsync(int daysToKeep = 7)
    {
        using var connection = await connectionFactory.CreateConnectionAsync();

        var deleted = await connection.ExecuteAsync("""
            DELETE FROM truth_queue
            WHERE processed = TRUE AND created_at < NOW() - INTERVAL '@Days days'
            """.Replace("@Days", daysToKeep.ToString()));

        if (deleted > 0)
        {
            logger.LogInformation("[TruthQueue] Cleaned up {Count} old items", deleted);
        }
    }
}
