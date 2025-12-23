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
                    me.embedding <=> @Embedding::vector as Distance,
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

            return results.Select(r =>
            {
                r.IsNewsDump = DetectNewsDump(r.ChunkText);
                return r;
            }).ToList();
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
                embedding <=> @Embedding::vector as Distance,
                1 - (embedding <=> @Embedding::vector) as Similarity
            FROM message_embeddings
            WHERE chat_id = @ChatId
            ORDER BY embedding <=> @Embedding::vector
            LIMIT @Limit
            """,
            new { ChatId = chatId, Embedding = embeddingString, Limit = limit });

        return results.Select(r =>
        {
            r.IsNewsDump = DetectNewsDump(r.ChunkText);
            return r;
        }).ToList();
    }

    /// <summary>
    /// Search with confidence assessment — the main method for RAG
    /// </summary>
    public async Task<SearchResponse> SearchWithConfidenceAsync(
        long chatId,
        string query,
        int limit = 20,
        CancellationToken ct = default)
    {
        var response = new SearchResponse();

        try
        {
            // First, try full-text search for exact matches
            var fullTextResults = await FullTextSearchAsync(chatId, query, 5, ct);
            response.HasFullTextMatch = fullTextResults.Count > 0;

            // Then, vector search
            var results = await SearchSimilarAsync(chatId, query, limit, ct);
            if (results.Count == 0)
            {
                response.Confidence = SearchConfidence.None;
                response.ConfidenceReason = "Нет embeddings в этом чате";
                return response;
            }

            // Apply adjustments: news dump penalty + recency boost
            var now = DateTimeOffset.UtcNow;
            foreach (var r in results)
            {
                // News dump penalty
                if (r.IsNewsDump)
                {
                    r.Similarity -= 0.05;
                }

                // Recency boost: newer messages get up to +0.1 boost
                var timestamp = ParseTimestampFromMetadata(r.MetadataJson);
                if (timestamp != DateTimeOffset.MinValue)
                {
                    var ageInDays = (now - timestamp).TotalDays;
                    // Last 7 days: +0.1, 30 days: +0.05, 90 days: +0.02, older: 0
                    var recencyBoost = ageInDays switch
                    {
                        <= 7 => 0.10,
                        <= 30 => 0.05,
                        <= 90 => 0.02,
                        _ => 0.0
                    };
                    r.Similarity += recencyBoost;
                }
            }

            // Re-sort after adjustments
            results = results.OrderByDescending(r => r.Similarity).ToList();
            response.Results = results;

            // Calculate confidence metrics
            var best = results[0].Similarity;
            var fifth = results.Count >= 5 ? results[4].Similarity : results.Last().Similarity;
            var gap = best - fifth;

            response.BestScore = best;
            response.ScoreGap = gap;

            // Determine confidence level
            (response.Confidence, response.ConfidenceReason) = EvaluateConfidence(best, gap, response.HasFullTextMatch);

            // If confidence is low and no full-text match, try ILIKE fallback for slang
            if (response.Confidence <= SearchConfidence.Low && !response.HasFullTextMatch)
            {
                var ilikeResults = await SimpleTextSearchAsync(chatId, query, 10, ct);
                if (ilikeResults.Count > 0)
                {
                    _logger.LogInformation("[Search] ILIKE fallback found {Count} additional results", ilikeResults.Count);

                    // Merge ILIKE results with vector results (avoiding duplicates)
                    var existingIds = results.Select(r => r.MessageId).ToHashSet();
                    var newResults = ilikeResults.Where(r => !existingIds.Contains(r.MessageId)).ToList();

                    if (newResults.Count > 0)
                    {
                        results.AddRange(newResults);
                        results = results.OrderByDescending(r => r.Similarity).ToList();
                        response.Results = results;

                        // Recalculate confidence with ILIKE boost
                        response.HasFullTextMatch = true;
                        best = results[0].Similarity;
                        response.BestScore = best;
                        (response.Confidence, response.ConfidenceReason) = EvaluateConfidence(best, gap, true);
                        response.ConfidenceReason += " (ILIKE fallback)";
                    }
                }
            }

            _logger.LogInformation(
                "[Search] Query: '{Query}' | Best: {Best:F3} | Gap: {Gap:F3} | FullText: {FT} | Confidence: {Conf}",
                TruncateForLog(query, 50), best, gap, response.HasFullTextMatch, response.Confidence);

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to search with confidence for query: {Query}", query);
            response.Confidence = SearchConfidence.None;
            response.ConfidenceReason = "Ошибка поиска";
            return response;
        }
    }

    /// <summary>
    /// Full-text search using PostgreSQL tsvector
    /// </summary>
    public async Task<List<SearchResult>> FullTextSearchAsync(
        long chatId,
        string query,
        int limit = 10,
        CancellationToken ct = default)
    {
        try
        {
            using var connection = await _connectionFactory.CreateConnectionAsync();

            // Extract meaningful words for full-text search
            var searchTerms = ExtractSearchTerms(query);
            if (string.IsNullOrWhiteSpace(searchTerms))
                return new List<SearchResult>();

            var results = await connection.QueryAsync<SearchResult>(
                """
                SELECT
                    chat_id as ChatId,
                    message_id as MessageId,
                    chunk_index as ChunkIndex,
                    chunk_text as ChunkText,
                    metadata as MetadataJson,
                    0.0 as Distance,
                    ts_rank(to_tsvector('russian', chunk_text), websearch_to_tsquery('russian', @Query)) as Similarity
                FROM message_embeddings
                WHERE chat_id = @ChatId
                  AND to_tsvector('russian', chunk_text) @@ websearch_to_tsquery('russian', @Query)
                ORDER BY Similarity DESC
                LIMIT @Limit
                """,
                new { ChatId = chatId, Query = searchTerms, Limit = limit });

            return results.ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Full-text search failed for query: {Query}", query);
            return new List<SearchResult>();
        }
    }

    /// <summary>
    /// Simple ILIKE search for slang/toxic words that embeddings miss
    /// </summary>
    public async Task<List<SearchResult>> SimpleTextSearchAsync(
        long chatId,
        string query,
        int limit = 10,
        CancellationToken ct = default)
    {
        try
        {
            // Extract words longer than 3 chars for ILIKE search
            var words = query
                .Split(new[] { ' ', ',', '.', '!', '?', ':', ';', '-', '(', ')', '[', ']', '"', '\'' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length > 3)
                .Select(w => w.ToLowerInvariant())
                .Distinct()
                .Take(5) // Limit to avoid huge queries
                .ToList();

            if (words.Count == 0)
                return new List<SearchResult>();

            using var connection = await _connectionFactory.CreateConnectionAsync();

            // Build ILIKE conditions for each word
            var conditions = words.Select((w, i) => $"LOWER(chunk_text) LIKE @Word{i}").ToList();
            var whereClause = string.Join(" OR ", conditions);

            var parameters = new DynamicParameters();
            parameters.Add("ChatId", chatId);
            parameters.Add("Limit", limit);
            for (var i = 0; i < words.Count; i++)
            {
                parameters.Add($"Word{i}", $"%{words[i]}%");
            }

            var sql = $"""
                SELECT
                    chat_id as ChatId,
                    message_id as MessageId,
                    chunk_index as ChunkIndex,
                    chunk_text as ChunkText,
                    metadata as MetadataJson,
                    0.0 as Distance,
                    0.6 as Similarity
                FROM message_embeddings
                WHERE chat_id = @ChatId AND ({whereClause})
                ORDER BY message_id DESC
                LIMIT @Limit
                """;

            var results = await connection.QueryAsync<SearchResult>(sql, parameters);

            _logger.LogDebug("[ILIKE] Found {Count} results for words: {Words}",
                results.Count(), string.Join(", ", words));

            return results.ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ILIKE search failed for query: {Query}", query);
            return new List<SearchResult>();
        }
    }

    /// <summary>
    /// Extract meaningful search terms from a query
    /// </summary>
    private static string ExtractSearchTerms(string query)
    {
        // Remove common question words and punctuation
        var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "кто", "что", "где", "когда", "как", "почему", "зачем", "какой", "какая", "какое", "какие",
            "это", "эта", "этот", "эти", "тот", "та", "то", "те", "чем", "про", "об", "обо",
            "ли", "же", "бы", "не", "ни", "да", "нет", "или", "и", "а", "но", "в", "на", "с", "к", "у", "о",
            "за", "из", "по", "до", "от", "для", "при", "без", "над", "под", "между", "через",
            "самый", "самая", "самое", "очень", "много", "мало", "все", "всё", "всех", "весь", "вся",
            "был", "была", "было", "были", "есть", "будет", "можно", "нужно", "надо"
        };

        var words = query
            .ToLowerInvariant()
            .Split(new[] { ' ', ',', '.', '!', '?', ':', ';', '-', '(', ')', '[', ']', '"', '\'' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2 && !stopWords.Contains(w))
            .Distinct()
            .ToList();

        return string.Join(" ", words);
    }

    /// <summary>
    /// Evaluate search confidence based on scores
    /// </summary>
    private static (SearchConfidence confidence, string reason) EvaluateConfidence(double bestScore, double gap, bool hasFullText)
    {
        // If full-text found exact matches, that's a strong signal
        if (hasFullText)
        {
            if (bestScore >= 0.5)
                return (SearchConfidence.High, "Точное совпадение слов + высокий similarity");
            if (bestScore >= 0.35)
                return (SearchConfidence.Medium, "Точное совпадение слов");
            return (SearchConfidence.Low, "Слова найдены, но семантически далеко");
        }

        // Vector-only search thresholds
        // High: best >= 0.5 AND gap >= 0.05 (clear winner)
        if (bestScore >= 0.5 && gap >= 0.05)
            return (SearchConfidence.High, $"Сильное совпадение (sim={bestScore:F2}, gap={gap:F2})");

        // Medium: best >= 0.4 OR (best >= 0.35 AND gap >= 0.03)
        if (bestScore >= 0.4)
            return (SearchConfidence.Medium, $"Среднее совпадение (sim={bestScore:F2})");

        if (bestScore >= 0.35 && gap >= 0.03)
            return (SearchConfidence.Medium, $"Есть выделяющийся результат (sim={bestScore:F2}, gap={gap:F2})");

        // Low: best >= 0.25
        if (bestScore >= 0.25)
            return (SearchConfidence.Low, $"Слабое совпадение (sim={bestScore:F2})");

        // None: best < 0.25
        return (SearchConfidence.None, $"Нет релевантных совпадений (best sim={bestScore:F2})");
    }

    /// <summary>
    /// Detect if text looks like a news dump (long, lots of links, emojis)
    /// </summary>
    private static bool DetectNewsDump(string text)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        var indicators = 0;

        // Long text
        if (text.Length > 800) indicators++;

        // Multiple URLs
        var urlCount = System.Text.RegularExpressions.Regex.Matches(text, @"https?://").Count;
        if (urlCount >= 2) indicators++;

        // News indicators
        var newsPatterns = new[] { "— СМИ", "Подписаться", "⚡", "❗", "🔴", "BREAKING", "Срочно:", "Источник:" };
        if (newsPatterns.Any(p => text.Contains(p, StringComparison.OrdinalIgnoreCase))) indicators++;

        // Many emojis at the start
        if (text.Length > 0 && char.IsHighSurrogate(text[0])) indicators++;

        return indicators >= 2;
    }

    private static string TruncateForLog(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            return text;
        return text[..(maxLength - 3)] + "...";
    }

    /// <summary>
    /// Get messages from a specific user (for personal questions like "я гондон?" or "что за тип @Вася?")
    /// </summary>
    public async Task<List<SearchResult>> GetUserMessagesAsync(
        long chatId,
        string usernameOrName,
        int days = 7,
        int limit = 30,
        CancellationToken ct = default)
    {
        try
        {
            using var connection = await _connectionFactory.CreateConnectionAsync();

            // Remove @ prefix if present
            var cleanName = usernameOrName.TrimStart('@');

            var startDate = DateTime.UtcNow.AddDays(-days);

            // Search by username or display name in metadata
            var results = await connection.QueryAsync<SearchResult>(
                """
                SELECT
                    me.chat_id as ChatId,
                    me.message_id as MessageId,
                    me.chunk_index as ChunkIndex,
                    me.chunk_text as ChunkText,
                    me.metadata as MetadataJson,
                    0.0 as Distance,
                    1.0 as Similarity
                FROM message_embeddings me
                JOIN messages m ON me.chat_id = m.chat_id AND me.message_id = m.id
                WHERE me.chat_id = @ChatId
                  AND m.date_utc >= @StartDate
                  AND (
                      me.metadata->>'Username' ILIKE @Pattern
                      OR me.metadata->>'DisplayName' ILIKE @Pattern
                      OR me.chunk_text ILIKE @TextPattern
                  )
                ORDER BY m.date_utc DESC
                LIMIT @Limit
                """,
                new
                {
                    ChatId = chatId,
                    StartDate = startDate,
                    Pattern = cleanName,
                    TextPattern = $"{cleanName}:%", // "Name: message..."
                    Limit = limit
                });

            _logger.LogInformation("[Search] Found {Count} messages from user '{User}' in chat {ChatId}",
                results.Count(), cleanName, chatId);

            return results.ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get messages for user: {User}", usernameOrName);
            return new List<SearchResult>();
        }
    }

    /// <summary>
    /// Get messages that mention a specific user
    /// </summary>
    public async Task<List<SearchResult>> GetMentionsOfUserAsync(
        long chatId,
        string usernameOrName,
        int days = 7,
        int limit = 20,
        CancellationToken ct = default)
    {
        try
        {
            using var connection = await _connectionFactory.CreateConnectionAsync();

            var cleanName = usernameOrName.TrimStart('@');
            var startDate = DateTime.UtcNow.AddDays(-days);

            // Search for mentions in text (but NOT messages from the user themselves)
            var results = await connection.QueryAsync<SearchResult>(
                """
                SELECT
                    me.chat_id as ChatId,
                    me.message_id as MessageId,
                    me.chunk_index as ChunkIndex,
                    me.chunk_text as ChunkText,
                    me.metadata as MetadataJson,
                    0.0 as Distance,
                    0.9 as Similarity
                FROM message_embeddings me
                JOIN messages m ON me.chat_id = m.chat_id AND me.message_id = m.id
                WHERE me.chat_id = @ChatId
                  AND m.date_utc >= @StartDate
                  AND me.chunk_text ILIKE @Pattern
                  AND NOT (
                      me.metadata->>'Username' ILIKE @Name
                      OR me.metadata->>'DisplayName' ILIKE @Name
                  )
                ORDER BY m.date_utc DESC
                LIMIT @Limit
                """,
                new
                {
                    ChatId = chatId,
                    StartDate = startDate,
                    Pattern = $"%{cleanName}%",
                    Name = cleanName,
                    Limit = limit
                });

            _logger.LogInformation("[Search] Found {Count} mentions of user '{User}' in chat {ChatId}",
                results.Count(), cleanName, chatId);

            return results.ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get mentions for user: {User}", usernameOrName);
            return new List<SearchResult>();
        }
    }

    /// <summary>
    /// Combined personal retrieval: user's messages + mentions of user
    /// Now with proper vector search within the pool!
    /// </summary>
    public async Task<SearchResponse> GetPersonalContextAsync(
        long chatId,
        string usernameOrName,
        string? displayName,
        string question,  // The actual question to search for relevance
        int days = 7,
        CancellationToken ct = default)
    {
        var response = new SearchResponse();

        try
        {
            var searchNames = new List<string>();

            // Add username if provided
            if (!string.IsNullOrWhiteSpace(usernameOrName))
                searchNames.Add(usernameOrName.TrimStart('@'));

            // Add display name if different from username
            if (!string.IsNullOrWhiteSpace(displayName) &&
                !searchNames.Any(n => n.Equals(displayName, StringComparison.OrdinalIgnoreCase)))
                searchNames.Add(displayName);

            // Step 1: Collect pool of message IDs from user's messages + mentions
            var poolMessageIds = new HashSet<long>();

            foreach (var name in searchNames)
            {
                // Get user's own messages (larger pool)
                var userMessages = await GetUserMessagesAsync(chatId, name, days, 100, ct);
                foreach (var msg in userMessages)
                    poolMessageIds.Add(msg.MessageId);

                // Get mentions of user
                var mentions = await GetMentionsOfUserAsync(chatId, name, days, 50, ct);
                foreach (var msg in mentions)
                    poolMessageIds.Add(msg.MessageId);
            }

            _logger.LogInformation(
                "[Personal] User: {Names} | Pool size: {Count} messages",
                string.Join("/", searchNames), poolMessageIds.Count);

            if (poolMessageIds.Count == 0)
            {
                response.Confidence = SearchConfidence.None;
                response.ConfidenceReason = "Пользователь не найден в истории чата";
                return response;
            }

            // Step 2: Vector search WITHIN this pool using the question
            var results = await SearchByVectorInPoolAsync(chatId, question, poolMessageIds.ToList(), 20, ct);

            if (results.Count == 0)
            {
                response.Confidence = SearchConfidence.Low;
                response.ConfidenceReason = $"Найден пул из {poolMessageIds.Count} сообщений, но не релевантных вопросу";
                return response;
            }

            // Apply recency boost (same as main search)
            var now = DateTimeOffset.UtcNow;
            foreach (var r in results)
            {
                if (r.IsNewsDump)
                    r.Similarity -= 0.05;

                var timestamp = ParseTimestampFromMetadata(r.MetadataJson);
                if (timestamp != DateTimeOffset.MinValue)
                {
                    var ageInDays = (now - timestamp).TotalDays;
                    var recencyBoost = ageInDays switch
                    {
                        <= 7 => 0.10,
                        <= 30 => 0.05,
                        <= 90 => 0.02,
                        _ => 0.0
                    };
                    r.Similarity += recencyBoost;
                }
            }

            // Re-sort after adjustments
            results = results.OrderByDescending(r => r.Similarity).ToList();
            response.Results = results;

            // Calculate confidence metrics
            var best = results[0].Similarity;
            var fifth = results.Count >= 5 ? results[4].Similarity : results.Last().Similarity;
            var gap = best - fifth;

            response.BestScore = best;
            response.ScoreGap = gap;
            response.HasFullTextMatch = false; // Could add full-text within pool if needed

            // Determine confidence level (same thresholds as main search)
            (response.Confidence, response.ConfidenceReason) = EvaluateConfidence(best, gap, false);
            response.ConfidenceReason = $"[Персональный пул: {poolMessageIds.Count}] " + response.ConfidenceReason;

            _logger.LogInformation(
                "[Personal] User: {Names} | Pool: {Pool} | Best: {Best:F3} | Gap: {Gap:F3} | Confidence: {Conf}",
                string.Join("/", searchNames), poolMessageIds.Count, best, gap, response.Confidence);

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get personal context for: {User}", usernameOrName);
            response.Confidence = SearchConfidence.None;
            response.ConfidenceReason = "Ошибка поиска";
            return response;
        }
    }

    /// <summary>
    /// Vector search within a specific pool of message IDs
    /// </summary>
    private async Task<List<SearchResult>> SearchByVectorInPoolAsync(
        long chatId,
        string query,
        List<long> messageIds,
        int limit = 20,
        CancellationToken ct = default)
    {
        if (messageIds.Count == 0)
            return new List<SearchResult>();

        try
        {
            var queryEmbedding = await _embeddingClient.GetEmbeddingAsync(query, ct);
            if (queryEmbedding.Length == 0)
            {
                _logger.LogWarning("[Personal] Failed to get embedding for query: {Query}", query);
                return new List<SearchResult>();
            }

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
                    embedding <=> @Embedding::vector as Distance,
                    1 - (embedding <=> @Embedding::vector) as Similarity
                FROM message_embeddings
                WHERE chat_id = @ChatId
                  AND message_id = ANY(@MessageIds)
                ORDER BY embedding <=> @Embedding::vector
                LIMIT @Limit
                """,
                new { ChatId = chatId, Embedding = embeddingString, MessageIds = messageIds.ToArray(), Limit = limit });

            return results.Select(r =>
            {
                r.IsNewsDump = DetectNewsDump(r.ChunkText);
                return r;
            }).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to search in pool for query: {Query}", query);
            return new List<SearchResult>();
        }
    }

    private static DateTimeOffset ParseTimestampFromMetadata(string? metadataJson)
    {
        if (string.IsNullOrEmpty(metadataJson))
            return DateTimeOffset.MinValue;

        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            if (doc.RootElement.TryGetProperty("DateUtc", out var dateEl))
                return dateEl.GetDateTimeOffset();
        }
        catch
        {
            // ignored
        }

        return DateTimeOffset.MinValue;
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
    public double Distance { get; set; }

    /// <summary>
    /// Флаг: сообщение похоже на новостную простыню (много ссылок, эмодзи, длинное)
    /// </summary>
    public bool IsNewsDump { get; set; }
}

/// <summary>
/// Результат поиска с оценкой уверенности
/// </summary>
public class SearchResponse
{
    public List<SearchResult> Results { get; set; } = new();

    /// <summary>
    /// Уверенность в результатах: High, Medium, Low, None
    /// </summary>
    public SearchConfidence Confidence { get; set; }

    /// <summary>
    /// Объяснение почему такая уверенность
    /// </summary>
    public string? ConfidenceReason { get; set; }

    /// <summary>
    /// Лучший скор similarity
    /// </summary>
    public double BestScore { get; set; }

    /// <summary>
    /// Разница между top-1 и top-5 (gap)
    /// </summary>
    public double ScoreGap { get; set; }

    /// <summary>
    /// Есть ли точные совпадения по тексту (full-text)
    /// </summary>
    public bool HasFullTextMatch { get; set; }
}

public enum SearchConfidence
{
    /// <summary>Нет совпадений — не кормить LLM</summary>
    None,
    /// <summary>Слабые совпадения — предупредить пользователя</summary>
    Low,
    /// <summary>Средние совпадения — можно использовать с оговоркой</summary>
    Medium,
    /// <summary>Хорошие совпадения — уверенный ответ</summary>
    High
}

public class EmbeddingStats
{
    public int TotalEmbeddings { get; set; }
    public DateTimeOffset? OldestEmbedding { get; set; }
    public DateTimeOffset? NewestEmbedding { get; set; }
}

