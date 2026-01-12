using Dapper;
using Telegram.Bot.Types;
using WatchmenBot.Infrastructure.Database;
using WatchmenBot.Infrastructure.Queue;

namespace WatchmenBot.Features.Search.Services;

/// <summary>
/// Request for background /ask processing.
/// Implements IQueueItem for resilient queue processing.
/// </summary>
public class AskQueueItem : IQueueItem
{
    public int Id { get; init; }
    public required long ChatId { get; init; }
    public required int ReplyToMessageId { get; init; }
    public required string Question { get; init; }
    public required string Command { get; init; } // "ask" or "smart"
    public required long AskerId { get; init; }
    public required string AskerName { get; init; }
    public string? AskerUsername { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    // IQueueItem implementation for retry support
    public int AttemptCount { get; init; }
    public string? Error { get; init; }

    /// <summary>
    /// Generate idempotency key for deduplication.
    /// Based on chat + message + command to prevent duplicate processing.
    /// </summary>
    public string GenerateIdempotencyKey() =>
        $"{ChatId}:{ReplyToMessageId}:{Command}";
}

/// <summary>
/// PostgreSQL-backed queue service for background /ask processing.
/// Persists requests to survive restarts and ensures reliable delivery.
/// </summary>
public class AskQueueService(
    IDbConnectionFactory connectionFactory,
    ILogger<AskQueueService> logger)
{
    /// <summary>
    /// Enqueue /ask request for background processing (persisted to DB).
    /// Uses idempotency key to prevent duplicate processing of the same request.
    /// </summary>
    public async Task<bool> EnqueueAsync(AskQueueItem item)
    {
        try
        {
            using var connection = await connectionFactory.CreateConnectionAsync();
            var idempotencyKey = item.GenerateIdempotencyKey();

            // Use ON CONFLICT to handle idempotency:
            // - If same request exists and not processed, skip (already queued)
            // - If same request was processed, allow new one (user retrying)
            var inserted = await connection.ExecuteAsync("""
                INSERT INTO ask_queue (chat_id, reply_to_message_id, question, command, asker_id, asker_name, asker_username, idempotency_key)
                VALUES (@ChatId, @ReplyToMessageId, @Question, @Command, @AskerId, @AskerName, @AskerUsername, @IdempotencyKey)
                ON CONFLICT (idempotency_key) WHERE processed = FALSE DO NOTHING
                """,
                new
                {
                    item.ChatId,
                    item.ReplyToMessageId,
                    item.Question,
                    item.Command,
                    item.AskerId,
                    item.AskerName,
                    item.AskerUsername,
                    IdempotencyKey = idempotencyKey
                });

            if (inserted > 0)
            {
                logger.LogInformation("[AskQueue] Enqueued /{Command} from @{User}: {Question}",
                    item.Command, item.AskerUsername ?? item.AskerName,
                    item.Question.Length > 50 ? item.Question[..50] + "..." : item.Question);
            }
            else
            {
                logger.LogDebug("[AskQueue] Skipped duplicate /{Command} (already queued)", item.Command);
            }

            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[AskQueue] Failed to enqueue request");
            return false;
        }
    }

    /// <summary>
    /// Enqueue from Telegram Message
    /// </summary>
    public Task<bool> EnqueueFromMessageAsync(Message message, string command, string question)
    {
        var askerName = message.From?.FirstName ?? message.From?.Username ?? "Unknown";
        var askerUsername = message.From?.Username;
        var askerId = message.From?.Id ?? 0;

        return EnqueueAsync(new AskQueueItem
        {
            ChatId = message.Chat.Id,
            ReplyToMessageId = message.MessageId,
            Question = question,
            Command = command,
            AskerId = askerId,
            AskerName = askerName,
            AskerUsername = askerUsername
        });
    }

    /// <summary>
    /// Get pending requests from queue (for background worker).
    /// Note: BackgroundAskWorker now uses ResilientQueueService.PickAsync instead.
    /// This method is kept for backwards compatibility and testing.
    /// </summary>
    public async Task<List<AskQueueItem>> GetPendingAsync(int limit = 10)
    {
        try
        {
            using var connection = await connectionFactory.CreateConnectionAsync();

            var items = await connection.QueryAsync<AskQueueItem>("""
                SELECT
                    id as Id,
                    chat_id as ChatId,
                    reply_to_message_id as ReplyToMessageId,
                    question as Question,
                    command as Command,
                    asker_id as AskerId,
                    asker_name as AskerName,
                    asker_username as AskerUsername,
                    created_at as CreatedAt,
                    attempt_count as AttemptCount,
                    error as Error
                FROM ask_queue
                WHERE processed = FALSE
                ORDER BY created_at
                LIMIT @Limit
                """,
                new { Limit = limit });

            return items.ToList();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[AskQueue] Failed to get pending requests");
            return [];
        }
    }

    /// <summary>
    /// Mark request as started (prevents duplicate processing)
    /// </summary>
    public async Task MarkAsStartedAsync(int id)
    {
        try
        {
            using var connection = await connectionFactory.CreateConnectionAsync();

            await connection.ExecuteAsync("""
                UPDATE ask_queue SET started_at = NOW() WHERE id = @Id
                """,
                new { Id = id });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[AskQueue] Failed to mark request {Id} as started", id);
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
                UPDATE ask_queue
                SET processed = TRUE, completed_at = NOW()
                WHERE id = @Id
                """,
                new { Id = id });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[AskQueue] Failed to mark request {Id} as completed", id);
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
                UPDATE ask_queue
                SET processed = TRUE, completed_at = NOW(), error = @Error
                WHERE id = @Id
                """,
                new { Id = id, Error = error });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[AskQueue] Failed to mark request {Id} as failed", id);
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
                DELETE FROM ask_queue
                WHERE processed = TRUE AND created_at < NOW() - @Days * INTERVAL '1 day'
                """,
                new { Days = daysToKeep });

            if (deleted > 0)
            {
                logger.LogInformation("[AskQueue] Cleaned up {Count} old processed requests", deleted);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[AskQueue] Failed to cleanup old requests");
        }
    }

    /// <summary>
    /// Get approximate queue size
    /// </summary>
    public async Task<int> GetPendingCountAsync()
    {
        try
        {
            using var connection = await connectionFactory.CreateConnectionAsync();

            return await connection.ExecuteScalarAsync<int>("""
                SELECT COUNT(*) FROM ask_queue WHERE processed = FALSE
                """);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[AskQueue] Failed to get pending count");
            return 0;
        }
    }
}