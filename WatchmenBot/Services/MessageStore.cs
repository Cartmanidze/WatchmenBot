using System.Text.RegularExpressions;
using Dapper;
using WatchmenBot.Infrastructure.Database;
using WatchmenBot.Models;

namespace WatchmenBot.Services;

public class MessageStore
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<MessageStore> _logger;

    public MessageStore(IDbConnectionFactory connectionFactory, ILogger<MessageStore> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task SaveAsync(MessageRecord record)
    {
        const string sql = """
            INSERT INTO messages (id, chat_id, thread_id, from_user_id, username, display_name, text, date_utc, has_links, has_media, reply_to_message_id, message_type)
            VALUES (@Id, @ChatId, @ThreadId, @FromUserId, @Username, @DisplayName, @Text, @DateUtc, @HasLinks, @HasMedia, @ReplyToMessageId, @MessageType)
            ON CONFLICT (chat_id, id) DO UPDATE SET
                username = EXCLUDED.username,
                display_name = EXCLUDED.display_name,
                text = EXCLUDED.text,
                has_links = EXCLUDED.has_links,
                has_media = EXCLUDED.has_media,
                reply_to_message_id = EXCLUDED.reply_to_message_id,
                message_type = EXCLUDED.message_type;
            """;

        try
        {
            using var connection = await _connectionFactory.CreateConnectionAsync();
            await connection.ExecuteAsync(sql, record);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save message {MessageId} for chat {ChatId}", record.Id, record.ChatId);
            throw;
        }
    }

    public async Task<List<MessageRecord>> GetMessagesAsync(long chatId, DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        const string sql = """
            SELECT id, chat_id, thread_id, from_user_id, username, display_name, text, date_utc, has_links, has_media, reply_to_message_id, message_type
            FROM messages 
            WHERE chat_id = @ChatId AND date_utc >= @StartUtc AND date_utc < @EndUtc
            ORDER BY date_utc;
            """;

        try
        {
            using var connection = await _connectionFactory.CreateConnectionAsync();
            var results = await connection.QueryAsync<MessageRecord>(sql, new { ChatId = chatId, StartUtc = startUtc, EndUtc = endUtc });
            return results.ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get messages for chat {ChatId} between {StartUtc} and {EndUtc}", chatId, startUtc, endUtc);
            throw;
        }
    }

    public async Task<List<long>> GetDistinctChatIdsAsync()
    {
        const string sql = "SELECT DISTINCT chat_id FROM messages;";

        try
        {
            using var connection = await _connectionFactory.CreateConnectionAsync();
            var results = await connection.QueryAsync<long>(sql);
            return results.ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get distinct chat IDs");
            throw;
        }
    }

    public static bool DetectLinks(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var pattern = @"https?://\S+";
        return Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Get messages that don't have embeddings yet (for background indexing)
    /// </summary>
    public async Task<List<MessageRecord>> GetMessagesWithoutEmbeddingsAsync(int limit = 100)
    {
        const string sql = """
            SELECT m.id, m.chat_id, m.thread_id, m.from_user_id, m.username, m.display_name,
                   m.text, m.date_utc, m.has_links, m.has_media, m.reply_to_message_id, m.message_type
            FROM messages m
            LEFT JOIN message_embeddings e ON m.chat_id = e.chat_id AND m.id = e.message_id
            WHERE e.id IS NULL
              AND m.text IS NOT NULL
              AND LENGTH(m.text) > 5
            ORDER BY m.date_utc DESC
            LIMIT @Limit;
            """;

        try
        {
            using var connection = await _connectionFactory.CreateConnectionAsync();
            var results = await connection.QueryAsync<MessageRecord>(sql, new { Limit = limit });
            return results.ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get messages without embeddings");
            throw;
        }
    }

    /// <summary>
    /// Get count of messages pending embedding indexing
    /// </summary>
    public async Task<(long total, long indexed, long pending)> GetEmbeddingStatsAsync()
    {
        const string sql = """
            SELECT
                (SELECT COUNT(*) FROM messages WHERE text IS NOT NULL AND LENGTH(text) > 5) as total,
                (SELECT COUNT(DISTINCT (chat_id, message_id)) FROM message_embeddings) as indexed;
            """;

        try
        {
            using var connection = await _connectionFactory.CreateConnectionAsync();
            var result = await connection.QuerySingleAsync<(long total, long indexed)>(sql);
            return (result.total, result.indexed, result.total - result.indexed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get embedding stats");
            return (0, 0, 0);
        }
    }
}