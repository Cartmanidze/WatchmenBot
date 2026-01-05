using System.Text;
using Dapper;
using WatchmenBot.Features.Search.Models;
using WatchmenBot.Infrastructure.Database;
using WatchmenBot.Models;

namespace WatchmenBot.Features.Search.Services;

/// <summary>
/// Core embedding service - handles search and RAG operations.
/// Storage, personal search, and context window operations are delegated to specialized services.
/// </summary>
public class EmbeddingService(
    EmbeddingClient embeddingClient,
    IDbConnectionFactory connectionFactory,
    ILogger<EmbeddingService> logger,
    EmbeddingStorageService storageService,
    PersonalSearchService personalSearchService,
    ContextWindowService contextWindowService,
    SearchConfidenceEvaluator confidenceEvaluator)
{
    // Search scoring constants are defined in SearchConstants class

    #region Delegated Operations

    /// <summary>
    /// Stores embedding for a message (delegates to EmbeddingStorageService)
    /// </summary>
    public Task StoreMessageEmbeddingAsync(MessageRecord message, CancellationToken ct = default)
        => storageService.StoreMessageEmbeddingAsync(message, ct);

    /// <summary>
    /// Batch store embeddings for multiple messages (delegates to EmbeddingStorageService)
    /// </summary>
    public Task StoreMessageEmbeddingsBatchAsync(IEnumerable<MessageRecord> messages, CancellationToken ct = default)
        => storageService.StoreMessageEmbeddingsBatchAsync(messages, ct);

    /// <summary>
    /// Delete embeddings for a chat (delegates to EmbeddingStorageService)
    /// </summary>
    public Task DeleteChatEmbeddingsAsync(long chatId, CancellationToken ct = default)
        => storageService.DeleteChatEmbeddingsAsync(chatId, ct);

    /// <summary>
    /// Delete ALL embeddings (delegates to EmbeddingStorageService)
    /// </summary>
    public Task DeleteAllEmbeddingsAsync(CancellationToken ct = default)
        => storageService.DeleteAllEmbeddingsAsync(ct);

    /// <summary>
    /// Replace display name in embeddings (delegates to EmbeddingStorageService)
    /// </summary>
    public Task<int> RenameInEmbeddingsAsync(long? chatId, string oldName, string newName, CancellationToken ct = default)
        => storageService.RenameInEmbeddingsAsync(chatId, oldName, newName, ct);

    /// <summary>
    /// Get embedding statistics (delegates to EmbeddingStorageService)
    /// </summary>
    public Task<EmbeddingStats> GetStatsAsync(long chatId, CancellationToken ct = default)
        => storageService.GetStatsAsync(chatId, ct);

    /// <summary>
    /// Get messages from a specific user (delegates to PersonalSearchService)
    /// </summary>
    public Task<List<SearchResult>> GetUserMessagesAsync(
        long chatId,
        string usernameOrName,
        int days = 7,
        int limit = 30,
        CancellationToken ct = default)
        => personalSearchService.GetUserMessagesAsync(chatId, usernameOrName, days, limit, ct);

    /// <summary>
    /// Get messages that mention a specific user (delegates to PersonalSearchService)
    /// </summary>
    public Task<List<SearchResult>> GetMentionsOfUserAsync(
        long chatId,
        string usernameOrName,
        int days = 7,
        int limit = 20,
        CancellationToken ct = default)
        => personalSearchService.GetMentionsOfUserAsync(chatId, usernameOrName, days, limit, ct);

    /// <summary>
    /// Combined personal retrieval (delegates to PersonalSearchService)
    /// Supports both user_id (preferred, stable) and name-based search (fallback)
    /// </summary>
    public Task<SearchResponse> GetPersonalContextAsync(
        long chatId,
        string usernameOrName,
        string? displayName,
        string question,
        int days = 7,
        long? userId = null,
        CancellationToken ct = default)
        => personalSearchService.GetPersonalContextAsync(chatId, usernameOrName, displayName, question, days, userId, ct);

    /// <summary>
    /// Get merged context windows (delegates to ContextWindowService)
    /// </summary>
    public Task<List<ContextWindow>> GetMergedContextWindowsAsync(
        long chatId,
        List<long> messageIds,
        int windowSize = 2,
        CancellationToken ct = default)
        => contextWindowService.GetMergedContextWindowsAsync(chatId, messageIds, windowSize, ct);

    #endregion

    #region Search Operations

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
            using var connection = await connectionFactory.CreateConnectionAsync();
            var embeddingCount = await connection.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM message_embeddings WHERE chat_id = @ChatId",
                new { ChatId = chatId });

            logger.LogInformation("[Search] Chat {ChatId} has {Count} embeddings", chatId, embeddingCount);

            if (embeddingCount == 0)
            {
                logger.LogWarning("[Search] No embeddings found for chat {ChatId}", chatId);
                return [];
            }

            var queryEmbedding = await embeddingClient.GetEmbeddingAsync(query, EmbeddingTask.RetrievalQuery, ct);
            if (queryEmbedding.Length == 0)
            {
                logger.LogWarning("[Search] Failed to get embedding for query: {Query}", query);
                return [];
            }

            logger.LogDebug("[Search] Got query embedding with {Dims} dimensions", queryEmbedding.Length);

            var results = await SearchByVectorAsync(chatId, queryEmbedding, limit, ct, queryText: query);
            logger.LogInformation("[Search] Found {Count} results for query in chat {ChatId} (hybrid)", results.Count, chatId);

            return results;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to search similar messages for query: {Query}", query);
            return [];
        }
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
            var queryEmbedding = await embeddingClient.GetEmbeddingAsync(query, EmbeddingTask.RetrievalQuery, ct);
            if (queryEmbedding.Length == 0)
                return [];

            using var connection = await connectionFactory.CreateConnectionAsync();
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
                r.IsNewsDump = NewsDumpDetector.IsNewsDump(r.ChunkText);
                return r;
            }).ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to search similar messages in range for query: {Query}", query);
            return [];
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
            using var connection = await connectionFactory.CreateConnectionAsync();

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
            logger.LogWarning(ex, "Failed to get diverse messages for chat {ChatId}", chatId);
            return [];
        }
    }

    /// <summary>
    /// Check if embedding exists for a message
    /// </summary>
    public async Task<bool> HasEmbeddingAsync(long chatId, long messageId, CancellationToken ct = default)
    {
        using var connection = await connectionFactory.CreateConnectionAsync();

        var exists = await connection.ExecuteScalarAsync<bool>(
            "SELECT EXISTS(SELECT 1 FROM message_embeddings WHERE chat_id = @ChatId AND message_id = @MessageId)",
            new { ChatId = chatId, MessageId = messageId });

        return exists;
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
            var fullTextResults = await FullTextSearchAsync(chatId, query, 10, ct);
            response.HasFullTextMatch = fullTextResults.Count > 0;

            // Then, vector search
            var results = await SearchSimilarAsync(chatId, query, limit, ct);

            // Merge full-text results with vector results (full-text takes priority)
            if (fullTextResults.Count > 0)
            {
                var existingIds = results.Select(r => r.MessageId).ToHashSet();
                var newResults = fullTextResults.Where(r => !existingIds.Contains(r.MessageId)).ToList();

                if (newResults.Count > 0)
                {
                    logger.LogInformation("[Search] Full-text added {Count} exact matches", newResults.Count);
                    results = newResults.Concat(results).ToList();
                }
            }

            if (results.Count == 0)
            {
                response.Confidence = SearchConfidence.None;
                response.ConfidenceReason = "Нет embeddings в этом чате";
                return response;
            }

            // Apply adjustments: news dump penalty + recency boost, then re-sort
            results = confidenceEvaluator.ApplyAdjustmentsAndSort(results);
            response.Results = results;

            // Calculate confidence metrics
            var best = results[0].Similarity;
            var gap = confidenceEvaluator.CalculateGap(results);

            response.BestScore = best;
            response.ScoreGap = gap;

            // Determine confidence level
            (response.Confidence, response.ConfidenceReason) = confidenceEvaluator.Evaluate(best, gap, response.HasFullTextMatch);

            // Try ILIKE fallback for exact matches
            var ilikeResults = await SimpleTextSearchAsync(chatId, query, 10, ct);
            if (ilikeResults.Count > 0)
            {
                var existingIds = results.Select(r => r.MessageId).ToHashSet();
                var newResults = ilikeResults.Where(r => !existingIds.Contains(r.MessageId)).ToList();

                if (newResults.Count > 0)
                {
                    logger.LogInformation("[Search] ILIKE fallback added {Count} new results (total: {Total})",
                        newResults.Count, ilikeResults.Count);

                    results.AddRange(newResults);
                    results = results.OrderByDescending(r => r.Similarity).ToList();
                    response.Results = results;

                    var newBest = results[0].Similarity;
                    if (newBest > best || !response.HasFullTextMatch)
                    {
                        response.HasFullTextMatch = true;
                        best = newBest;
                        response.BestScore = best;
                        (response.Confidence, response.ConfidenceReason) = confidenceEvaluator.Evaluate(best, gap, true);
                        response.ConfidenceReason += " (ILIKE boost)";
                    }
                }
            }

            logger.LogInformation(
                "[Search] Query: '{Query}' | Best: {Best:F3} | Gap: {Gap:F3} | FullText: {FT} | Confidence: {Conf}",
                TextSearchHelpers.TruncateForLog(query, 50), best, gap, response.HasFullTextMatch, response.Confidence);

            return response;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to search with confidence for query: {Query}", query);
            response.Confidence = SearchConfidence.None;
            response.ConfidenceReason = "Ошибка поиска";
            return response;
        }
    }

    #endregion

    #region Text Search Operations

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
            using var connection = await connectionFactory.CreateConnectionAsync();

            var searchTerms = TextSearchHelpers.ExtractSearchTerms(query);
            if (string.IsNullOrWhiteSpace(searchTerms))
                return [];

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
            logger.LogDebug(ex, "Full-text search failed for query: {Query}", query);
            return [];
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
            var rawWords = TextSearchHelpers.ExtractIlikeWords(query);
            if (rawWords.Count == 0)
                return [];

            var words = TextSearchHelpers.ExpandWithStems(rawWords);

            logger.LogDebug("[ILIKE] Words: [{Raw}] + stems: [{All}]",
                string.Join(", ", rawWords), string.Join(", ", words));

            using var connection = await connectionFactory.CreateConnectionAsync();

            var wordList = words.ToList();
            var conditions = wordList.Select((w, i) => $"LOWER(chunk_text) LIKE @Word{i}").ToList();
            var whereClause = string.Join(" OR ", conditions);

            var parameters = new DynamicParameters();
            parameters.Add("ChatId", chatId);
            parameters.Add("Limit", limit);
            for (var i = 0; i < wordList.Count; i++)
            {
                parameters.Add($"Word{i}", $"%{wordList[i]}%");
            }

            var embeddingsSql = $"""
                SELECT
                    me.chat_id as ChatId,
                    me.message_id as MessageId,
                    me.chunk_index as ChunkIndex,
                    me.chunk_text as ChunkText,
                    me.metadata as MetadataJson,
                    0.0 as Distance,
                    0.95 as Similarity
                FROM message_embeddings me
                JOIN messages m ON me.chat_id = m.chat_id AND me.message_id = m.id
                WHERE me.chat_id = @ChatId AND ({whereClause})
                ORDER BY m.date_utc DESC
                LIMIT @Limit
                """;

            var results = (await connection.QueryAsync<SearchResult>(embeddingsSql, parameters)).ToList();

            logger.LogDebug("[ILIKE] Embeddings: {Count} results for words: {Words}",
                results.Count, string.Join(", ", wordList));

            // Fallback to raw messages if no embeddings found
            if (results.Count == 0)
            {
                var messagesConditions = wordList.Select((w, i) => $"LOWER(text) LIKE @Word{i}").ToList();
                var messagesWhere = string.Join(" OR ", messagesConditions);

                parameters.Add("Since", DateTimeOffset.UtcNow.AddDays(-30));

                var messagesSql = $"""
                    SELECT
                        chat_id as ChatId,
                        id as MessageId,
                        0 as ChunkIndex,
                        text as ChunkText,
                        jsonb_build_object(
                            'Username', username,
                            'DisplayName', display_name,
                            'DateUtc', date_utc
                        )::text as MetadataJson,
                        0.0 as Distance,
                        0.9 as Similarity
                    FROM messages
                    WHERE chat_id = @ChatId
                      AND text IS NOT NULL
                      AND date_utc >= @Since
                      AND ({messagesWhere})
                    ORDER BY date_utc DESC
                    LIMIT @Limit
                    """;

                var rawResults = (await connection.QueryAsync<SearchResult>(messagesSql, parameters)).ToList();

                if (rawResults.Count > 0)
                {
                    logger.LogInformation("[ILIKE] Raw messages fallback found {Count} results (last 30 days)",
                        rawResults.Count);
                    results.AddRange(rawResults);
                }
            }

            logger.LogDebug("[ILIKE] Total: {Count} results for words: {Words}",
                results.Count, string.Join(", ", wordList));

            return results;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "ILIKE search failed for query: {Query}", query);
            return [];
        }
    }

    #endregion

    #region Private Helpers

    private async Task<List<SearchResult>> SearchByVectorAsync(
        long chatId,
        float[] queryEmbedding,
        int limit,
        CancellationToken ct,
        string? queryText = null)
    {
        using var connection = await connectionFactory.CreateConnectionAsync();

        // Parse query for hybrid search components
        var (searchTerms, exactMatchWords, useHybrid) = VectorSearchBase.ParseQuery(queryText);

        // Build SQL components using base class helpers
        var exactMatchSql = VectorSearchBase.BuildExactMatchSql("me.chunk_text", exactMatchWords);
        var similaritySql = VectorSearchBase.BuildSimilaritySql(
            embeddingColumn: "me.embedding",
            textColumn: "me.chunk_text",
            dateColumn: "m.date_utc",
            exactMatchSql: exactMatchSql,
            useHybrid: useHybrid);

        var sql = $"""
            SELECT
                me.chat_id as ChatId,
                me.message_id as MessageId,
                me.chunk_index as ChunkIndex,
                me.chunk_text as ChunkText,
                me.metadata as MetadataJson,
                me.embedding <=> @Embedding::vector as Distance,
                {similaritySql} as Similarity
            FROM message_embeddings me
            JOIN messages m ON me.chat_id = m.chat_id AND me.message_id = m.id
            WHERE me.chat_id = @ChatId
            ORDER BY Similarity DESC
            LIMIT @Limit
            """;

        var parameters = VectorSearchBase.BuildSearchParameters(chatId, queryEmbedding, searchTerms, exactMatchWords, limit);
        var results = await connection.QueryAsync<SearchResult>(sql, parameters);

        logger.LogDebug("[Search] Hybrid={Hybrid}, Terms='{Terms}', ExactWords=[{Words}]",
            useHybrid, searchTerms ?? "none", string.Join(",", exactMatchWords));

        return results.Select(r =>
        {
            r.IsNewsDump = NewsDumpDetector.IsNewsDump(r.ChunkText);
            return r;
        }).ToList();
    }


    #endregion
}
