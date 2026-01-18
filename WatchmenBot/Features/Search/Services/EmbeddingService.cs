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
    ContextWindowService contextWindowService)
{

    #region Delegated Operations

    /// <summary>
    /// Stores embedding for a message (delegates to EmbeddingStorageService).
    /// Returns true if embedding was stored, false if skipped.
    /// </summary>
    public Task<bool> StoreMessageEmbeddingAsync(MessageRecord message, CancellationToken ct = default)
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
    /// Combined personal retrieval with multiple search names (B+C solution).
    /// Searches using ALL provided name variants: LLM-normalized, original mentions, and DB aliases.
    /// </summary>
    public Task<SearchResponse> GetPersonalContextAsync(
        long chatId,
        List<string> searchNames,
        string question,
        int days = 7,
        long? userId = null,
        CancellationToken ct = default)
        => personalSearchService.GetPersonalContextAsync(chatId, searchNames, question, days, userId, ct);

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
                    1 - (me.embedding <=> @Embedding::vector) as Similarity,
                    COALESCE(me.is_question, FALSE) as IsQuestionEmbedding
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
                // Support both DB flag and legacy ChunkIndex < 0 convention
                r.IsQuestionEmbedding = r.IsQuestionEmbedding || r.ChunkIndex < 0;
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

    #endregion

    #region Vector Search Helpers

    /// <summary>
    /// True hybrid search: Vector (HNSW) + Full-Text (GIN) with RRF fusion in PostgreSQL.
    /// Uses the GIN index on to_tsvector('russian', chunk_text) for keyword matching.
    /// </summary>
    public async Task<List<SearchResult>> HybridSearchAsync(
        long chatId,
        float[] queryEmbedding,
        string keywordQuery,
        int limit,
        CancellationToken ct = default)
    {
        using var connection = await connectionFactory.CreateConnectionAsync();
        var embeddingString = "[" + string.Join(",", queryEmbedding) + "]";

        // Clean keyword query for tsquery (remove special characters)
        var cleanedKeywords = CleanForTsQuery(keywordQuery);

        if (string.IsNullOrWhiteSpace(cleanedKeywords))
        {
            // Fallback to pure vector search if no valid keywords
            return await SearchByVectorAsync(chatId, queryEmbedding, limit, ct, keywordQuery);
        }

        const int rrfK = 60; // RRF constant
        var candidateLimit = Math.Min(limit * 5, 150);

        // True hybrid search with RRF fusion in SQL
        // Both vector and keyword searches run in parallel, then combined
        var hybridSql = $"""
            WITH vector_search AS (
                SELECT
                    me.chat_id,
                    me.message_id,
                    me.chunk_index,
                    me.chunk_text,
                    me.metadata,
                    me.embedding <=> @Embedding::vector as distance,
                    COALESCE(me.is_question, FALSE) as is_question,
                    ROW_NUMBER() OVER (ORDER BY me.embedding <=> @Embedding::vector) as vector_rank
                FROM message_embeddings me
                WHERE me.chat_id = @ChatId
                ORDER BY me.embedding <=> @Embedding::vector
                LIMIT @CandidateLimit
            ),
            keyword_search AS (
                SELECT
                    me.chat_id,
                    me.message_id,
                    me.chunk_index,
                    me.chunk_text,
                    me.metadata,
                    COALESCE(me.is_question, FALSE) as is_question,
                    ts_rank_cd(to_tsvector('russian', me.chunk_text), query) as text_rank,
                    ROW_NUMBER() OVER (ORDER BY ts_rank_cd(to_tsvector('russian', me.chunk_text), query) DESC) as keyword_rank
                FROM message_embeddings me,
                     to_tsquery('russian', @Keywords) query
                WHERE me.chat_id = @ChatId
                  AND to_tsvector('russian', me.chunk_text) @@ query
                ORDER BY ts_rank_cd(to_tsvector('russian', me.chunk_text), query) DESC
                LIMIT @CandidateLimit
            )
            SELECT
                COALESCE(v.chat_id, k.chat_id) as ChatId,
                COALESCE(v.message_id, k.message_id) as MessageId,
                COALESCE(v.chunk_index, k.chunk_index) as ChunkIndex,
                COALESCE(v.chunk_text, k.chunk_text) as ChunkText,
                COALESCE(v.metadata, k.metadata) as MetadataJson,
                COALESCE(v.distance, 1.0) as Distance,
                -- RRF fusion: sum of reciprocal ranks
                COALESCE(1.0 / ({rrfK} + v.vector_rank), 0) +
                COALESCE(1.0 / ({rrfK} + k.keyword_rank), 0) as Similarity,
                -- Q→A bridge flag (TRUE if either source is a question embedding)
                COALESCE(v.is_question, k.is_question, FALSE) as IsQuestionEmbedding,
                -- Debug info
                v.vector_rank,
                k.keyword_rank,
                k.text_rank
            FROM vector_search v
            FULL OUTER JOIN keyword_search k
                ON v.chat_id = k.chat_id
                AND v.message_id = k.message_id
                AND v.chunk_index = k.chunk_index
            ORDER BY
                COALESCE(1.0 / ({rrfK} + v.vector_rank), 0) +
                COALESCE(1.0 / ({rrfK} + k.keyword_rank), 0) DESC
            LIMIT @Limit
            """;

        try
        {
            var results = await connection.QueryAsync<HybridSearchResult>(
                hybridSql,
                new { ChatId = chatId, Embedding = embeddingString, Keywords = cleanedKeywords, CandidateLimit = candidateLimit, Limit = limit });

            var resultList = results.ToList();

            // Log hybrid search effectiveness
            var vectorOnly = resultList.Count(r => r.KeywordRank == null);
            var keywordOnly = resultList.Count(r => r.VectorRank == null);
            var both = resultList.Count(r => r.VectorRank != null && r.KeywordRank != null);

            logger.LogInformation(
                "[HybridSearch] Query: '{Query}' → {Total} results (vector-only: {VectorOnly}, keyword-only: {KeywordOnly}, both: {Both})",
                keywordQuery.Length > 40 ? keywordQuery[..40] + "..." : keywordQuery,
                resultList.Count, vectorOnly, keywordOnly, both);

            return resultList.Select(r => new SearchResult
            {
                ChatId = r.ChatId,
                MessageId = r.MessageId,
                ChunkIndex = r.ChunkIndex,
                ChunkText = r.ChunkText,
                MetadataJson = r.MetadataJson,
                Distance = r.Distance,
                Similarity = r.Similarity,
                IsNewsDump = NewsDumpDetector.IsNewsDump(r.ChunkText),
                IsQuestionEmbedding = r.IsQuestionEmbedding || r.ChunkIndex < 0 // Both DB flag and legacy convention
            }).ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[HybridSearch] Failed for keywords '{Keywords}', falling back to vector search", cleanedKeywords);
            return await SearchByVectorAsync(chatId, queryEmbedding, limit, ct, keywordQuery);
        }
    }

    /// <summary>
    /// Clean text for PostgreSQL tsquery (removes special characters, creates OR query)
    /// </summary>
    private static string CleanForTsQuery(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        // Extract words (Russian and English letters, digits)
        var words = System.Text.RegularExpressions.Regex.Matches(text.ToLowerInvariant(), @"[\p{L}\d]+")
            .Select(m => m.Value)
            .Where(w => w.Length >= 2) // Skip single chars
            .Distinct()
            .ToList();

        if (words.Count == 0)
            return "";

        // Join with | for OR query (more permissive than AND)
        return string.Join(" | ", words);
    }

    /// <summary>
    /// Internal model for hybrid search results with debug info
    /// </summary>
    private class HybridSearchResult
    {
        public long ChatId { get; set; }
        public long MessageId { get; set; }
        public int ChunkIndex { get; set; }
        public string ChunkText { get; set; } = "";
        public string? MetadataJson { get; set; }
        public double Distance { get; set; }
        public double Similarity { get; set; }
        public bool IsQuestionEmbedding { get; set; }
        public int? VectorRank { get; set; }
        public int? KeywordRank { get; set; }
        public double? TextRank { get; set; }
    }

    /// <summary>
    /// Search using a pre-computed embedding vector.
    /// Uses two-stage retrieval: HNSW index for candidates, then hybrid re-ranking.
    /// </summary>
    public async Task<List<SearchResult>> SearchByVectorAsync(
        long chatId,
        float[] queryEmbedding,
        int limit,
        CancellationToken ct,
        string? queryText = null)
    {
        using var connection = await connectionFactory.CreateConnectionAsync();

        // Parse query for hybrid search components
        var (searchTerms, exactMatchWords, useHybrid) = VectorSearchBase.ParseQuery(queryText);
        var embeddingString = "[" + string.Join(",", queryEmbedding) + "]";

        // Stage 1: Get candidates using HNSW index (fast O(log n) search)
        // Fetch more candidates than needed for better re-ranking quality
        var candidateMultiplier = useHybrid ? 10 : 5; // More candidates for hybrid to improve BM25 recall
        var candidateLimit = Math.Min(limit * candidateMultiplier, 200);

        var candidatesSql = """
            SELECT
                me.chat_id as ChatId,
                me.message_id as MessageId,
                me.chunk_index as ChunkIndex,
                me.chunk_text as ChunkText,
                me.metadata as MetadataJson,
                me.embedding <=> @Embedding::vector as Distance,
                m.date_utc as DateUtc,
                COALESCE(me.is_question, FALSE) as IsQuestionEmbedding
            FROM message_embeddings me
            JOIN messages m ON me.chat_id = m.chat_id AND me.message_id = m.id
            WHERE me.chat_id = @ChatId
            ORDER BY me.embedding <=> @Embedding::vector
            LIMIT @CandidateLimit
            """;

        var candidates = (await connection.QueryAsync<SearchResultWithDate>(
            candidatesSql,
            new { ChatId = chatId, Embedding = embeddingString, CandidateLimit = candidateLimit }))
            .ToList();

        if (candidates.Count == 0)
        {
            return [];
        }

        // Stage 2: Re-rank candidates using hybrid scoring in memory
        var now = DateTime.UtcNow;
        var searchTermsLower = searchTerms?.ToLowerInvariant();
        var exactWordsLower = exactMatchWords.Select(w => w.ToLowerInvariant()).ToList();

        foreach (var c in candidates)
        {
            var vectorScore = 1.0 - c.Distance;
            var textLower = c.ChunkText?.ToLowerInvariant() ?? "";

            // BM25-like text score (simplified for in-memory)
            double textScore = 0;
            if (useHybrid && !string.IsNullOrEmpty(searchTermsLower))
            {
                var terms = searchTermsLower.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var matchCount = terms.Count(t => textLower.Contains(t));
                textScore = terms.Length > 0 ? (double)matchCount / terms.Length : 0;
            }

            // Exact match boost
            double exactBoost = exactWordsLower.Any(w => textLower.Contains(w))
                ? SearchConstants.ExactMatchBoost : 0;

            // Time decay
            var daysSince = (now - c.DateUtc).TotalDays;
            var timeDecay = SearchConstants.TimeDecayWeight *
                Math.Exp(-Math.Max(0, daysSince) * Math.Log(2) / SearchConstants.TimeDecayHalfLifeDays);

            // Combined score
            c.Similarity = useHybrid
                ? SearchConstants.DenseWeight * vectorScore
                  + SearchConstants.SparseWeight * textScore
                  + exactBoost
                  + timeDecay
                : vectorScore + exactBoost + timeDecay;
        }

        // Sort by hybrid score and take top results
        var results = candidates
            .OrderByDescending(c => c.Similarity)
            .Take(limit)
            .Select(c => new SearchResult
            {
                ChatId = c.ChatId,
                MessageId = c.MessageId,
                ChunkIndex = c.ChunkIndex,
                ChunkText = c.ChunkText,
                MetadataJson = c.MetadataJson,
                Distance = c.Distance,
                Similarity = c.Similarity,
                IsNewsDump = NewsDumpDetector.IsNewsDump(c.ChunkText),
                IsQuestionEmbedding = c.IsQuestionEmbedding || c.ChunkIndex < 0 // Both DB flag and legacy convention
            })
            .ToList();

        logger.LogDebug("[Search] Two-stage: {Candidates} candidates → {Results} results, Hybrid={Hybrid}, Terms='{Terms}'",
            candidates.Count, results.Count, useHybrid, searchTerms ?? "none");

        return results;
    }

    /// <summary>
    /// Internal model for two-stage retrieval with date for time decay calculation
    /// </summary>
    private class SearchResultWithDate
    {
        public long ChatId { get; set; }
        public long MessageId { get; set; }
        public int ChunkIndex { get; set; }
        public string ChunkText { get; set; } = "";
        public string? MetadataJson { get; set; }
        public double Distance { get; set; }
        public double Similarity { get; set; }
        public DateTime DateUtc { get; set; }
        public bool IsQuestionEmbedding { get; set; }
    }

    /// <summary>
    /// Get embeddings for multiple queries in a single batch API call.
    /// Much faster than calling GetEmbeddingAsync for each query separately.
    /// </summary>
    public Task<List<float[]>> GetBatchEmbeddingsAsync(
        IEnumerable<string> queries,
        CancellationToken ct = default)
    {
        return embeddingClient.GetEmbeddingsAsync(queries, EmbeddingTask.RetrievalQuery, ct);
    }

    #endregion
}
