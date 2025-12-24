using System.Text.Json;
using WatchmenBot.Services.Llm;

namespace WatchmenBot.Services;

/// <summary>
/// Reranking service: uses LLM to re-score search results for better relevance
/// </summary>
public class RerankService
{
    private readonly LlmRouter _llmRouter;
    private readonly ILogger<RerankService> _logger;

    // Only rerank top N results to save tokens
    private const int MaxResultsToRerank = 10;

    public RerankService(LlmRouter llmRouter, ILogger<RerankService> logger)
    {
        _llmRouter = llmRouter;
        _logger = logger;
    }

    /// <summary>
    /// Rerank search results using LLM
    /// </summary>
    public async Task<RerankResponse> RerankAsync(
        string query,
        List<SearchResult> results,
        CancellationToken ct = default)
    {
        var response = new RerankResponse
        {
            OriginalOrder = results.Select(r => r.MessageId).ToList()
        };

        if (results.Count == 0)
            return response;

        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            // Extract keywords from query (words > 3 chars, excluding common words)
            var keywords = ExtractKeywords(query);

            // Find results containing query keywords (these are high-value matches)
            var keywordMatches = results
                .Where(r => keywords.Any(kw =>
                    r.ChunkText.Contains(kw, StringComparison.OrdinalIgnoreCase)))
                .Take(5) // Max 5 keyword matches
                .ToList();

            // Take top N, but ensure keyword matches are included
            var topByScore = results.Take(MaxResultsToRerank).ToList();
            var toRerank = topByScore
                .Union(keywordMatches) // Add keyword matches if not already in top N
                .Take(MaxResultsToRerank)
                .ToList();

            if (keywordMatches.Count > 0)
            {
                _logger.LogInformation("[Rerank] Boosted {Count} keyword matches: {Keywords}",
                    keywordMatches.Count, string.Join(", ", keywords.Take(3)));
            }

            // Build documents list for LLM
            var documents = toRerank.Select((r, i) => new
            {
                id = i,
                text = TruncateText(r.ChunkText, 300) // Limit text length
            }).ToList();

            var systemPrompt = """
                Ты — эксперт по оценке релевантности текстов из неформального чата.

                Тебе дан ВОПРОС и список ДОКУМЕНТОВ. Оцени релевантность каждого документа для ответа на вопрос.

                ВАЖНО:
                - Это неформальный чат, в нём много слэнга, мата, шуток и оскорблений
                - Если в вопросе есть слово/термин и документ содержит это же слово — это ВЫСОКАЯ релевантность
                - Слова типа "сосун", "пидор", "хуй" и т.п. — это обычная лексика чата, оценивай по смыслу
                - НЕ обнуляй релевантность из-за "неприличного" содержания

                Правила оценки:
                - 3 = документ содержит ключевые слова из вопроса ИЛИ напрямую отвечает на вопрос
                - 2 = документ связан с темой вопроса
                - 1 = документ косвенно связан
                - 0 = документ вообще не связан с вопросом

                Отвечай ТОЛЬКО JSON массивом с id и score, без пояснений:
                [{"id": 0, "score": 3}, {"id": 1, "score": 1}, ...]
                """;

            var userPrompt = $"""
                ВОПРОС: {query}

                ДОКУМЕНТЫ:
                {JsonSerializer.Serialize(documents, new JsonSerializerOptions { WriteIndented = true })}

                Оцени релевантность каждого документа (0-3):
                """;

            var llmResponse = await _llmRouter.CompleteWithFallbackAsync(
                new LlmRequest
                {
                    SystemPrompt = systemPrompt,
                    UserPrompt = userPrompt,
                    Temperature = 0.1 // Low for consistency
                },
                preferredTag: null,
                ct: ct);

            // Parse scores
            var scores = ParseScores(llmResponse.Content, toRerank.Count);
            response.Scores = scores;

            // Apply scores and rerank
            for (var i = 0; i < toRerank.Count && i < scores.Count; i++)
            {
                toRerank[i].Similarity = CombineScores(toRerank[i].Similarity, scores[i]);
            }

            // Sort by new scores
            var reranked = toRerank
                .OrderByDescending(r => r.Similarity)
                .Concat(results.Skip(MaxResultsToRerank)) // Add remaining results
                .ToList();

            response.Results = reranked;
            response.RerankedOrder = reranked.Select(r => r.MessageId).ToList();

            sw.Stop();
            response.TimeMs = sw.ElapsedMilliseconds;
            response.TokensUsed = llmResponse.TotalTokens;

            _logger.LogInformation(
                "[Rerank] Query: '{Query}' | Reranked {Count} results | Time: {Ms}ms | Tokens: {Tokens}",
                TruncateText(query, 30), toRerank.Count, response.TimeMs, response.TokensUsed);

            // Log score changes
            for (var i = 0; i < Math.Min(5, scores.Count); i++)
            {
                var origPos = response.OriginalOrder.IndexOf(response.RerankedOrder[i]);
                _logger.LogDebug("[Rerank] #{NewPos} ← #{OldPos} (score={Score})",
                    i + 1, origPos + 1, scores.Count > i ? scores[response.OriginalOrder.IndexOf(response.RerankedOrder[i])] : -1);
            }

            return response;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogWarning(ex, "[Rerank] Failed, returning original order");

            response.Results = results;
            response.RerankedOrder = results.Select(r => r.MessageId).ToList();
            response.TimeMs = sw.ElapsedMilliseconds;
            response.Error = ex.Message;

            return response;
        }
    }

    /// <summary>
    /// Parse LLM response to get scores
    /// </summary>
    private List<int> ParseScores(string content, int expectedCount)
    {
        var scores = new List<int>();

        try
        {
            // Handle markdown code blocks
            var json = content.Trim();
            if (json.StartsWith("```"))
            {
                var lines = json.Split('\n');
                json = string.Join("\n", lines.Skip(1).TakeWhile(l => !l.StartsWith("```")));
            }

            using var doc = JsonDocument.Parse(json);
            var array = doc.RootElement.EnumerateArray().ToList();

            // Sort by id to ensure correct order
            var sorted = array
                .OrderBy(e => e.GetProperty("id").GetInt32())
                .ToList();

            foreach (var item in sorted)
            {
                var score = item.GetProperty("score").GetInt32();
                scores.Add(Math.Clamp(score, 0, 3)); // Clamp to valid range
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Rerank] Failed to parse scores, using defaults");
            // Return default scores (2 = medium relevance)
            scores = Enumerable.Repeat(2, expectedCount).ToList();
        }

        // Pad with defaults if needed
        while (scores.Count < expectedCount)
            scores.Add(1);

        return scores;
    }

    /// <summary>
    /// Combine original similarity with rerank score
    /// Formula: 0.5 * original + 0.5 * (rerank_score / 3)
    /// </summary>
    private static double CombineScores(double originalSimilarity, int rerankScore)
    {
        var normalizedRerank = rerankScore / 3.0; // 0-1 range
        return 0.5 * originalSimilarity + 0.5 * normalizedRerank;
    }

    private static string TruncateText(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            return text;
        return text[..(maxLength - 3)] + "...";
    }

    /// <summary>
    /// Extract meaningful keywords from query for keyword matching
    /// </summary>
    private static List<string> ExtractKeywords(string query)
    {
        var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "кто", "что", "где", "когда", "как", "почему", "зачем", "какой", "какая", "какое", "какие",
            "это", "эта", "этот", "эти", "тот", "та", "то", "те", "чем", "про", "об", "обо",
            "составь", "найди", "покажи", "расскажи", "напиши", "сделай",
            "рейтинг", "список", "топ", "лучший", "лучшие", "самый", "самые",
            "чата", "чате", "этого", "этом", "нашего", "нашем"
        };

        var words = query
            .ToLowerInvariant()
            .Split(new[] { ' ', ',', '.', '!', '?', ':', ';', '-', '(', ')', '[', ']', '"', '\'' },
                StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 3 && !stopWords.Contains(w))
            .Distinct()
            .ToList();

        // Also add stemmed versions (simple Russian stemming)
        var withStems = new List<string>(words);
        foreach (var word in words)
        {
            var stem = GetRussianStem(word);
            if (!string.IsNullOrEmpty(stem) && stem.Length >= 3 && stem != word)
            {
                withStems.Add(stem);
            }
        }

        return withStems.Distinct().Take(10).ToList();
    }

    /// <summary>
    /// Simple Russian stemmer - strips common word endings
    /// </summary>
    private static string GetRussianStem(string word)
    {
        if (string.IsNullOrEmpty(word) || word.Length < 4)
            return word;

        var endings = new[]
        {
            "ами", "ями", "ов", "ев", "ей", "ах", "ях", "ом", "ем", "ём",
            "ам", "ям", "ы", "и", "а", "я", "у", "ю", "е", "о"
        };

        foreach (var ending in endings)
        {
            if (word.Length > ending.Length + 2 && word.EndsWith(ending))
            {
                return word[..^ending.Length];
            }
        }

        return word;
    }
}

/// <summary>
/// Response from reranking
/// </summary>
public class RerankResponse
{
    public List<SearchResult> Results { get; set; } = new();

    /// <summary>
    /// Original order of message IDs before reranking
    /// </summary>
    public List<long> OriginalOrder { get; set; } = new();

    /// <summary>
    /// New order of message IDs after reranking
    /// </summary>
    public List<long> RerankedOrder { get; set; } = new();

    /// <summary>
    /// Scores assigned by LLM (0-3)
    /// </summary>
    public List<int> Scores { get; set; } = new();

    /// <summary>
    /// Time taken for reranking
    /// </summary>
    public long TimeMs { get; set; }

    /// <summary>
    /// Tokens used for reranking
    /// </summary>
    public int TokensUsed { get; set; }

    /// <summary>
    /// Error message if reranking failed
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// Check if order changed significantly
    /// </summary>
    public bool HasSignificantChange()
    {
        if (OriginalOrder.Count == 0 || RerankedOrder.Count == 0)
            return false;

        // Check if top-3 changed
        var origTop3 = OriginalOrder.Take(3).ToHashSet();
        var newTop3 = RerankedOrder.Take(3).ToHashSet();

        return !origTop3.SetEquals(newTop3);
    }

    /// <summary>
    /// Get results filtered by minimum rerank score
    /// Removes results with score below threshold (0-1 = irrelevant)
    /// </summary>
    public List<SearchResult> GetFilteredResults(int minScore = 2)
    {
        if (Scores.Count == 0 || Results.Count == 0)
            return Results;

        var filtered = new List<SearchResult>();
        for (var i = 0; i < Results.Count && i < Scores.Count; i++)
        {
            if (Scores[i] >= minScore)
                filtered.Add(Results[i]);
        }

        // Always keep at least top result if nothing passes filter
        if (filtered.Count == 0 && Results.Count > 0)
            filtered.Add(Results[0]);

        return filtered;
    }

    /// <summary>
    /// Count of results filtered out due to low score
    /// </summary>
    public int FilteredOutCount(int minScore = 2)
    {
        return Scores.Count(s => s < minScore);
    }
}
