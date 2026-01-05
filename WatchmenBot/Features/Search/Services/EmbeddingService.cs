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

    #endregion

    #region Vector Search Helpers

    /// <summary>
    /// Search using a pre-computed embedding vector.
    /// Use this for batch operations where embeddings are already available.
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
