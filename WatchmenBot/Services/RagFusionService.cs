using System.Text.Json;
using WatchmenBot.Services.Llm;

namespace WatchmenBot.Services;

/// <summary>
/// RAG Fusion service: generates query variations and merges results using RRF
/// Based on: https://habr.com/ru/companies/postgrespro/articles/979820/
/// </summary>
public class RagFusionService
{
    private readonly LlmRouter _llmRouter;
    private readonly EmbeddingService _embeddingService;
    private readonly ILogger<RagFusionService> _logger;

    // RRF constant (standard value from literature)
    private const int RrfK = 60;

    public RagFusionService(
        LlmRouter llmRouter,
        EmbeddingService embeddingService,
        ILogger<RagFusionService> logger)
    {
        _llmRouter = llmRouter;
        _embeddingService = embeddingService;
        _logger = logger;
    }

    /// <summary>
    /// Search with RAG Fusion: generate query variations, search each, merge with RRF
    /// </summary>
    public async Task<RagFusionResponse> SearchWithFusionAsync(
        long chatId,
        string query,
        int variationCount = 3,
        int resultsPerQuery = 15,
        CancellationToken ct = default)
    {
        var response = new RagFusionResponse { OriginalQuery = query };
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            // Step 1: Generate query variations
            var variations = await GenerateQueryVariationsAsync(query, variationCount, ct);
            response.QueryVariations = variations;

            _logger.LogInformation("[RAG Fusion] Generated {Count} variations for: {Query}",
                variations.Count, TruncateForLog(query, 50));

            // Step 2: Search with original + all variations in parallel
            var allQueries = new List<string> { query };
            allQueries.AddRange(variations);

            var searchTasks = allQueries.Select(q =>
                _embeddingService.SearchSimilarAsync(chatId, q, resultsPerQuery, ct));

            var allResults = await Task.WhenAll(searchTasks);

            // Step 3: Apply Reciprocal Rank Fusion
            var fusedResults = ApplyRrfFusion(allResults, allQueries);
            response.Results = fusedResults;

            // Step 4: Calculate confidence based on fused scores
            if (fusedResults.Count > 0)
            {
                var bestScore = fusedResults[0].FusedScore;
                var fifthScore = fusedResults.Count >= 5
                    ? fusedResults[4].FusedScore
                    : fusedResults.Last().FusedScore;
                var gap = bestScore - fifthScore;

                response.BestScore = bestScore;
                response.ScoreGap = gap;

                // RRF scores are typically 0.01-0.05 range, so adjust thresholds
                (response.Confidence, response.ConfidenceReason) = EvaluateFusionConfidence(
                    bestScore, gap, fusedResults.Count, allQueries.Count);
            }
            else
            {
                response.Confidence = SearchConfidence.None;
                response.ConfidenceReason = "No results from any query variation";
            }

            sw.Stop();
            response.TotalTimeMs = sw.ElapsedMilliseconds;

            _logger.LogInformation(
                "[RAG Fusion] Query: '{Query}' | Variations: {Vars} | Results: {Count} | Best RRF: {Best:F4} | Confidence: {Conf} | Time: {Ms}ms",
                TruncateForLog(query, 30), variations.Count, fusedResults.Count,
                response.BestScore, response.Confidence, response.TotalTimeMs);

            return response;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "[RAG Fusion] Failed for query: {Query}", query);

            response.Confidence = SearchConfidence.None;
            response.ConfidenceReason = "Fusion search failed";
            response.TotalTimeMs = sw.ElapsedMilliseconds;
            return response;
        }
    }

    /// <summary>
    /// Generate query variations using LLM
    /// </summary>
    private async Task<List<string>> GenerateQueryVariationsAsync(
        string query, int count, CancellationToken ct)
    {
        try
        {
            var systemPrompt = $"""
                Ты — помощник для улучшения поисковых запросов в чате.

                Сгенерируй {count} альтернативных формулировок запроса для поиска в истории сообщений.

                Правила:
                1. Каждая вариация должна искать ту же информацию, но другими словами
                2. Используй синонимы, перефразирования, альтернативные написания
                3. Если есть имена — добавь варианты (Вася/Василий, @username)
                4. Если есть аббревиатуры — расшифруй их
                5. Добавь контекст если очевиден (NBA, футбол, политика)
                6. Используй и русский, и английский если уместно

                Отвечай ТОЛЬКО JSON массивом строк, без пояснений:
                ["вариация 1", "вариация 2", "вариация 3"]
                """;

            var response = await _llmRouter.CompleteWithFallbackAsync(
                new LlmRequest
                {
                    SystemPrompt = systemPrompt,
                    UserPrompt = query,
                    Temperature = 0.7 // Higher for diversity
                },
                preferredTag: null,
                ct: ct);

            // Parse JSON response
            var content = response.Content.Trim();

            // Handle markdown code blocks
            if (content.StartsWith("```"))
            {
                var lines = content.Split('\n');
                content = string.Join("\n", lines.Skip(1).TakeWhile(l => !l.StartsWith("```")));
            }

            var variations = JsonSerializer.Deserialize<List<string>>(content);

            if (variations == null || variations.Count == 0)
            {
                _logger.LogWarning("[RAG Fusion] LLM returned empty variations, using fallback");
                return GenerateFallbackVariations(query);
            }

            // Limit to requested count and filter empty
            return variations
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Take(count)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[RAG Fusion] Failed to generate variations, using fallback");
            return GenerateFallbackVariations(query);
        }
    }

    /// <summary>
    /// Fallback variations when LLM fails
    /// </summary>
    private static List<string> GenerateFallbackVariations(string query)
    {
        var variations = new List<string>();

        // Simple keyword extraction
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 3)
            .ToList();

        if (words.Count >= 2)
        {
            // Variation 1: just keywords
            variations.Add(string.Join(" ", words.Take(3)));

            // Variation 2: reverse order
            variations.Add(string.Join(" ", words.Take(3).Reverse()));
        }

        return variations;
    }

    /// <summary>
    /// Apply Reciprocal Rank Fusion to merge results from multiple queries
    /// Formula: score(d) = Σ 1/(k + rank(d, q))
    /// </summary>
    private List<FusedSearchResult> ApplyRrfFusion(
        List<SearchResult>[] allResults,
        List<string> queries)
    {
        // Dictionary: MessageId -> (FusedScore, BestResult, ContributingQueries)
        var fusionScores = new Dictionary<long, (double Score, SearchResult Result, List<int> QueryIndices)>();

        for (var queryIndex = 0; queryIndex < allResults.Length; queryIndex++)
        {
            var results = allResults[queryIndex];

            for (var rank = 0; rank < results.Count; rank++)
            {
                var result = results[rank];
                var rrfScore = 1.0 / (RrfK + rank + 1); // rank is 0-based, so +1

                if (fusionScores.TryGetValue(result.MessageId, out var existing))
                {
                    // Add to existing score
                    existing.QueryIndices.Add(queryIndex);
                    fusionScores[result.MessageId] = (
                        existing.Score + rrfScore,
                        // Keep result with higher similarity
                        result.Similarity > existing.Result.Similarity ? result : existing.Result,
                        existing.QueryIndices
                    );
                }
                else
                {
                    fusionScores[result.MessageId] = (
                        rrfScore,
                        result,
                        new List<int> { queryIndex }
                    );
                }
            }
        }

        // Convert to list and sort by fused score
        var fusedResults = fusionScores
            .Select(kv => new FusedSearchResult
            {
                MessageId = kv.Key,
                ChatId = kv.Value.Result.ChatId,
                ChunkIndex = kv.Value.Result.ChunkIndex,
                ChunkText = kv.Value.Result.ChunkText,
                MetadataJson = kv.Value.Result.MetadataJson,
                Similarity = kv.Value.Result.Similarity,
                Distance = kv.Value.Result.Distance,
                IsNewsDump = kv.Value.Result.IsNewsDump,
                FusedScore = kv.Value.Score,
                MatchedQueryCount = kv.Value.QueryIndices.Count,
                MatchedQueryIndices = kv.Value.QueryIndices
            })
            .OrderByDescending(r => r.FusedScore)
            .ToList();

        _logger.LogDebug("[RRF] Fused {InputCount} result sets into {OutputCount} unique results",
            allResults.Length, fusedResults.Count);

        // Log top results with their query contributions
        foreach (var result in fusedResults.Take(5))
        {
            var matchedQueries = result.MatchedQueryIndices
                .Select(i => i == 0 ? "original" : $"var{i}")
                .ToList();

            _logger.LogDebug("[RRF] MsgId={Id} | RRF={Score:F4} | Sim={Sim:F3} | Queries=[{Queries}]",
                result.MessageId, result.FusedScore, result.Similarity, string.Join(",", matchedQueries));
        }

        return fusedResults;
    }

    /// <summary>
    /// Evaluate confidence based on RRF scores
    /// RRF scores are typically in 0.01-0.05 range (much lower than similarity scores)
    /// </summary>
    private static (SearchConfidence confidence, string reason) EvaluateFusionConfidence(
        double bestScore, double gap, int resultCount, int queryCount)
    {
        // Maximum possible RRF score for a result in position 1 across all queries:
        // queryCount * 1/(60+1) ≈ queryCount * 0.0164
        var maxPossible = queryCount * (1.0 / (RrfK + 1));

        // Normalize to 0-1 range
        var normalizedBest = bestScore / maxPossible;

        // If result appears in multiple queries, that's a strong signal
        // RRF score > 0.03 with 4 queries means it appeared in ~2+ queries
        var appearsInMultiple = bestScore > (2.0 / (RrfK + 5)); // ~0.031

        if (normalizedBest >= 0.7 || appearsInMultiple)
            return (SearchConfidence.High, $"RRF={bestScore:F4} (norm={normalizedBest:F2}), multi-query match");

        if (normalizedBest >= 0.4)
            return (SearchConfidence.Medium, $"RRF={bestScore:F4} (norm={normalizedBest:F2})");

        if (normalizedBest >= 0.2 || resultCount >= 5)
            return (SearchConfidence.Low, $"RRF={bestScore:F4} (norm={normalizedBest:F2}), weak match");

        return (SearchConfidence.None, $"RRF={bestScore:F4} too low");
    }

    private static string TruncateForLog(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            return text;
        return text[..(maxLength - 3)] + "...";
    }
}

/// <summary>
/// Search result with RRF fusion score
/// </summary>
public class FusedSearchResult : SearchResult
{
    /// <summary>
    /// Reciprocal Rank Fusion score (sum of 1/(k+rank) across all queries)
    /// </summary>
    public double FusedScore { get; set; }

    /// <summary>
    /// How many query variations found this result
    /// </summary>
    public int MatchedQueryCount { get; set; }

    /// <summary>
    /// Which query indices matched (0 = original, 1+ = variations)
    /// </summary>
    public List<int> MatchedQueryIndices { get; set; } = new();
}

/// <summary>
/// Response from RAG Fusion search
/// </summary>
public class RagFusionResponse
{
    public string OriginalQuery { get; set; } = string.Empty;
    public List<string> QueryVariations { get; set; } = new();
    public List<FusedSearchResult> Results { get; set; } = new();

    public SearchConfidence Confidence { get; set; }
    public string? ConfidenceReason { get; set; }

    public double BestScore { get; set; }
    public double ScoreGap { get; set; }

    public long TotalTimeMs { get; set; }

    /// <summary>
    /// Convert to standard SearchResponse for compatibility
    /// </summary>
    public SearchResponse ToSearchResponse()
    {
        return new SearchResponse
        {
            Results = Results.Cast<SearchResult>().ToList(),
            Confidence = Confidence,
            ConfidenceReason = ConfidenceReason,
            BestScore = BestScore,
            ScoreGap = ScoreGap,
            HasFullTextMatch = false // RAG Fusion doesn't use full-text directly
        };
    }
}
