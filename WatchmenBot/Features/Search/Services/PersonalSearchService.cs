using System.Text.Json;
using Dapper;
using WatchmenBot.Features.Search.Models;
using WatchmenBot.Infrastructure.Database;
using WatchmenBot.Infrastructure.Settings;
using WatchmenBot.Features.Messages.Services;
using WatchmenBot.Features.Admin.Services;
using WatchmenBot.Features.Webhook.Services;

namespace WatchmenBot.Features.Search.Services;

/// <summary>
/// Service for personal/user-specific search queries.
/// Handles questions like "когда Я говорил..." or "@username когда..."
/// </summary>
public class PersonalSearchService(
    IDbConnectionFactory connectionFactory,
    EmbeddingClient embeddingClient,
    ILogger<PersonalSearchService> logger)
{
    // Hybrid search weights
    private const double DenseWeight = 0.7;  // 70% semantic
    private const double SparseWeight = 0.3;  // 30% keyword

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
            // OPTIMIZED: Single query instead of 2N queries (4x faster for 2 names)
            var poolMessageIds = await GetPersonalMessagePoolAsync(chatId, searchNames, days, ct);

            logger.LogInformation(
                "[Personal] User: {Names} | Pool size: {Count} messages (optimized single query)",
                string.Join("/", searchNames), poolMessageIds.Count);

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

            // Re-sort after adjustments (primary: similarity, secondary: date for tie-breaking)
            results = results
                .OrderByDescending(r => r.Similarity)
                .ThenByDescending(r => ParseTimestampFromMetadata(r.MetadataJson))
                .ToList();
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
            var queryEmbedding = await embeddingClient.GetEmbeddingAsync(query, ct);
            if (queryEmbedding.Length == 0)
            {
                logger.LogWarning("[Personal] Failed to get embedding for query: {Query}", query);
                return [];
            }

            using var connection = await connectionFactory.CreateConnectionAsync();
            var embeddingString = "[" + string.Join(",", queryEmbedding) + "]";

            // Extract search terms for hybrid scoring
            var searchTerms = ExtractSearchTerms(query);
            var useHybrid = !string.IsNullOrWhiteSpace(searchTerms);

            var sql = useHybrid
                ? $"""
                    SELECT
                        chat_id as ChatId,
                        message_id as MessageId,
                        chunk_index as ChunkIndex,
                        chunk_text as ChunkText,
                        metadata as MetadataJson,
                        embedding <=> @Embedding::vector as Distance,
                        -- Hybrid score
                        {DenseWeight} * (1 - (embedding <=> @Embedding::vector))
                        + {SparseWeight} * COALESCE(
                            ts_rank_cd(
                                to_tsvector('russian', chunk_text),
                                websearch_to_tsquery('russian', @SearchTerms),
                                32
                            ),
                            0
                        ) as Similarity
                    FROM message_embeddings
                    WHERE chat_id = @ChatId
                      AND message_id = ANY(@MessageIds)
                    ORDER BY Similarity DESC
                    LIMIT @Limit
                    """
                : """
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
                    """;

            var results = await connection.QueryAsync<SearchResult>(
                sql,
                new { ChatId = chatId, Embedding = embeddingString, SearchTerms = searchTerms, MessageIds = messageIds.ToArray(), Limit = limit });

            return results.Select(r =>
            {
                r.IsNewsDump = DetectNewsDump(r.ChunkText);
                return r;
            }).ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to search in pool for query: {Query}", query);
            return [];
        }
    }

    #region Helper Methods

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
            .Split([' ', ',', '.', '!', '?', ':', ';', '-', '(', ')', '[', ']', '"', '\''], StringSplitOptions.RemoveEmptyEntries)
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

    /// <summary>
    /// Parse timestamp from JSON metadata
    /// </summary>
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

    #endregion
}
