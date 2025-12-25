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

    public async Task SaveChatAsync(long chatId, string? title, string chatType)
    {
        const string sql = """
            INSERT INTO chats (id, title, type, updated_at)
            VALUES (@Id, @Title, @Type, NOW())
            ON CONFLICT (id) DO UPDATE SET
                title = COALESCE(EXCLUDED.title, chats.title),
                type = EXCLUDED.type,
                updated_at = NOW();
            """;

        try
        {
            using var connection = await _connectionFactory.CreateConnectionAsync();
            await connection.ExecuteAsync(sql, new { Id = chatId, Title = title, Type = chatType });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save chat {ChatId}", chatId);
        }
    }

    public async Task<List<MessageRecord>> GetMessagesAsync(long chatId, DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        const string sql = """
            SELECT
                id as Id,
                chat_id as ChatId,
                thread_id as ThreadId,
                from_user_id as FromUserId,
                username as Username,
                display_name as DisplayName,
                text as Text,
                date_utc as DateUtc,
                has_links as HasLinks,
                has_media as HasMedia,
                reply_to_message_id as ReplyToMessageId,
                message_type as MessageType
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

    /// <summary>
    /// Get latest N messages from a chat (excluding bot messages)
    /// </summary>
    public async Task<List<MessageRecord>> GetLatestMessagesAsync(long chatId, int limit = 20)
    {
        const string sql = """
            SELECT
                id as Id,
                chat_id as ChatId,
                thread_id as ThreadId,
                from_user_id as FromUserId,
                username as Username,
                display_name as DisplayName,
                text as Text,
                date_utc as DateUtc,
                has_links as HasLinks,
                has_media as HasMedia,
                reply_to_message_id as ReplyToMessageId,
                message_type as MessageType
            FROM messages
            WHERE chat_id = @ChatId
              AND text IS NOT NULL
              AND text != ''
              AND (username IS NULL OR NOT (username ILIKE '%bot' OR username ILIKE '%_bot'))
            ORDER BY date_utc DESC
            LIMIT @Limit;
            """;

        try
        {
            using var connection = await _connectionFactory.CreateConnectionAsync();
            var results = await connection.QueryAsync<MessageRecord>(sql, new { ChatId = chatId, Limit = limit });
            return results.Reverse().ToList(); // Return in chronological order
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get latest messages for chat {ChatId}", chatId);
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

    public async Task<List<MessageRecord>> GetMessagesByUsernameAsync(long chatId, string username, DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        const string sql = """
            SELECT
                id as Id,
                chat_id as ChatId,
                thread_id as ThreadId,
                from_user_id as FromUserId,
                username as Username,
                display_name as DisplayName,
                text as Text,
                date_utc as DateUtc,
                has_links as HasLinks,
                has_media as HasMedia,
                reply_to_message_id as ReplyToMessageId,
                message_type as MessageType
            FROM messages
            WHERE chat_id = @ChatId
              AND date_utc >= @StartUtc
              AND date_utc < @EndUtc
              AND (LOWER(username) = LOWER(@Username) OR LOWER(display_name) LIKE LOWER(@DisplayNamePattern))
            ORDER BY date_utc DESC;
            """;

        try
        {
            using var connection = await _connectionFactory.CreateConnectionAsync();
            var results = await connection.QueryAsync<MessageRecord>(sql, new
            {
                ChatId = chatId,
                StartUtc = startUtc,
                EndUtc = endUtc,
                Username = username,
                DisplayNamePattern = $"%{username}%"
            });
            return results.ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get messages for username {Username} in chat {ChatId}", username, chatId);
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
            SELECT
                m.id as Id,
                m.chat_id as ChatId,
                m.thread_id as ThreadId,
                m.from_user_id as FromUserId,
                m.username as Username,
                m.display_name as DisplayName,
                m.text as Text,
                m.date_utc as DateUtc,
                m.has_links as HasLinks,
                m.has_media as HasMedia,
                m.reply_to_message_id as ReplyToMessageId,
                m.message_type as MessageType
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

    /// <summary>
    /// Get list of known chats with message counts
    /// </summary>
    public async Task<List<ChatInfo>> GetKnownChatsAsync()
    {
        const string sql = """
            SELECT
                m.chat_id as ChatId,
                c.title as Title,
                c.type as ChatType,
                COUNT(*) as MessageCount,
                MIN(m.date_utc) as FirstMessage,
                MAX(m.date_utc) as LastMessage
            FROM messages m
            LEFT JOIN chats c ON m.chat_id = c.id
            GROUP BY m.chat_id, c.title, c.type
            ORDER BY MAX(m.date_utc) DESC
            """;

        try
        {
            using var connection = await _connectionFactory.CreateConnectionAsync();
            var result = await connection.QueryAsync<ChatInfo>(sql);
            return result.ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get known chats");
            return new List<ChatInfo>();
        }
    }

    /// <summary>
    /// Rename display_name in messages (for fixing imported contact names)
    /// </summary>
    public async Task<int> RenameDisplayNameAsync(long? chatId, string oldName, string newName)
    {
        var sql = chatId.HasValue
            ? """
              UPDATE messages
              SET display_name = @NewName
              WHERE chat_id = @ChatId AND display_name = @OldName
              """
            : """
              UPDATE messages
              SET display_name = @NewName
              WHERE display_name = @OldName
              """;

        try
        {
            using var connection = await _connectionFactory.CreateConnectionAsync();
            var affected = await connection.ExecuteAsync(sql, new { ChatId = chatId, OldName = oldName, NewName = newName });
            _logger.LogInformation("Renamed '{OldName}' to '{NewName}' in {Count} messages (chatId: {ChatId})",
                oldName, newName, affected, chatId?.ToString() ?? "all");
            return affected;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to rename '{OldName}' to '{NewName}'", oldName, newName);
            throw;
        }
    }

    /// <summary>
    /// Get unique display names in a chat
    /// </summary>
    public async Task<List<(string DisplayName, int MessageCount)>> GetUniqueDisplayNamesAsync(long chatId)
    {
        const string sql = """
            SELECT display_name, COUNT(*) as cnt
            FROM messages
            WHERE chat_id = @ChatId AND display_name IS NOT NULL
            GROUP BY display_name
            ORDER BY cnt DESC
            """;

        try
        {
            using var connection = await _connectionFactory.CreateConnectionAsync();
            var results = await connection.QueryAsync<(string DisplayName, int cnt)>(sql, new { ChatId = chatId });
            return results.Select(r => (r.DisplayName, r.cnt)).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get unique display names for chat {ChatId}", chatId);
            throw;
        }
    }
}

public class ChatInfo
{
    public long ChatId { get; set; }
    public string? Title { get; set; }
    public string? ChatType { get; set; }
    public int MessageCount { get; set; }
    public DateTimeOffset FirstMessage { get; set; }
    public DateTimeOffset LastMessage { get; set; }
}