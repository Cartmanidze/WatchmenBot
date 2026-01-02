using Dapper;
using System.Text.Json;
using WatchmenBot.Infrastructure.Database;

namespace WatchmenBot.Services.Memory;

/// <summary>
/// Service for managing conversation memory storage and retrieval
/// </summary>
public class ConversationMemoryService(
    IDbConnectionFactory connectionFactory,
    ILogger<ConversationMemoryService> logger)
{
    private const int MaxRecentMemories = 5;

    /// <summary>
    /// Get recent conversation memory for user
    /// </summary>
    public async Task<List<ConversationMemory>> GetRecentMemoriesAsync(
        long chatId, long userId, int limit = MaxRecentMemories, CancellationToken ct = default)
    {
        try
        {
            using var connection = await connectionFactory.CreateConnectionAsync();

            var records = await connection.QueryAsync<ConversationMemoryRecord>(
                """
                SELECT id, user_id, chat_id, query, response_summary, topics, extracted_facts, created_at
                FROM conversation_memory
                WHERE chat_id = @ChatId AND user_id = @UserId
                ORDER BY created_at DESC
                LIMIT @Limit
                """,
                new { ChatId = chatId, UserId = userId, Limit = limit });

            return records.Select(r => new ConversationMemory
            {
                Id = r.id,
                UserId = r.user_id,
                ChatId = r.chat_id,
                Query = r.query,
                ResponseSummary = r.response_summary,
                Topics = MemoryHelpers.ParseJsonArray(r.topics),
                ExtractedFacts = MemoryHelpers.ParseJsonArray(r.extracted_facts),
                CreatedAt = new DateTimeOffset(r.created_at, TimeSpan.Zero)
            }).ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Memory] Failed to get memories for user {UserId}", userId);
            return [];
        }
    }

    /// <summary>
    /// Store new conversation memory with summary
    /// </summary>
    public async Task StoreMemoryAsync(
        long chatId, long userId, string query, MemorySummary summary, CancellationToken ct = default)
    {
        try
        {
            using var connection = await connectionFactory.CreateConnectionAsync();

            await connection.ExecuteAsync(
                """
                INSERT INTO conversation_memory (user_id, chat_id, query, response_summary, topics, extracted_facts)
                VALUES (@UserId, @ChatId, @Query, @Summary, @Topics::jsonb, @Facts::jsonb)
                """,
                new
                {
                    UserId = userId,
                    ChatId = chatId,
                    Query = MemoryHelpers.TruncateText(query, 500),
                    Summary = summary.Summary,
                    Topics = JsonSerializer.Serialize(summary.Topics),
                    Facts = JsonSerializer.Serialize(summary.Facts)
                });

            // Cleanup old memories (keep last 20)
            await connection.ExecuteAsync(
                """
                DELETE FROM conversation_memory
                WHERE chat_id = @ChatId AND user_id = @UserId
                AND id NOT IN (
                    SELECT id FROM conversation_memory
                    WHERE chat_id = @ChatId AND user_id = @UserId
                    ORDER BY created_at DESC
                    LIMIT 20
                )
                """,
                new { ChatId = chatId, UserId = userId });

            logger.LogDebug("[Memory] Stored memory for user {UserId}: {Topics}",
                userId, string.Join(", ", summary.Topics));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Memory] Failed to store memory for user {UserId}", userId);
        }
    }
}
