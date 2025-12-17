using System.Text;
using System.Text.Json;
using Dapper;
using WatchmenBot.Infrastructure.Database;
using WatchmenBot.Models;

namespace WatchmenBot.Services;

public class EmbeddingService
{
    private readonly EmbeddingClient _embeddingClient;
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<EmbeddingService> _logger;

    public EmbeddingService(
        EmbeddingClient embeddingClient,
        IDbConnectionFactory connectionFactory,
        ILogger<EmbeddingService> logger)
    {
        _embeddingClient = embeddingClient;
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    /// <summary>
    /// Stores embedding for a message
    /// </summary>
    public async Task StoreMessageEmbeddingAsync(MessageRecord message, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(message.Text))
            return;

        try
        {
            var text = FormatMessageForEmbedding(message);
            var embedding = await _embeddingClient.GetEmbeddingAsync(text, ct);

            if (embedding.Length == 0)
            {
                _logger.LogWarning("Empty embedding returned for message {MessageId}", message.Id);
                return;
            }

            await StoreEmbeddingAsync(
                message.ChatId,
                message.Id,
                0,
                text,
                embedding,
                new { message.FromUserId, message.Username, message.DisplayName, message.DateUtc },
                ct);

            _logger.LogDebug("Stored embedding for message {MessageId} in chat {ChatId}", message.Id, message.ChatId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to store embedding for message {MessageId}", message.Id);
        }
    }

    /// <summary>
    /// Batch store embeddings for multiple messages
    /// </summary>
    public async Task StoreMessageEmbeddingsBatchAsync(IEnumerable<MessageRecord> messages, CancellationToken ct = default)
    {
        var messageList = messages
            .Where(m => !string.IsNullOrWhiteSpace(m.Text))
            .ToList();

        if (messageList.Count == 0)
            return;

        try
        {
            var texts = messageList.Select(FormatMessageForEmbedding).ToList();
            var embeddings = await _embeddingClient.GetEmbeddingsAsync(texts, ct);

            var stored = 0;
            for (var i = 0; i < messageList.Count && i < embeddings.Count; i++)
            {
                var message = messageList[i];
                var embedding = embeddings[i];

                if (embedding.Length == 0) continue;

                await StoreEmbeddingAsync(
                    message.ChatId,
                    message.Id,
                    0,
                    texts[i],
                    embedding,
                    new { message.FromUserId, message.Username, message.DisplayName, message.DateUtc },
                    ct);
                stored++;
            }

            _logger.LogDebug("[Embeddings] Stored {Stored}/{Total} embeddings to DB", stored, messageList.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Embeddings] Failed to store batch of {Count} messages", messageList.Count);
            throw;
        }
    }

    /// <summary>
    /// Search for similar messages using vector similarity
    /// </summary>
    public async Task<List<SearchResult>> SearchSimilarAsync(
        long chatId,
        string query,
        int limit = 10,
        CancellationToken ct = default)
    {
        try
        {
            var queryEmbedding = await _embeddingClient.GetEmbeddingAsync(query, ct);
            if (queryEmbedding.Length == 0)
                return new List<SearchResult>();

            return await SearchByVectorAsync(chatId, queryEmbedding, limit, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to search similar messages for query: {Query}", query);
            return new List<SearchResult>();
        }
    }

    /// <summary>
    /// Get context for RAG based on a query
    /// </summary>
    public async Task<string> GetRagContextAsync(
        long chatId,
        string query,
        int maxResults = 10,
        CancellationToken ct = default)
    {
        var results = await SearchSimilarAsync(chatId, query, maxResults, ct);

        if (results.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        foreach (var result in results)
        {
            sb.AppendLine(result.ChunkText);
            sb.AppendLine("---");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Search for messages similar to a topic/query within a date range
    /// </summary>
    public async Task<List<SearchResult>> SearchSimilarInRangeAsync(
        long chatId,
        string query,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        int limit = 20,
        CancellationToken ct = default)
    {
        try
        {
            var queryEmbedding = await _embeddingClient.GetEmbeddingAsync(query, ct);
            if (queryEmbedding.Length == 0)
                return new List<SearchResult>();

            using var connection = await _connectionFactory.CreateConnectionAsync();
            var embeddingString = "[" + string.Join(",", queryEmbedding) + "]";

            var results = await connection.QueryAsync<SearchResult>(
                """
                SELECT
                    me.chat_id as ChatId,
                    me.message_id as MessageId,
                    me.chunk_index as ChunkIndex,
                    me.chunk_text as ChunkText,
                    me.metadata as MetadataJson,
                    1 - (me.embedding <=> @Embedding::vector) as Similarity
                FROM message_embeddings me
                JOIN messages m ON me.chat_id = m.chat_id AND me.message_id = m.id
                WHERE me.chat_id = @ChatId
                  AND m.date_utc >= @StartUtc
                  AND m.date_utc < @EndUtc
                ORDER BY me.embedding <=> @Embedding::vector
                LIMIT @Limit
                """,
                new { ChatId = chatId, Embedding = embeddingString, StartUtc = startUtc.UtcDateTime, EndUtc = endUtc.UtcDateTime, Limit = limit });

            return results.ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to search similar messages in range for query: {Query}", query);
            return new List<SearchResult>();
        }
    }

    /// <summary>
    /// Get diverse representative messages by sampling across time buckets
    /// </summary>
    public async Task<List<SearchResult>> GetDiverseMessagesAsync(
        long chatId,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        int limit = 50,
        CancellationToken ct = default)
    {
        try
        {
            using var connection = await _connectionFactory.CreateConnectionAsync();

            // Simple approach: sample messages evenly across time period
            // This avoids loading vectors into memory
            var results = await connection.QueryAsync<SearchResult>(
                """
                WITH numbered AS (
                    SELECT
                        me.chat_id as ChatId,
                        me.message_id as MessageId,
                        me.chunk_index as ChunkIndex,
                        me.chunk_text as ChunkText,
                        me.metadata as MetadataJson,
                        ROW_NUMBER() OVER (ORDER BY m.date_utc) as rn,
                        COUNT(*) OVER () as total
                    FROM message_embeddings me
                    JOIN messages m ON me.chat_id = m.chat_id AND me.message_id = m.id
                    WHERE me.chat_id = @ChatId
                      AND m.date_utc >= @StartUtc
                      AND m.date_utc < @EndUtc
                )
                SELECT ChatId, MessageId, ChunkIndex, ChunkText, MetadataJson, 1.0 as Similarity
                FROM numbered
                WHERE total <= @Limit OR rn % GREATEST(1, total / @Limit) = 0
                LIMIT @Limit
                """,
                new { ChatId = chatId, StartUtc = startUtc.UtcDateTime, EndUtc = endUtc.UtcDateTime, Limit = limit });

            return results.ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get diverse messages for chat {ChatId}", chatId);
            return new List<SearchResult>();
        }
    }

    /// <summary>
    /// Check if embedding exists for a message
    /// </summary>
    public async Task<bool> HasEmbeddingAsync(long chatId, long messageId, CancellationToken ct = default)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync();

        var exists = await connection.ExecuteScalarAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM message_embeddings WHERE chat_id = @ChatId AND message_id = @MessageId)",
            new { ChatId = chatId, MessageId = messageId });

        return exists;
    }

    /// <summary>
    /// Delete embeddings for a chat
    /// </summary>
    public async Task DeleteChatEmbeddingsAsync(long chatId, CancellationToken ct = default)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync();

        var deleted = await connection.ExecuteAsync(
            "DELETE FROM message_embeddings WHERE chat_id = @ChatId",
            new { ChatId = chatId });

        _logger.LogInformation("Deleted {Count} embeddings for chat {ChatId}", deleted, chatId);
    }

    /// <summary>
    /// Get embedding statistics for a chat
    /// </summary>
    public async Task<EmbeddingStats> GetStatsAsync(long chatId, CancellationToken ct = default)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync();

        var stats = await connection.QuerySingleOrDefaultAsync<EmbeddingStats>(
            """
            SELECT
                COUNT(*) as TotalEmbeddings,
                MIN(created_at) as OldestEmbedding,
                MAX(created_at) as NewestEmbedding
            FROM message_embeddings
            WHERE chat_id = @ChatId
            """,
            new { ChatId = chatId });

        return stats ?? new EmbeddingStats();
    }

    private static string FormatMessageForEmbedding(MessageRecord message)
    {
        var name = !string.IsNullOrWhiteSpace(message.DisplayName)
            ? message.DisplayName
            : !string.IsNullOrWhiteSpace(message.Username)
                ? message.Username
                : message.FromUserId.ToString();

        return $"[{message.DateUtc.ToLocalTime():yyyy-MM-dd HH:mm}] {name}: {message.Text}";
    }

    private async Task StoreEmbeddingAsync(
        long chatId,
        long messageId,
        int chunkIndex,
        string chunkText,
        float[] embedding,
        object metadata,
        CancellationToken ct)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync();

        var embeddingString = "[" + string.Join(",", embedding) + "]";
        var metadataJson = JsonSerializer.Serialize(metadata);

        await connection.ExecuteAsync(
            """
            INSERT INTO message_embeddings (chat_id, message_id, chunk_index, chunk_text, embedding, metadata)
            VALUES (@ChatId, @MessageId, @ChunkIndex, @ChunkText, @Embedding::vector, @Metadata::jsonb)
            ON CONFLICT (chat_id, message_id, chunk_index)
            DO UPDATE SET
                chunk_text = EXCLUDED.chunk_text,
                embedding = EXCLUDED.embedding,
                metadata = EXCLUDED.metadata,
                created_at = NOW()
            """,
            new
            {
                ChatId = chatId,
                MessageId = messageId,
                ChunkIndex = chunkIndex,
                ChunkText = chunkText,
                Embedding = embeddingString,
                Metadata = metadataJson
            });
    }

    private async Task<List<SearchResult>> SearchByVectorAsync(
        long chatId,
        float[] queryEmbedding,
        int limit,
        CancellationToken ct)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync();

        var embeddingString = "[" + string.Join(",", queryEmbedding) + "]";

        var results = await connection.QueryAsync<SearchResult>(
            """
            SELECT
                chat_id as ChatId,
                message_id as MessageId,
                chunk_index as ChunkIndex,
                chunk_text as ChunkText,
                metadata as MetadataJson,
                1 - (embedding <=> @Embedding::vector) as Similarity
            FROM message_embeddings
            WHERE chat_id = @ChatId
            ORDER BY embedding <=> @Embedding::vector
            LIMIT @Limit
            """,
            new { ChatId = chatId, Embedding = embeddingString, Limit = limit });

        return results.ToList();
    }
}

public class SearchResult
{
    public long ChatId { get; set; }
    public long MessageId { get; set; }
    public int ChunkIndex { get; set; }
    public string ChunkText { get; set; } = string.Empty;
    public string? MetadataJson { get; set; }
    public double Similarity { get; set; }
}

public class EmbeddingStats
{
    public int TotalEmbeddings { get; set; }
    public DateTimeOffset? OldestEmbedding { get; set; }
    public DateTimeOffset? NewestEmbedding { get; set; }
}

