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
}