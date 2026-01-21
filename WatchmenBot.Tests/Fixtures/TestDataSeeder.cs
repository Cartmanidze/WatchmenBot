using System.Text.Json;
using Dapper;
using WatchmenBot.Features.Search.Services;
using WatchmenBot.Infrastructure.Database;

namespace WatchmenBot.Tests.Fixtures;

/// <summary>
/// Helper for seeding test data in database.
/// Provides methods to create messages, embeddings, user profiles, etc.
/// </summary>
public class TestDataSeeder
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly EmbeddingClient _embeddingClient;

    public TestDataSeeder(IDbConnectionFactory connectionFactory, EmbeddingClient embeddingClient)
    {
        _connectionFactory = connectionFactory;
        _embeddingClient = embeddingClient;
    }

    /// <summary>
    /// Ensure chat exists in the chats table.
    /// Creates the chat if it doesn't exist.
    /// </summary>
    public async Task EnsureChatExistsAsync(long chatId, string title = "Test Chat")
    {
        using var connection = await _connectionFactory.CreateConnectionAsync();
        var chatType = chatId < 0 ? "supergroup" : "private";

        await connection.ExecuteAsync(@"
            INSERT INTO chats (id, title, type, updated_at)
            VALUES (@ChatId, @Title, @Type, NOW())
            ON CONFLICT (id) DO NOTHING",
            new { ChatId = chatId, Title = title, Type = chatType });
    }

    /// <summary>
    /// Seed simple text messages without embeddings.
    /// Good for testing message retrieval and summary.
    /// </summary>
    public async Task SeedMessagesAsync(
        long chatId,
        long userId,
        string username,
        params string[] texts)
    {
        // Ensure chat exists first
        await EnsureChatExistsAsync(chatId);

        using var connection = await _connectionFactory.CreateConnectionAsync();

        for (int i = 0; i < texts.Length; i++)
        {
            var messageId = (int)(Math.Abs(chatId) * 1000 + i + 1); // Unique across chats
            await connection.ExecuteAsync(@"
                INSERT INTO messages (id, chat_id, from_user_id, username, display_name, text, date_utc)
                VALUES (@MsgId, @ChatId, @UserId, @Username, @DisplayName, @Text, @Date)
                ON CONFLICT (chat_id, id) DO NOTHING",
                new
                {
                    MsgId = messageId,
                    ChatId = chatId,
                    UserId = userId,
                    Username = username,
                    DisplayName = username, // Use username as display_name by default
                    Text = texts[i],
                    Date = DateTime.UtcNow.AddHours(-texts.Length + i)
                });
        }
    }

    /// <summary>
    /// Seed messages with embeddings for RAG testing.
    /// Uses real embedding API to create searchable vectors.
    /// </summary>
    public async Task SeedMessagesWithEmbeddingsAsync(
        long chatId,
        long userId,
        string username,
        params string[] texts)
    {
        // First seed the messages
        await SeedMessagesAsync(chatId, userId, username, texts);

        // Then create embeddings
        using var connection = await _connectionFactory.CreateConnectionAsync();
        var embeddings = await _embeddingClient.GetEmbeddingsAsync(texts, CancellationToken.None);

        var metadataJson = JsonSerializer.Serialize(new { Username = username });

        for (int i = 0; i < texts.Length; i++)
        {
            var messageId = (int)(Math.Abs(chatId) * 1000 + i + 1);
            var vectorStr = string.Join(",", embeddings[i]);

            await connection.ExecuteAsync($@"
                INSERT INTO message_embeddings (chat_id, message_id, chunk_index, chunk_text, embedding, metadata)
                VALUES (@ChatId, @MsgId, 0, @Text, '[{vectorStr}]'::vector, @Metadata::jsonb)
                ON CONFLICT (chat_id, message_id, chunk_index) DO NOTHING",
                new
                {
                    ChatId = chatId,
                    MsgId = messageId,
                    Text = texts[i],
                    Metadata = metadataJson
                });
        }
    }

    /// <summary>
    /// Seed a user profile for personalization testing.
    /// </summary>
    public async Task SeedUserProfileAsync(
        long chatId,
        long userId,
        string username,
        string displayName,
        string[]? facts = null,
        string? summary = null)
    {
        // Ensure chat exists first
        await EnsureChatExistsAsync(chatId);

        using var connection = await _connectionFactory.CreateConnectionAsync();
        var factsJson = facts != null ? JsonSerializer.Serialize(facts) : "[]";

        await connection.ExecuteAsync(@"
            INSERT INTO user_profiles (user_id, chat_id, display_name, username, facts, summary, interaction_count, message_count)
            VALUES (@UserId, @ChatId, @DisplayName, @Username, @Facts::jsonb, @Summary, 1, 10)
            ON CONFLICT (chat_id, user_id) DO UPDATE
            SET display_name = @DisplayName, username = @Username, facts = @Facts::jsonb, summary = @Summary",
            new
            {
                UserId = userId,
                ChatId = chatId,
                DisplayName = displayName,
                Username = username,
                Facts = factsJson,
                Summary = summary
            });

        // Add username alias
        await connection.ExecuteAsync(@"
            INSERT INTO user_aliases (chat_id, user_id, alias, alias_type, usage_count)
            VALUES (@ChatId, @UserId, @Username, 'username', 1)
            ON CONFLICT (chat_id, user_id, alias) DO NOTHING",
            new { ChatId = chatId, UserId = userId, Username = username });

        // Add display name alias
        await connection.ExecuteAsync(@"
            INSERT INTO user_aliases (chat_id, user_id, alias, alias_type, usage_count)
            VALUES (@ChatId, @UserId, @DisplayName, 'display_name', 1)
            ON CONFLICT (chat_id, user_id, alias) DO NOTHING",
            new { ChatId = chatId, UserId = userId, DisplayName = displayName });
    }

    /// <summary>
    /// Seed a conversation with multiple users.
    /// Good for testing multi-user scenarios.
    /// </summary>
    public async Task SeedConversationAsync(
        long chatId,
        params (long userId, string username, string[] messages)[] participants)
    {
        // Ensure chat exists first
        await EnsureChatExistsAsync(chatId);

        using var connection = await _connectionFactory.CreateConnectionAsync();
        var messageIndex = 0;

        // Interleave messages from different users
        var maxMessages = participants.Max(p => p.messages.Length);

        for (int i = 0; i < maxMessages; i++)
        {
            foreach (var (userId, username, messages) in participants)
            {
                if (i >= messages.Length) continue;

                var messageId = (int)(Math.Abs(chatId) * 1000 + messageIndex + 1);
                await connection.ExecuteAsync(@"
                    INSERT INTO messages (id, chat_id, from_user_id, username, display_name, text, date_utc)
                    VALUES (@MsgId, @ChatId, @UserId, @Username, @DisplayName, @Text, @Date)
                    ON CONFLICT (chat_id, id) DO NOTHING",
                    new
                    {
                        MsgId = messageId,
                        ChatId = chatId,
                        UserId = userId,
                        Username = username,
                        DisplayName = username,
                        Text = messages[i],
                        Date = DateTime.UtcNow.AddMinutes(-maxMessages * participants.Length + messageIndex)
                    });

                messageIndex++;
            }
        }
    }

    /// <summary>
    /// Seed messages from the last N hours for summary testing.
    /// </summary>
    public async Task SeedRecentMessagesAsync(
        long chatId,
        int hoursAgo,
        params (long userId, string username, string text)[] messages)
    {
        // Ensure chat exists first
        await EnsureChatExistsAsync(chatId);

        using var connection = await _connectionFactory.CreateConnectionAsync();
        // Add 5 minute buffer to ensure messages are clearly within the time window
        // This prevents race conditions where seeder timestamp falls before query start
        var baseTime = DateTime.UtcNow.AddHours(-hoursAgo).AddMinutes(5);

        for (int i = 0; i < messages.Length; i++)
        {
            var (userId, username, text) = messages[i];
            var messageId = (int)(Math.Abs(chatId) * 1000 + i + 1);
            var timestamp = baseTime.AddMinutes(i * (hoursAgo * 60.0 / messages.Length));

            await connection.ExecuteAsync(@"
                INSERT INTO messages (id, chat_id, from_user_id, username, display_name, text, date_utc)
                VALUES (@MsgId, @ChatId, @UserId, @Username, @DisplayName, @Text, @Date)
                ON CONFLICT (chat_id, id) DO NOTHING",
                new
                {
                    MsgId = messageId,
                    ChatId = chatId,
                    UserId = userId,
                    Username = username,
                    DisplayName = username,
                    Text = text,
                    Date = timestamp
                });
        }
    }

    /// <summary>
    /// Get the count of messages in a chat.
    /// </summary>
    public async Task<int> GetMessageCountAsync(long chatId)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync();
        return await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM messages WHERE chat_id = @ChatId",
            new { ChatId = chatId });
    }

    /// <summary>
    /// Get the count of embeddings in a chat.
    /// </summary>
    public async Task<int> GetEmbeddingCountAsync(long chatId)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync();
        return await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM message_embeddings WHERE chat_id = @ChatId",
            new { ChatId = chatId });
    }

    /// <summary>
    /// Verify a message was saved to the database.
    /// </summary>
    public async Task<bool> MessageExistsAsync(long chatId, int messageId)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync();
        return await connection.ExecuteScalarAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM messages WHERE chat_id = @ChatId AND id = @MsgId)",
            new { ChatId = chatId, MsgId = messageId });
    }
}
