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
}

/// <summary>
/// PostgreSQL-backed queue service for background summary generation.
/// Persists requests to survive restarts and ensures reliable delivery.
/// </summary>
public class SummaryQueueService(
    IDbConnectionFactory connectionFactory,
    ILogger<SummaryQueueService> logger)
{
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
    /// Get pending requests from queue (for background worker)
    /// </summary>
    public async Task<List<SummaryQueueItem>> GetPendingAsync(int limit = 10)
    {
        try
        {
            using var connection = await connectionFactory.CreateConnectionAsync();

            var items = await connection.QueryAsync<SummaryQueueItem>("""
                SELECT
                    id as Id,
                    chat_id as ChatId,
                    reply_to_message_id as ReplyToMessageId,
                    hours as Hours,
                    requested_by as RequestedBy,
                    created_at as RequestedAt
                FROM summary_queue
                WHERE processed = FALSE
                ORDER BY created_at
                LIMIT @Limit
                """,
                new { Limit = limit });

            return items.ToList();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[SummaryQueue] Failed to get pending requests");
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
                UPDATE summary_queue SET started_at = NOW() WHERE id = @Id
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
