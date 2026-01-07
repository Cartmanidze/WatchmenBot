using WatchmenBot.Features.Search.Models;
using WatchmenBot.Features.Search.Services;

namespace WatchmenBot.Features.Search;

/// <summary>
/// Search strategy service for /ask command
/// Handles personal (hybrid) and context-only search strategies
/// Enhanced with RAG Fusion for better recall on ambiguous queries
/// Now includes cross-encoder reranking via Cohere for improved question→answer matching
/// </summary>
public class SearchStrategyService(
    EmbeddingService embeddingService,
    ContextEmbeddingService contextEmbeddingService,
    RagFusionService ragFusionService,
    NicknameResolverService nicknameResolverService,
    CohereRerankService cohereReranker,
    ILogger<SearchStrategyService> logger)
{
    /// <summary>
    /// Route search based on classified intent
    /// </summary>
    public async Task<SearchResponse> SearchWithIntentAsync(
        long chatId,
        ClassifiedQuery classified,
        string? askerUsername,
        string askerName,
        CancellationToken ct)
    {
        logger.LogInformation("[SearchStrategy] Intent: {Intent}, Temporal: {Temporal}, People: {People}",
            classified.Intent, classified.HasTemporal, classified.MentionedPeople.Count);

        return classified.Intent switch
        {
            // Personal question about self
            QueryIntent.PersonalSelf => await SearchPersonalWithHybridAsync(
                chatId,
                askerUsername ?? askerName,
                askerName,
                classified.OriginalQuestion,
                GetDaysFromTemporal(classified),
                ct),

            // Personal question about someone else
            QueryIntent.PersonalOther when classified.MentionedPeople.Count > 0 => await SearchPersonalWithHybridAsync(
                chatId,
                classified.MentionedPeople[0],
                null,
                classified.OriginalQuestion,
                GetDaysFromTemporal(classified),
                ct),

            // Time-bound question
            QueryIntent.Temporal when classified.HasTemporal => await SearchWithTimeRangeAsync(
                chatId,
                classified.OriginalQuestion,
                classified.TemporalRef!,
                ct),

            // Comparison between entities
            QueryIntent.Comparison when classified.Entities.Count >= 2 => await SearchComparisonAsync(
                chatId,
                classified.Entities,
                classified.OriginalQuestion,
                ct),

            // Multi-entity question
            QueryIntent.MultiEntity when classified.MentionedPeople.Count >= 2 => await SearchMultiEntityAsync(
                chatId,
                classified.MentionedPeople,
                classified.OriginalQuestion,
                ct),

            // Default: context-only search (HyDE handles Q→A semantic gap)
            _ => await SearchContextOnlyAsync(chatId, classified.OriginalQuestion, ct)
        };
    }

    /// <summary>
    /// Search with time range filter based on temporal reference
    /// </summary>
    public async Task<SearchResponse> SearchWithTimeRangeAsync(
        long chatId,
        string query,
        TemporalReference temporal,
        CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var now = DateTimeOffset.UtcNow;

        // Calculate time range
        var (startUtc, endUtc) = CalculateTimeRange(now, temporal);

        logger.LogInformation("[TemporalSearch] Query: '{Query}', Range: {Start:yyyy-MM-dd HH:mm} to {End:yyyy-MM-dd HH:mm}",
            query.Length > 30 ? query[..30] + "..." : query, startUtc, endUtc);

        // Search in message embeddings within time range
        var results = await embeddingService.SearchSimilarInRangeAsync(
            chatId, query, startUtc, endUtc, limit: 15, ct);

        sw.Stop();

        if (results.Count == 0)
        {
            return new SearchResponse
            {
                Confidence = SearchConfidence.None,
                ConfidenceReason = $"Ничего не найдено за период '{temporal.Text ?? "указанный"}'",
                BestScore = 0
            };
        }

        var bestSim = results.Max(r => r.Similarity);
        var confidence = bestSim switch
        {
            > 0.5 => SearchConfidence.High,
            > 0.35 => SearchConfidence.Medium,
            > 0.25 => SearchConfidence.Low,
            _ => SearchConfidence.None
        };

        logger.LogInformation("[TemporalSearch] Found {Count} results in {Ms}ms, best={Best:F3}",
            results.Count, sw.ElapsedMilliseconds, bestSim);

        return new SearchResponse
        {
            Results = results,
            Confidence = confidence,
            ConfidenceReason = $"[Temporal: {temporal.Text}] Найдено {results.Count} результатов (sim={bestSim:F3})",
            BestScore = bestSim
        };
    }

    /// <summary>
    /// Search for comparison between multiple entities.
    /// Supports all entity types: Person, Topic, Location, Organization, etc.
    /// </summary>
    public async Task<SearchResponse> SearchComparisonAsync(
        long chatId,
        List<ExtractedEntity> entities,
        string query,
        CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Search for each entity in parallel (all types, not just Person)
        var searchEntities = entities
            .Take(3)
            .ToList();

        logger.LogInformation("[ComparisonSearch] Starting search for {Count} entities: [{Entities}]",
            searchEntities.Count, string.Join(", ", searchEntities.Select(e => $"{e.Type}:{e.Text}")));

        if (searchEntities.Count == 0)
        {
            return new SearchResponse
            {
                Confidence = SearchConfidence.None,
                ConfidenceReason = "Не найдено сущностей для сравнения",
                BestScore = 0
            };
        }

        var searchQueries = searchEntities.Select(e => $"{query} {e.Text}").ToList();
        logger.LogInformation("[ComparisonSearch] Queries: [{Queries}]", string.Join(" | ", searchQueries));

        // SEQUENTIAL execution to prevent DB connection contention
        var searchResults = new List<SearchResult>[searchQueries.Count];
        for (var i = 0; i < searchQueries.Count; i++)
        {
            searchResults[i] = await embeddingService.SearchSimilarAsync(chatId, searchQueries[i], limit: 5, ct);
        }

        logger.LogInformation("[ComparisonSearch] Search returned {Count} result sets: [{Counts}]",
            searchResults.Length, string.Join(", ", searchResults.Select(r => r.Count)));

        var allResults = searchResults.SelectMany(r => r).ToList();

        // Deduplicate and sort
        var mergedResults = allResults
            .GroupBy(r => r.MessageId)
            .Select(g => g.OrderByDescending(r => r.Similarity).First())
            .OrderByDescending(r => r.Similarity)
            .ToList();

        sw.Stop();

        var entityNames = searchEntities.Select(e => e.Text).ToList();

        if (mergedResults.Count == 0)
        {
            return new SearchResponse
            {
                Confidence = SearchConfidence.None,
                ConfidenceReason = $"Не найдено сообщений про: {string.Join(", ", entityNames)}",
                BestScore = 0
            };
        }

        var bestSim = mergedResults.Max(r => r.Similarity);
        var confidence = bestSim switch
        {
            > 0.5 => SearchConfidence.High,
            > 0.35 => SearchConfidence.Medium,
            > 0.25 => SearchConfidence.Low,
            _ => SearchConfidence.None
        };

        logger.LogInformation("[ComparisonSearch] Entities: [{Entities}], Found {Count} results in {Ms}ms",
            string.Join(", ", entityNames), mergedResults.Count, sw.ElapsedMilliseconds);

        return new SearchResponse
        {
            Results = mergedResults,
            Confidence = confidence,
            ConfidenceReason = $"[Comparison: {string.Join(" vs ", entityNames)}] " +
                             $"Найдено {mergedResults.Count} результатов (sim={bestSim:F3})",
            BestScore = bestSim
        };
    }

    /// <summary>
    /// Search for mentions of multiple people together
    /// </summary>
    public async Task<SearchResponse> SearchMultiEntityAsync(
        long chatId,
        List<string> people,
        string query,
        CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Create combined query with all people
        var combinedQuery = $"{query} {string.Join(" ", people.Take(3))}";

        var results = await embeddingService.SearchSimilarAsync(chatId, combinedQuery, limit: 15, ct);

        sw.Stop();

        if (results.Count == 0)
        {
            return new SearchResponse
            {
                Confidence = SearchConfidence.None,
                ConfidenceReason = $"Не найдено сообщений про: {string.Join(", ", people.Take(3))}",
                BestScore = 0
            };
        }

        var bestSim = results.Max(r => r.Similarity);
        var confidence = bestSim switch
        {
            > 0.5 => SearchConfidence.High,
            > 0.35 => SearchConfidence.Medium,
            > 0.25 => SearchConfidence.Low,
            _ => SearchConfidence.None
        };

        logger.LogInformation("[MultiEntitySearch] People: [{People}], Found {Count} results in {Ms}ms",
            string.Join(", ", people.Take(3)), results.Count, sw.ElapsedMilliseconds);

        return new SearchResponse
        {
            Results = results,
            Confidence = confidence,
            ConfidenceReason = $"[MultiEntity: {string.Join(", ", people.Take(3))}] " +
                             $"Найдено {results.Count} результатов (sim={bestSim:F3})",
            BestScore = bestSim
        };
    }

    private static int GetDaysFromTemporal(ClassifiedQuery classified)
    {
        if (classified.TemporalRef?.RelativeDays.HasValue ?? false)
            return Math.Abs(classified.TemporalRef.RelativeDays.Value) + 1;
        return 7; // Default: 7 days
    }

    private static (DateTimeOffset startUtc, DateTimeOffset endUtc) CalculateTimeRange(
        DateTimeOffset now, TemporalReference temporal)
    {
        // Handle absolute date
        if (temporal.AbsoluteDate.HasValue)
        {
            var date = temporal.AbsoluteDate.Value;
            return (date.Date, date.Date.AddDays(1));
        }

        // Handle relative days
        var days = temporal.RelativeDays ?? -1;

        return days switch
        {
            0 => (now.Date, now), // today: from midnight to now
            -1 => (now.AddDays(-1).Date, now.Date), // yesterday: full day
            -2 => (now.AddDays(-2).Date, now.AddDays(-1).Date), // day before yesterday
            <= -7 and > -14 => (now.AddDays(-7).Date, now.Date), // this week to last week
            <= -14 and > -30 => (now.AddDays(-14).Date, now.AddDays(-7).Date), // last two weeks
            <= -30 => (now.AddDays(-30).Date, now.AddDays(-14).Date), // last month
            _ => (now.AddDays(days).Date, now) // generic relative
        };
    }

    /// <summary>
    /// Hybrid search for personal questions:
    /// 1. Resolve nickname/name to stable user_id via user_aliases table
    /// 2. Try finding user's messages via message_embeddings (precise targeting by user_id)
    /// 3. Expand with context windows via context_embeddings (full dialog context)
    /// 4. If no personal messages found, fallback to context-only search (user might have participated in dialogs)
    /// </summary>
    public async Task<SearchResponse> SearchPersonalWithHybridAsync(
        long chatId,
        string usernameOrName,
        string? displayName,
        string query,
        int days,
        CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Step 0: Resolve nickname/name to user_id via user_aliases table
        // This allows finding messages even when user changed their display name
        var resolved = await nicknameResolverService.ResolveToUserIdAsync(chatId, usernameOrName, ct);
        var resolvedUserId = resolved.UserId;
        var resolvedName = resolved.ResolvedName ?? displayName ?? usernameOrName;

        logger.LogInformation("[HybridPersonal] Resolved '{Name}' → user_id={UserId} ({ResolvedName}), confidence={Conf:F2}",
            usernameOrName, resolvedUserId?.ToString() ?? "null", resolvedName, resolved.Confidence);

        // Step 1: Try finding user's relevant messages using message_embeddings
        // Pass user_id for precise filtering that works across name changes
        var personalTask = embeddingService.GetPersonalContextAsync(
            chatId, usernameOrName, resolvedName, query, days, resolvedUserId, ct);

        // Step 2: Parallel search in context embeddings (user might be in dialogs)
        // Fetch more candidates for reranking - cross-encoder will improve relevance
        var contextLimit = cohereReranker.IsConfigured ? 50 : 10;
        var contextTask = contextEmbeddingService.SearchContextAsync(chatId, query, limit: contextLimit, ct);

        await Task.WhenAll(personalTask, contextTask);

        var personalResponse = await personalTask;
        var contextWindows = await contextTask;

        // Step 3: Merge results based on what we found
        var allResults = new List<SearchResult>();
        var personalCount = 0;
        var contextCount = 0;

        // Add personal message results if found
        if (personalResponse.Results.Count > 0)
        {
            allResults.AddRange(personalResponse.Results);
            personalCount = personalResponse.Results.Count;

            // Expand with context windows containing these messages
            var topMessageIds = personalResponse.Results
                .Take(5)
                .Select(r => r.MessageId)
                .ToList();

            var expandedWindows = await contextEmbeddingService.GetContextWindowsByMessageIdsAsync(
                chatId, topMessageIds, limit: 5, ct);

            var expandedResults = expandedWindows.Select(cw => new SearchResult
            {
                ChatId = cw.ChatId,
                MessageId = cw.CenterMessageId,
                ChunkIndex = 0,
                ChunkText = cw.ContextText,
                MetadataJson = null,
                Similarity = 0.75, // Lower than direct hits but higher than generic context
                Distance = 0.25,
                IsNewsDump = false,
                IsContextWindow = true // Already has full context from context_embeddings
            });

            allResults.AddRange(expandedResults);
            contextCount += expandedWindows.Count;
        }

        // Add context-only results (might include user's participation in dialogs)
        var contextResults = contextWindows.Select(cw => new SearchResult
        {
            ChatId = cw.ChatId,
            MessageId = cw.CenterMessageId,
            ChunkIndex = 0,
            ChunkText = cw.ContextText,
            MetadataJson = null,
            Similarity = cw.Similarity * 0.9, // Slightly lower priority than personal
            Distance = cw.Distance,
            IsNewsDump = false,
            IsContextWindow = true // Already has full context from context_embeddings
        });

        allResults.AddRange(contextResults);
        contextCount += contextWindows.Count;

        // Deduplicate by message_id, keeping best similarity
        var mergedResults = allResults
            .GroupBy(r => r.MessageId)
            .Select(g => g.OrderByDescending(r => r.Similarity).First())
            .OrderByDescending(r => r.Similarity)
            .ToList();

        // Step 4: Cross-encoder reranking (if configured)
        // This dramatically improves question→answer matching that bi-encoders miss
        if (cohereReranker.IsConfigured && mergedResults.Count > 0)
        {
            var beforeRerank = sw.ElapsedMilliseconds;
            mergedResults = await cohereReranker.RerankAsync(query, mergedResults, topN: 15, ct);
            logger.LogInformation(
                "[HybridPersonal] Reranked {Count} results in {Ms}ms",
                mergedResults.Count, sw.ElapsedMilliseconds - beforeRerank);
        }

        sw.Stop();

        logger.LogInformation(
            "[HybridPersonal] User: {User} | Found {Total} results in {Ms}ms ({Personal} personal + {Context} context)",
            usernameOrName, mergedResults.Count, sw.ElapsedMilliseconds, personalCount, contextCount);

        // Determine confidence based on combined results
        if (mergedResults.Count == 0)
        {
            return new SearchResponse
            {
                Confidence = SearchConfidence.None,
                ConfidenceReason = $"Пользователь {usernameOrName} не найден в истории"
            };
        }

        var bestSim = mergedResults[0].Similarity;
        // Always calculate confidence from post-rerank similarity
        // (Cohere cross-encoder may significantly change scores)
        var confidence = bestSim switch
        {
            > 0.5 => SearchConfidence.High,
            > 0.35 => SearchConfidence.Medium,
            > 0.25 => SearchConfidence.Low,
            _ => SearchConfidence.None
        };

        return new SearchResponse
        {
            Results = mergedResults,
            Confidence = confidence,
            ConfidenceReason = personalCount > 0
                ? $"[Hybrid: {personalCount} personal + {contextCount} context] {personalResponse.ConfidenceReason}"
                : $"[Context-only: {contextCount} windows] Найдено в диалогах (sim={bestSim:F3})",
            BestScore = bestSim,
            ScoreGap = personalResponse.ScoreGap,
            HasFullTextMatch = personalResponse.HasFullTextMatch
        };
    }

    /// <summary>
    /// RAG Fusion search for general questions:
    /// 1. Generate query variations using LLM ("кто гондон?" → "я гондон", "гондон в чате", etc.)
    /// 2. Search with all variations in parallel
    /// 3. Merge results using Reciprocal Rank Fusion (RRF)
    /// 4. Also search context_embeddings for dialog context
    /// </summary>
    public async Task<SearchResponse> SearchContextOnlyAsync(
        long chatId, string query, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Step 1: RAG Fusion search (generates variations + HyDE + RRF merge)
        // HyDE handles bot-directed questions semantically without explicit flag
        var fusionResponse = await ragFusionService.SearchWithFusionAsync(
            chatId, query,
            participantNames: null, // Could pass chat participants for better name variation
            variationCount: 3,
            resultsPerQuery: 15,
            ct);

        // Step 2: Context embeddings search (for dialog context)
        // Fetch more candidates for reranking if cross-encoder is configured
        var contextLimit = cohereReranker.IsConfigured ? 50 : 10;
        var contextResults = await contextEmbeddingService.SearchContextAsync(chatId, query, limit: contextLimit, ct);

        logger.LogInformation(
            "[RAG+Context] Query: '{Query}' | Variations: [{Vars}] | Fusion: {FusionCount} | Context: {ContextCount} | {Ms}ms",
            query.Length > 30 ? query[..30] + "..." : query,
            string.Join(", ", fusionResponse.QueryVariations.Take(3).Select(v => v.Length > 20 ? v[..20] + "..." : v)),
            fusionResponse.Results.Count,
            contextResults.Count,
            sw.ElapsedMilliseconds);

        // Convert context results (priority: 1.0x similarity)
        var contextSearchResults = contextResults.Select(cr => new SearchResult
        {
            ChatId = cr.ChatId,
            MessageId = cr.CenterMessageId,
            ChunkIndex = 0,
            ChunkText = cr.ContextText,
            MetadataJson = null,
            Similarity = cr.Similarity,
            Distance = cr.Distance,
            IsNewsDump = false,
            IsContextWindow = true
        }).ToList();

        // Convert fusion results (keep original similarity, they're already ranked by RRF)
        var fusionSearchResults = fusionResponse.Results.Select(fr => new SearchResult
        {
            ChatId = fr.ChatId,
            MessageId = fr.MessageId,
            ChunkIndex = fr.ChunkIndex,
            ChunkText = fr.ChunkText,
            MetadataJson = fr.MetadataJson,
            Similarity = fr.Similarity,
            Distance = fr.Distance,
            IsNewsDump = fr.IsNewsDump
        }).ToList();

        // Merge: context windows + fusion results, deduplicate by message_id
        var allResults = contextSearchResults
            .Concat(fusionSearchResults)
            .GroupBy(r => r.MessageId)
            .Select(g => g.OrderByDescending(r => r.Similarity).First())
            .OrderByDescending(r => r.Similarity)
            .ToList();

        // Step 3: Cross-encoder reranking (if configured)
        // This dramatically improves question→answer matching that bi-encoders miss
        if (cohereReranker.IsConfigured && allResults.Count > 0)
        {
            var beforeRerank = sw.ElapsedMilliseconds;
            allResults = await cohereReranker.RerankAsync(query, allResults, topN: 15, ct);
            logger.LogInformation(
                "[RAG+Context] Reranked {Count} results in {Ms}ms",
                allResults.Count, sw.ElapsedMilliseconds - beforeRerank);
        }

        sw.Stop();

        logger.LogInformation(
            "[RAG+Context] Merged {Total} results ({Fusion} fusion + {Context} context) in {Ms}ms",
            allResults.Count, fusionResponse.Results.Count, contextResults.Count, sw.ElapsedMilliseconds);

        if (allResults.Count == 0)
        {
            return new SearchResponse
            {
                Confidence = SearchConfidence.None,
                ConfidenceReason = "No embeddings found (RAG Fusion + context search)"
            };
        }

        // Use fusion confidence if available, otherwise calculate from similarity
        var bestSim = allResults[0].Similarity;
        var confidence = fusionResponse.Confidence != SearchConfidence.None
            ? fusionResponse.Confidence
            : bestSim switch
            {
                > 0.5 => SearchConfidence.High,
                > 0.35 => SearchConfidence.Medium,
                > 0.25 => SearchConfidence.Low,
                _ => SearchConfidence.None
            };

        var varSummary = fusionResponse.QueryVariations.Count > 0
            ? $"vars=[{string.Join(", ", fusionResponse.QueryVariations.Take(2))}]"
            : "no vars";

        return new SearchResponse
        {
            Results = allResults,
            Confidence = confidence,
            ConfidenceReason = $"[RAG Fusion: {fusionResponse.Results.Count} + Context: {contextResults.Count}] " +
                             $"(sim={bestSim:F3}, {varSummary})",
            BestScore = bestSim,
            ScoreGap = fusionResponse.ScoreGap
        };
    }
}
