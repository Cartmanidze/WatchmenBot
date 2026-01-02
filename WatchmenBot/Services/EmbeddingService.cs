using System.Text;
using System.Text.Json;
using Dapper;
using WatchmenBot.Infrastructure.Database;
using WatchmenBot.Models;
using WatchmenBot.Services.Embeddings;

namespace WatchmenBot.Services;

/// <summary>
/// Core embedding service - handles search and RAG operations.
/// Storage, personal search, and context window operations are delegated to specialized services.
/// </summary>
public partial class EmbeddingService(
    EmbeddingClient embeddingClient,
    IDbConnectionFactory connectionFactory,
    ILogger<EmbeddingService> logger,
    EmbeddingStorageService storageService,
    PersonalSearchService personalSearchService,
    ContextWindowService contextWindowService)
{
    // Delegated services (injected for backward compatibility)

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

            var queryEmbedding = await embeddingClient.GetEmbeddingAsync(query, ct);
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
            var queryEmbedding = await embeddingClient.GetEmbeddingAsync(query, ct);
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
                r.IsNewsDump = DetectNewsDump(r.ChunkText);
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

    // Weight for hybrid search: 70% semantic, 30% keyword
    private const double DenseWeight = 0.7;
    private const double SparseWeight = 0.3;

    private async Task<List<SearchResult>> SearchByVectorAsync(
        long chatId,
        float[] queryEmbedding,
        int limit,
        CancellationToken ct,
        string? queryText = null)
    {
        using var connection = await connectionFactory.CreateConnectionAsync();

        var embeddingString = "[" + string.Join(",", queryEmbedding) + "]";

        // Extract search terms for tsvector (skip stop words)
        var searchTerms = !string.IsNullOrWhiteSpace(queryText)
            ? ExtractSearchTerms(queryText)
            : null;

        // Use hybrid scoring if we have query text, otherwise pure vector
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
                    -- Hybrid score: dense + sparse (tsvector)
                    {DenseWeight} * (1 - (embedding <=> @Embedding::vector))
                    + {SparseWeight} * COALESCE(
                        ts_rank_cd(
                            to_tsvector('russian', chunk_text),
                            websearch_to_tsquery('russian', @SearchTerms),
                            32  -- normalization: rank / (rank + 1)
                        ),
                        0
                    ) as Similarity
                FROM message_embeddings
                WHERE chat_id = @ChatId
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
                ORDER BY embedding <=> @Embedding::vector
                LIMIT @Limit
                """;

        var results = await connection.QueryAsync<SearchResult>(
            sql,
            new { ChatId = chatId, Embedding = embeddingString, SearchTerms = searchTerms, Limit = limit });

        logger.LogDebug("[Search] Hybrid={Hybrid}, Terms='{Terms}'", useHybrid, searchTerms ?? "none");

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
                    // Full-text results go first (they have exact word matches)
                    results = newResults.Concat(results).ToList();
                }
            }

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

            // Determine confidence level
            (response.Confidence, response.ConfidenceReason) = EvaluateConfidence(best, gap, response.HasFullTextMatch);

            // Always try ILIKE fallback to catch exact matches that PostgreSQL stemming might miss
            // This is especially important for slang/profanity where morphology can fail
            var ilikeResults = await SimpleTextSearchAsync(chatId, query, 10, ct);
            if (ilikeResults.Count > 0)
            {
                // Merge ILIKE results with vector results (avoiding duplicates)
                var existingIds = results.Select(r => r.MessageId).ToHashSet();
                var newResults = ilikeResults.Where(r => !existingIds.Contains(r.MessageId)).ToList();

                if (newResults.Count > 0)
                {
                    logger.LogInformation("[Search] ILIKE fallback added {Count} new results (total: {Total})",
                        newResults.Count, ilikeResults.Count);

                    results.AddRange(newResults);
                    results = results.OrderByDescending(r => r.Similarity).ToList();
                    response.Results = results;

                    // Update confidence if ILIKE brought better results
                    var newBest = results[0].Similarity;
                    if (newBest > best || !response.HasFullTextMatch)
                    {
                        response.HasFullTextMatch = true;
                        best = newBest;
                        response.BestScore = best;
                        (response.Confidence, response.ConfidenceReason) = EvaluateConfidence(best, gap, true);
                        response.ConfidenceReason += " (ILIKE boost)";
                    }
                }
            }

            logger.LogInformation(
                "[Search] Query: '{Query}' | Best: {Best:F3} | Gap: {Gap:F3} | FullText: {FT} | Confidence: {Conf}",
                TruncateForLog(query, 50), best, gap, response.HasFullTextMatch, response.Confidence);

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

            // Extract meaningful words for full-text search
            var searchTerms = ExtractSearchTerms(query);
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
            // Extract words longer than 3 chars for ILIKE search
            var rawWords = query
                .Split([' ', ',', '.', '!', '?', ':', ';', '-', '(', ')', '[', ']', '"', '\''], StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length > 3)
                .Select(w => w.ToLowerInvariant())
                .Distinct()
                .Take(5) // Limit to avoid huge queries
                .ToList();

            if (rawWords.Count == 0)
                return [];

            // Add stems (word roots) to catch different word forms
            // e.g., "сосунов" -> also search for "сосун"
            var words = new HashSet<string>(rawWords);
            foreach (var word in rawWords)
            {
                var stem = GetRussianStem(word);
                if (!string.IsNullOrEmpty(stem) && stem.Length >= 3 && stem != word)
                {
                    words.Add(stem);
                }
            }

            logger.LogDebug("[ILIKE] Words: [{Raw}] + stems: [{All}]",
                string.Join(", ", rawWords), string.Join(", ", words));

            using var connection = await connectionFactory.CreateConnectionAsync();

            // Build ILIKE conditions for each word
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

            // First try embeddings table
            var embeddingsConditions = wordList.Select((w, i) => $"LOWER(chunk_text) LIKE @Word{i}").ToList();
            var embeddingsWhere = string.Join(" OR ", embeddingsConditions);

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
                WHERE me.chat_id = @ChatId AND ({embeddingsWhere})
                ORDER BY m.date_utc DESC
                LIMIT @Limit
                """;

            var results = (await connection.QueryAsync<SearchResult>(embeddingsSql, parameters)).ToList();

            logger.LogDebug("[ILIKE] Embeddings: {Count} results for words: {Words}",
                results.Count, string.Join(", ", wordList));

            // Fallback: search raw messages table ONLY if embeddings found nothing
            // This catches messages not yet embedded (new or too short)
            // Limited to last 30 days to avoid slow full table scans
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

    /// <summary>
    /// Simple Russian stemmer - strips common word endings
    /// </summary>
    private static string GetRussianStem(string word)
    {
        if (string.IsNullOrEmpty(word) || word.Length < 4)
            return word;

        // Common Russian endings (ordered by length, longest first)
        var endings = new[]
        {
            // Noun endings (plural/genitive/etc)
            "ами", "ями", "ов", "ев", "ей", "ах", "ях", "ом", "ем", "ём",
            "ам", "ям", "ы", "и", "а", "я", "у", "ю", "е", "о",
            // Adjective endings
            "ый", "ий", "ой", "ая", "яя", "ое", "ее", "ые", "ие",
            "ого", "его", "ому", "ему", "ым", "им", "ой", "ей", "ую", "юю",
            // Verb endings
            "ать", "ять", "еть", "ить", "ут", "ют", "ет", "ит", "ешь", "ишь"
        };

        var lowerWord = word.ToLowerInvariant();

        foreach (var ending in endings)
        {
            if (lowerWord.Length > ending.Length + 2 && lowerWord.EndsWith(ending))
            {
                return lowerWord[..^ending.Length];
            }
        }

        return lowerWord;
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
        var urlCount = MyRegex().Matches(text).Count;
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
    /// </summary>
    public Task<SearchResponse> GetPersonalContextAsync(
        long chatId,
        string usernameOrName,
        string? displayName,
        string question,
        int days = 7,
        CancellationToken ct = default)
        => personalSearchService.GetPersonalContextAsync(chatId, usernameOrName, displayName, question, days, ct);

    /// <summary>
    /// Get context window around a message (delegates to ContextWindowService)
    /// </summary>
    public Task<List<ContextMessage>> GetContextWindowAsync(
        long chatId,
        long messageId,
        int windowSize = 2,
        CancellationToken ct = default)
        => contextWindowService.GetContextWindowAsync(chatId, messageId, windowSize, ct);

    /// <summary>
    /// Get merged context windows (delegates to ContextWindowService)
    /// </summary>
    public Task<List<ContextWindow>> GetMergedContextWindowsAsync(
        long chatId,
        List<long> messageIds,
        int windowSize = 2,
        CancellationToken ct = default)
        => contextWindowService.GetMergedContextWindowsAsync(chatId, messageIds, windowSize, ct);
    
    [System.Text.RegularExpressions.GeneratedRegex(@"https?://")]
    private static partial System.Text.RegularExpressions.Regex MyRegex();
}

// ============================================================================
// Models (shared across services)
// ============================================================================

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

    /// <summary>
    /// Флаг: ChunkText уже содержит полное контекстное окно (из context_embeddings)
    /// </summary>
    public bool IsContextWindow { get; set; }
}

public class SearchResponse
{
    public List<SearchResult> Results { get; set; } = [];

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
    None = 0,
    /// <summary>Слабые совпадения — предупредить пользователя</summary>
    Low = 1,
    /// <summary>Средние совпадения — можно использовать с оговоркой</summary>
    Medium = 2,
    /// <summary>Хорошие совпадения — уверенный ответ</summary>
    High = 3
}

public class EmbeddingStats
{
    public int TotalEmbeddings { get; set; }
    public DateTimeOffset? OldestEmbedding { get; set; }
    public DateTimeOffset? NewestEmbedding { get; set; }
}

// Context models (re-exported from ContextWindowService for backward compatibility)
public class ContextMessage
{
    public long MessageId { get; set; }
    public long ChatId { get; set; }
    public long FromUserId { get; set; }
    public string Author { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public DateTime DateUtc { get; set; }
}

public class ContextWindow
{
    /// <summary>
    /// The message ID that was originally found by search
    /// </summary>
    public long CenterMessageId { get; set; }

    /// <summary>
    /// All messages in the window (including center message)
    /// </summary>
    public List<ContextMessage> Messages { get; set; } = [];

    /// <summary>
    /// Format the window as readable text
    /// </summary>
    public string ToFormattedText()
    {
        var sb = new StringBuilder();
        foreach (var msg in Messages)
        {
            var isCenter = msg.MessageId == CenterMessageId;
            var marker = isCenter ? "→ " : "  ";
            var time = msg.DateUtc.ToString("HH:mm");
            sb.AppendLine($"{marker}[{time}] {msg.Author}: {msg.Text}");
        }
        return sb.ToString();
    }
}
