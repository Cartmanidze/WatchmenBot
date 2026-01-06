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
}

/// <summary>
/// PostgreSQL-backed queue service for background /truth processing.
/// Persists requests to survive restarts and ensures reliable delivery.
/// </summary>
public class TruthQueueService(
    IDbConnectionFactory connectionFactory,
    ILogger<TruthQueueService> logger)
{
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
    /// Get pending items for processing (not yet started)
    /// </summary>
    public async Task<List<TruthQueueItem>> GetPendingAsync(int limit = 5)
    {
        using var connection = await connectionFactory.CreateConnectionAsync();

        var items = await connection.QueryAsync<TruthQueueItem>("""
            SELECT id, chat_id AS ChatId, reply_to_message_id AS ReplyToMessageId,
                   message_count AS MessageCount, requested_by AS RequestedBy, created_at AS RequestedAt
            FROM truth_queue
            WHERE processed = FALSE AND started_at IS NULL
            ORDER BY created_at
            LIMIT @Limit
            """,
            new { Limit = limit });

        return items.ToList();
    }

    /// <summary>
    /// Mark item as started (in progress)
    /// </summary>
    public async Task MarkAsStartedAsync(int id)
    {
        using var connection = await connectionFactory.CreateConnectionAsync();

        await connection.ExecuteAsync("""
            UPDATE truth_queue SET started_at = NOW() WHERE id = @Id
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
