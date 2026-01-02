using WatchmenBot.Features.Search.Models;
using WatchmenBot.Features.Search.Services;

namespace WatchmenBot.Features.Search;

/// <summary>
/// Search strategy service for /ask command
/// Handles personal (hybrid) and context-only search strategies
/// </summary>
public class SearchStrategyService(
    EmbeddingService embeddingService,
    ContextEmbeddingService contextEmbeddingService,
    ILogger<SearchStrategyService> logger)
{
    /// <summary>
    /// Route search based on classified intent
    /// </summary>
    public async Task<SearchResponse> SearchWithIntentAsync(
        long chatId,
        ClassifiedQuery classified,
        string askerUsername,
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

            // Default: context-only search
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
    /// Search for comparison between multiple entities
    /// </summary>
    public async Task<SearchResponse> SearchComparisonAsync(
        long chatId,
        List<ExtractedEntity> entities,
        string query,
        CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Search for each entity in parallel
        var personEntities = entities
            .Where(e => e.Type == EntityType.Person)
            .Take(3)
            .ToList();

        var searchTasks = personEntities.Select(e =>
            embeddingService.SearchSimilarAsync(chatId, $"{query} {e.Text}", limit: 5, ct));

        var searchResults = await Task.WhenAll(searchTasks);
        var allResults = searchResults.SelectMany(r => r).ToList();

        // Deduplicate and sort
        var mergedResults = allResults
            .GroupBy(r => r.MessageId)
            .Select(g => g.OrderByDescending(r => r.Similarity).First())
            .OrderByDescending(r => r.Similarity)
            .ToList();

        sw.Stop();

        if (mergedResults.Count == 0)
        {
            return new SearchResponse
            {
                Confidence = SearchConfidence.None,
                ConfidenceReason = $"Не найдено сообщений про: {string.Join(", ", personEntities.Select(e => e.Text))}",
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
            string.Join(", ", personEntities.Select(e => e.Text)), mergedResults.Count, sw.ElapsedMilliseconds);

        return new SearchResponse
        {
            Results = mergedResults,
            Confidence = confidence,
            ConfidenceReason = $"[Comparison: {string.Join(" vs ", personEntities.Select(e => e.Text))}] " +
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
    /// 1. Try finding user's messages via message_embeddings (precise targeting)
    /// 2. Expand with context windows via context_embeddings (full dialog context)
    /// 3. If no personal messages found, fallback to context-only search (user might have participated in dialogs)
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

        // Step 1: Try finding user's relevant messages using message_embeddings
        var personalTask = embeddingService.GetPersonalContextAsync(
            chatId, usernameOrName, displayName, query, days, ct);

        // Step 2: Parallel search in context embeddings (user might be in dialogs)
        var contextTask = contextEmbeddingService.SearchContextAsync(chatId, query, limit: 10, ct);

        await Task.WhenAll(personalTask, contextTask);

        var personalResponse = await personalTask;
        var contextWindows = await contextTask;

        sw.Stop();

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
        var confidence = personalCount > 0 ? personalResponse.Confidence : (bestSim switch
        {
            > 0.5 => SearchConfidence.High,
            > 0.35 => SearchConfidence.Medium,
            > 0.25 => SearchConfidence.Low,
            _ => SearchConfidence.None
        });

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
    /// Hybrid search for general questions:
    /// 1. Search in context_embeddings for full dialog context (primary)
    /// 2. Search in message_embeddings for precise message matches (secondary)
    /// 3. Merge and deduplicate results
    /// </summary>
    public async Task<SearchResponse> SearchContextOnlyAsync(
        long chatId, string query, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Parallel search in both embedding types
        var contextTask = contextEmbeddingService.SearchContextAsync(chatId, query, limit: 10, ct);
        var messageTask = embeddingService.SearchSimilarAsync(chatId, query, limit: 10, ct);

        await Task.WhenAll(contextTask, messageTask);

        var contextResults = await contextTask;
        var messageResults = await messageTask;

        sw.Stop();
        logger.LogInformation(
            "[HybridSearch] Found {ContextCount} context windows + {MessageCount} messages in {Ms}ms",
            contextResults.Count, messageResults.Count, sw.ElapsedMilliseconds);

        // Convert context results (priority: 1.0x similarity)
        var contextSearchResults = contextResults.Select(cr => new SearchResult
        {
            ChatId = cr.ChatId,
            MessageId = cr.CenterMessageId,
            ChunkIndex = 0,
            ChunkText = cr.ContextText, // Full window with context
            MetadataJson = null,
            Similarity = cr.Similarity, // Keep original similarity (priority)
            Distance = cr.Distance,
            IsNewsDump = false,
            IsContextWindow = true // Already has full context, no need to expand
        }).ToList();

        // Convert message results (priority: 0.85x similarity - slightly lower than context)
        var messageSearchResults = messageResults.Select(mr => new SearchResult
        {
            ChatId = mr.ChatId,
            MessageId = mr.MessageId,
            ChunkIndex = mr.ChunkIndex,
            ChunkText = mr.ChunkText,
            MetadataJson = mr.MetadataJson,
            Similarity = mr.Similarity * 0.85, // Lower priority than full context
            Distance = mr.Distance,
            IsNewsDump = mr.IsNewsDump
        }).ToList();

        // Merge and deduplicate by message_id (keep best similarity)
        var allResults = contextSearchResults
            .Concat(messageSearchResults)
            .GroupBy(r => r.MessageId)
            .Select(g => g.OrderByDescending(r => r.Similarity).First())
            .OrderByDescending(r => r.Similarity)
            .ToList();

        logger.LogInformation(
            "[HybridSearch] Merged {Total} results ({Context} context + {Message} messages)",
            allResults.Count, contextResults.Count, messageResults.Count);

        if (allResults.Count == 0)
        {
            return new SearchResponse
            {
                Confidence = SearchConfidence.None,
                ConfidenceReason = "No embeddings found"
            };
        }

        // Determine confidence based on best similarity
        var bestSim = allResults[0].Similarity;
        var confidence = bestSim switch
        {
            > 0.5 => SearchConfidence.High,
            > 0.35 => SearchConfidence.Medium,
            > 0.25 => SearchConfidence.Low,
            _ => SearchConfidence.None
        };

        var confidenceReason = confidence switch
        {
            SearchConfidence.High => $"[Hybrid: {contextResults.Count}+{messageResults.Count}] Strong match (sim={bestSim:F3})",
            SearchConfidence.Medium => $"[Hybrid: {contextResults.Count}+{messageResults.Count}] Moderate match (sim={bestSim:F3})",
            SearchConfidence.Low => $"[Hybrid: {contextResults.Count}+{messageResults.Count}] Weak match (sim={bestSim:F3})",
            _ => $"[Hybrid: {contextResults.Count}+{messageResults.Count}] Very weak match (sim={bestSim:F3})"
        };

        return new SearchResponse
        {
            Results = allResults,
            Confidence = confidence,
            ConfidenceReason = confidenceReason,
            BestScore = bestSim
        };
    }
}
