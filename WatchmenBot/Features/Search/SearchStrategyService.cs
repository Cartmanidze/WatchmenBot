using WatchmenBot.Services;

namespace WatchmenBot.Features.Search;

/// <summary>
/// Search strategy service for /ask command
/// Handles personal (hybrid) and context-only search strategies
/// </summary>
public class SearchStrategyService
{
    private readonly EmbeddingService _embeddingService;
    private readonly ContextEmbeddingService _contextEmbeddingService;
    private readonly ILogger<SearchStrategyService> _logger;

    public SearchStrategyService(
        EmbeddingService embeddingService,
        ContextEmbeddingService contextEmbeddingService,
        ILogger<SearchStrategyService> logger)
    {
        _embeddingService = embeddingService;
        _contextEmbeddingService = contextEmbeddingService;
        _logger = logger;
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
        var personalTask = _embeddingService.GetPersonalContextAsync(
            chatId, usernameOrName, displayName, query, days, ct);

        // Step 2: Parallel search in context embeddings (user might be in dialogs)
        var contextTask = _contextEmbeddingService.SearchContextAsync(chatId, query, limit: 10, ct);

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

            var expandedWindows = await _contextEmbeddingService.GetContextWindowsByMessageIdsAsync(
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

        _logger.LogInformation(
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
        var contextTask = _contextEmbeddingService.SearchContextAsync(chatId, query, limit: 10, ct);
        var messageTask = _embeddingService.SearchSimilarAsync(chatId, query, limit: 10, ct);

        await Task.WhenAll(contextTask, messageTask);

        var contextResults = await contextTask;
        var messageResults = await messageTask;

        sw.Stop();
        _logger.LogInformation(
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

        _logger.LogInformation(
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
