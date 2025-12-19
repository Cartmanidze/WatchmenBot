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
    /// Batch store embeddings for multiple messages.
    /// Groups consecutive messages from the same author (within 5 min) into single embeddings.
    /// </summary>
    public async Task StoreMessageEmbeddingsBatchAsync(IEnumerable<MessageRecord> messages, CancellationToken ct = default)
    {
        var messageList = messages
            .Where(m => !string.IsNullOrWhiteSpace(m.Text) && m.Text.Length > 10)
            .OrderBy(m => m.DateUtc)
            .ToList();

        if (messageList.Count == 0)
            return;

        try
        {
            // Group consecutive messages from the same author
            var groups = GroupConsecutiveMessages(messageList);

            _logger.LogDebug("[Embeddings] Grouped {Original} messages into {Grouped} chunks",
                messageList.Count, groups.Count);

            var texts = groups.Select(g => FormatGroupForEmbedding(g)).ToList();
            var embeddings = await _embeddingClient.GetEmbeddingsAsync(texts, ct);

            // Prepare batch data
            var batchData = new List<(long ChatId, long MessageId, int ChunkIndex, string ChunkText, float[] Embedding, string MetadataJson)>();

            for (var i = 0; i < groups.Count && i < embeddings.Count; i++)
            {
                var group = groups[i];
                var embedding = embeddings[i];

                if (embedding.Length == 0) continue;

                var firstMsg = group.First();
                var lastMsg = group.Last();
                var metadata = new
                {
                    firstMsg.FromUserId,
                    firstMsg.Username,
                    firstMsg.DisplayName,
                    firstMsg.DateUtc,
                    EndDateUtc = lastMsg.DateUtc,
                    MessageCount = group.Count,
                    MessageIds = group.Select(m => m.Id).ToArray()
                };
                var metadataJson = JsonSerializer.Serialize(metadata);

                // Use first message ID as the primary key
                batchData.Add((firstMsg.ChatId, firstMsg.Id, 0, texts[i], embedding, metadataJson));
            }

            // Batch insert to DB
            if (batchData.Count > 0)
            {
                await StoreBatchEmbeddingsAsync(batchData, ct);
            }

            _logger.LogDebug("[Embeddings] Stored {Stored} embeddings from {Original} messages",
                batchData.Count, messageList.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Embeddings] Failed to store batch of {Count} messages", messageList.Count);
            throw;
        }
    }

    /// <summary>
    /// Groups consecutive messages from the same author within a time window
    /// </summary>
    private static List<List<MessageRecord>> GroupConsecutiveMessages(List<MessageRecord> messages, int maxGapMinutes = 5, int maxGroupSize = 10)
    {
        var groups = new List<List<MessageRecord>>();
        if (messages.Count == 0) return groups;

        var currentGroup = new List<MessageRecord> { messages[0] };

        for (var i = 1; i < messages.Count; i++)
        {
            var prev = messages[i - 1];
            var curr = messages[i];

            var sameAuthor = prev.FromUserId == curr.FromUserId && prev.FromUserId != 0;
            var sameChat = prev.ChatId == curr.ChatId;
            var withinTimeWindow = (curr.DateUtc - prev.DateUtc).TotalMinutes <= maxGapMinutes;
            var groupNotFull = currentGroup.Count < maxGroupSize;

            if (sameAuthor && sameChat && withinTimeWindow && groupNotFull)
            {
                currentGroup.Add(curr);
            }
            else
            {
                groups.Add(currentGroup);
                currentGroup = new List<MessageRecord> { curr };
            }
        }

        groups.Add(currentGroup);
        return groups;
    }

    private static string FormatGroupForEmbedding(List<MessageRecord> group)
    {
        var first = group.First();
        var name = !string.IsNullOrWhiteSpace(first.DisplayName)
            ? first.DisplayName
            : !string.IsNullOrWhiteSpace(first.Username)
                ? first.Username
                : first.FromUserId.ToString();

        if (group.Count == 1)
        {
            return $"{name}: {first.Text}";
        }

        // Multiple messages - combine with newlines
        var combinedText = string.Join("\n", group.Select(m => m.Text));
        return $"{name}: {combinedText}";
    }

    private async Task StoreBatchEmbeddingsAsync(
        List<(long ChatId, long MessageId, int ChunkIndex, string ChunkText, float[] Embedding, string MetadataJson)> batch,
        CancellationToken ct)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync();

        // Build batch insert SQL with VALUES
        var sb = new StringBuilder();
        sb.AppendLine("""
            INSERT INTO message_embeddings (chat_id, message_id, chunk_index, chunk_text, embedding, metadata)
            VALUES
            """);

        var parameters = new DynamicParameters();
        for (var i = 0; i < batch.Count; i++)
        {
            var (chatId, messageId, chunkIndex, chunkText, embedding, metadataJson) = batch[i];

            if (i > 0) sb.Append(',');
            sb.AppendLine($"(@ChatId{i}, @MessageId{i}, @ChunkIndex{i}, @ChunkText{i}, @Embedding{i}::vector, @Metadata{i}::jsonb)");

            parameters.Add($"ChatId{i}", chatId);
            parameters.Add($"MessageId{i}", messageId);
            parameters.Add($"ChunkIndex{i}", chunkIndex);
            parameters.Add($"ChunkText{i}", chunkText);
            parameters.Add($"Embedding{i}", "[" + string.Join(",", embedding) + "]");
            parameters.Add($"Metadata{i}", metadataJson);
        }

        sb.AppendLine("""
            ON CONFLICT (chat_id, message_id, chunk_index)
            DO UPDATE SET
                chunk_text = EXCLUDED.chunk_text,
                embedding = EXCLUDED.embedding,
                metadata = EXCLUDED.metadata,
                created_at = NOW()
            """);

        try
        {
            var affected = await connection.ExecuteAsync(sb.ToString(), parameters);
            _logger.LogDebug("[Embeddings] Batch INSERT: {Affected} rows affected", affected);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Embeddings] Batch INSERT FAILED for {Count} embeddings. SQL length: {SqlLength}",
                batch.Count, sb.Length);
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
            // First check how many embeddings exist for this chat
            using var connection = await _connectionFactory.CreateConnectionAsync();
            var embeddingCount = await connection.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM message_embeddings WHERE chat_id = @ChatId",
                new { ChatId = chatId });

            _logger.LogInformation("[Search] Chat {ChatId} has {Count} embeddings", chatId, embeddingCount);

            if (embeddingCount == 0)
            {
                _logger.LogWarning("[Search] No embeddings found for chat {ChatId}", chatId);
                return new List<SearchResult>();
            }

            var queryEmbedding = await _embeddingClient.GetEmbeddingAsync(query, ct);
            if (queryEmbedding.Length == 0)
            {
                _logger.LogWarning("[Search] Failed to get embedding for query: {Query}", query);
                return new List<SearchResult>();
            }

            _logger.LogDebug("[Search] Got query embedding with {Dims} dimensions", queryEmbedding.Length);

            var results = await SearchByVectorAsync(chatId, queryEmbedding, limit, ct);
            _logger.LogInformation("[Search] Found {Count} results for query in chat {ChatId}", results.Count, chatId);

            return results;
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
    /// Delete ALL embeddings (for full reindex)
    /// </summary>
    public async Task DeleteAllEmbeddingsAsync(CancellationToken ct = default)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync();

        var deleted = await connection.ExecuteAsync("TRUNCATE message_embeddings");

        _logger.LogInformation("Deleted ALL embeddings (TRUNCATE)");
    }

    /// <summary>
    /// Replace display name in chunk_text for existing embeddings
    /// Format: "OldName: message text" (new) or "[date] OldName: " (legacy)
    /// </summary>
    public async Task<int> RenameInEmbeddingsAsync(long? chatId, string oldName, string newName, CancellationToken ct = default)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync();

        // Handle both new format "Name: " and legacy format "] Name: "
        var sql = chatId.HasValue
            ? """
              UPDATE message_embeddings
              SET chunk_text = REPLACE(REPLACE(chunk_text, @OldPatternNew, @NewPatternNew), @OldPatternLegacy, @NewPatternLegacy),
                  metadata = CASE
                      WHEN metadata->>'DisplayName' = @OldName
                      THEN jsonb_set(metadata, '{DisplayName}', to_jsonb(@NewName::text))
                      ELSE metadata
                  END
              WHERE chat_id = @ChatId
                AND (chunk_text LIKE @LikePatternNew OR chunk_text LIKE @LikePatternLegacy OR metadata->>'DisplayName' = @OldName)
              """
            : """
              UPDATE message_embeddings
              SET chunk_text = REPLACE(REPLACE(chunk_text, @OldPatternNew, @NewPatternNew), @OldPatternLegacy, @NewPatternLegacy),
                  metadata = CASE
                      WHEN metadata->>'DisplayName' = @OldName
                      THEN jsonb_set(metadata, '{DisplayName}', to_jsonb(@NewName::text))
                      ELSE metadata
                  END
              WHERE chunk_text LIKE @LikePatternNew OR chunk_text LIKE @LikePatternLegacy OR metadata->>'DisplayName' = @OldName
              """;

        var affected = await connection.ExecuteAsync(sql, new
        {
            ChatId = chatId,
            OldName = oldName,
            NewName = newName,
            // New format: starts with "Name: "
            OldPatternNew = $"{oldName}: ",
            NewPatternNew = $"{newName}: ",
            LikePatternNew = $"{oldName}: %",
            // Legacy format: "] Name: "
            OldPatternLegacy = $"] {oldName}: ",
            NewPatternLegacy = $"] {newName}: ",
            LikePatternLegacy = $"%] {oldName}: %"
        });

        _logger.LogInformation("Renamed '{OldName}' to '{NewName}' in {Count} embeddings (chatId: {ChatId})",
            oldName, newName, affected, chatId?.ToString() ?? "all");

        return affected;
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
        // Format: "Name: message text" (без даты — она в metadata)
        var name = !string.IsNullOrWhiteSpace(message.DisplayName)
            ? message.DisplayName
            : !string.IsNullOrWhiteSpace(message.Username)
                ? message.Username
                : message.FromUserId.ToString();

        return $"{name}: {message.Text}";
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

