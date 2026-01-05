using Dapper;
using WatchmenBot.Features.Search.Models;
using WatchmenBot.Infrastructure.Database;

namespace WatchmenBot.Features.Search.Services;

/// <summary>
/// Service for personal/user-specific search queries.
/// Handles questions like "когда Я говорил..." or "@username когда..."
/// </summary>
public class PersonalSearchService(
    IDbConnectionFactory connectionFactory,
    EmbeddingClient embeddingClient,
    SearchConfidenceEvaluator confidenceEvaluator,
    ILogger<PersonalSearchService> logger)
{
    // Search scoring constants are defined in SearchConstants class

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
            using var connection = await connectionFactory.CreateConnectionAsync();

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

            var searchResults = results as SearchResult[] ?? results.ToArray();
            
            logger.LogInformation("[Search] Found {Count} messages from user '{User}' in chat {ChatId}",
                searchResults.Length, cleanName, chatId);

            return searchResults.ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to get messages for user: {User}", usernameOrName);
            return [];
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
            using var connection = await connectionFactory.CreateConnectionAsync();

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

            var searchResults = results as SearchResult[] ?? results.ToArray();
            
            logger.LogInformation("[Search] Found {Count} mentions of user '{User}' in chat {ChatId}",
                searchResults.Length, cleanName, chatId);

            return searchResults.ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to get mentions for user: {User}", usernameOrName);
            return [];
        }
    }

    /// <summary>
    /// Combined personal retrieval: user's messages + mentions of user
    /// Now with proper vector search within the pool!
    /// Supports both user_id (preferred, stable) and name-based search (fallback)
    /// </summary>
    public async Task<SearchResponse> GetPersonalContextAsync(
        long chatId,
        string usernameOrName,
        string? displayName,
        string question,  // The actual question to search for relevance
        int days = 7,
        long? userId = null,  // If provided, uses stable user_id for search
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
            // If user_id is provided, use it for precise filtering (works across name changes)
            var poolMessageIds = userId.HasValue
                ? await GetPersonalMessagePoolByUserIdAsync(chatId, userId.Value, searchNames, days, ct)
                : await GetPersonalMessagePoolAsync(chatId, searchNames, days, ct);

            logger.LogInformation(
                "[Personal] User: {UserId}/{Names} | Pool size: {Count} messages",
                userId?.ToString() ?? "none", string.Join("/", searchNames), poolMessageIds.Count);

            if (poolMessageIds.Count == 0)
            {
                response.Confidence = SearchConfidence.None;
                response.ConfidenceReason = "Пользователь не найден в истории чата";
                return response;
            }

            // Step 2: Vector search WITHIN this pool using the question
            var results = await SearchByVectorInPoolAsync(chatId, question, poolMessageIds, 20, ct);

            if (results.Count == 0)
            {
                response.Confidence = SearchConfidence.Low;
                response.ConfidenceReason = $"Найден пул из {poolMessageIds.Count} сообщений, но не релевантных вопросу";
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
            response.HasFullTextMatch = false; // Could add full-text within pool if needed

            // Determine confidence level (same thresholds as main search)
            (response.Confidence, response.ConfidenceReason) = confidenceEvaluator.Evaluate(best, gap, false);
            response.ConfidenceReason = $"[Персональный пул: {poolMessageIds.Count}] " + response.ConfidenceReason;

            logger.LogInformation(
                "[Personal] User: {Names} | Pool: {Pool} | Best: {Best:F3} | Gap: {Gap:F3} | Confidence: {Conf}",
                string.Join("/", searchNames), poolMessageIds.Count, best, gap, response.Confidence);

            return response;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to get personal context for: {User}", usernameOrName);
            response.Confidence = SearchConfidence.None;
            response.ConfidenceReason = "Ошибка поиска";
            return response;
        }
    }

    /// <summary>
    /// Get pool of message IDs for personal search (user's messages + mentions)
    /// OPTIMIZED: Single query instead of 2N queries
    /// </summary>
    private async Task<List<long>> GetPersonalMessagePoolAsync(
        long chatId,
        List<string> searchNames,
        int days = 7,
        CancellationToken ct = default)
    {
        if (searchNames.Count == 0)
            return [];

        try
        {
            using var connection = await connectionFactory.CreateConnectionAsync();

            var startDate = DateTime.UtcNow.AddDays(-days);
            var cleanNames = searchNames.Select(n => n.TrimStart('@')).ToArray();

            // OPTIMIZATION: Single query with UNION to get both user's messages and mentions
            var messageIds = await connection.QueryAsync<long>(
                """
                -- User's own messages (by username or display name)
                SELECT DISTINCT me.message_id
                FROM message_embeddings me
                JOIN messages m ON me.chat_id = m.chat_id AND me.message_id = m.id
                WHERE me.chat_id = @ChatId
                  AND m.date_utc >= @StartDate
                  AND (
                      me.metadata->>'Username' = ANY(@Names)
                      OR me.metadata->>'DisplayName' = ANY(@Names)
                      OR me.chunk_text ILIKE ANY(@TextPatterns)
                  )
                LIMIT 100

                UNION

                -- Mentions of user (text contains name, but NOT from user themselves)
                SELECT DISTINCT me.message_id
                FROM message_embeddings me
                JOIN messages m ON me.chat_id = m.chat_id AND me.message_id = m.id
                WHERE me.chat_id = @ChatId
                  AND m.date_utc >= @StartDate
                  AND me.chunk_text ILIKE ANY(@MentionPatterns)
                  AND NOT (
                      me.metadata->>'Username' = ANY(@Names)
                      OR me.metadata->>'DisplayName' = ANY(@Names)
                  )
                LIMIT 50
                """,
                new
                {
                    ChatId = chatId,
                    StartDate = startDate,
                    Names = cleanNames,
                    TextPatterns = cleanNames.Select(n => $"{n}:%").ToArray(), // "Name: message..."
                    MentionPatterns = cleanNames.Select(n => $"%{n}%").ToArray() // Mentions in text
                });

            var enumerable = messageIds as long[] ?? messageIds.ToArray();
            
            logger.LogDebug("[Personal] Found {Count} message IDs for names: {Names}",
                enumerable.Length, string.Join(", ", cleanNames));

            return enumerable.Distinct().ToList(); // Distinct to deduplicate UNION results
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to get personal message pool for: {Names}",
                string.Join(", ", searchNames));
            return [];
        }
    }

    /// <summary>
    /// Get pool of message IDs for personal search using stable user_id.
    /// This works correctly even when user changes their display name.
    /// </summary>
    private async Task<List<long>> GetPersonalMessagePoolByUserIdAsync(
        long chatId,
        long userId,
        List<string> searchNames,
        int days = 7,
        CancellationToken ct = default)
    {
        try
        {
            using var connection = await connectionFactory.CreateConnectionAsync();

            var startDate = DateTime.UtcNow.AddDays(-days);
            var cleanNames = searchNames.Select(n => n.TrimStart('@')).ToArray();

            // Search by user_id (stable) for user's own messages
            // Plus mentions by name patterns in other messages
            var messageIds = await connection.QueryAsync<long>(
                """
                -- User's own messages (by stable user_id)
                SELECT DISTINCT me.message_id
                FROM message_embeddings me
                JOIN messages m ON me.chat_id = m.chat_id AND me.message_id = m.id
                WHERE me.chat_id = @ChatId
                  AND m.date_utc >= @StartDate
                  AND m.from_user_id = @UserId
                LIMIT 100

                UNION

                -- Mentions of user (text contains any known name, but NOT from user themselves)
                SELECT DISTINCT me.message_id
                FROM message_embeddings me
                JOIN messages m ON me.chat_id = m.chat_id AND me.message_id = m.id
                WHERE me.chat_id = @ChatId
                  AND m.date_utc >= @StartDate
                  AND me.chunk_text ILIKE ANY(@MentionPatterns)
                  AND m.from_user_id != @UserId
                LIMIT 50
                """,
                new
                {
                    ChatId = chatId,
                    StartDate = startDate,
                    UserId = userId,
                    MentionPatterns = cleanNames.Select(n => $"%{n}%").ToArray()
                });

            var enumerable = messageIds as long[] ?? messageIds.ToArray();

            logger.LogDebug("[Personal] Found {Count} message IDs for user_id: {UserId} (names: {Names})",
                enumerable.Length, userId, string.Join(", ", cleanNames));

            return enumerable.Distinct().ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to get personal message pool for user_id: {UserId}", userId);
            return [];
        }
    }

    /// <summary>
    /// Vector search within a specific pool of message IDs (with hybrid scoring)
    /// </summary>
    private async Task<List<SearchResult>> SearchByVectorInPoolAsync(
        long chatId,
        string query,
        List<long> messageIds,
        int limit = 20,
        CancellationToken ct = default)
    {
        if (messageIds.Count == 0)
            return [];

        try
        {
            var queryEmbedding = await embeddingClient.GetEmbeddingAsync(query, EmbeddingTask.RetrievalQuery, ct);
            if (queryEmbedding.Length == 0)
            {
                logger.LogWarning("[Personal] Failed to get embedding for query: {Query}", query);
                return [];
            }

            using var connection = await connectionFactory.CreateConnectionAsync();

            // Parse query for hybrid search components
            var (searchTerms, exactMatchWords, useHybrid) = VectorSearchBase.ParseQuery(query);

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
                  AND me.message_id = ANY(@MessageIds)
                ORDER BY Similarity DESC
                LIMIT @Limit
                """;

            var parameters = VectorSearchBase.BuildSearchParameters(chatId, queryEmbedding, searchTerms, exactMatchWords, limit);
            parameters.Add("MessageIds", messageIds.ToArray());

            var results = await connection.QueryAsync<SearchResult>(sql, parameters);

            return results.Select(r =>
            {
                r.IsNewsDump = NewsDumpDetector.IsNewsDump(r.ChunkText);
                return r;
            }).ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to search in pool for query: {Query}", query);
            return [];
        }
    }

}
