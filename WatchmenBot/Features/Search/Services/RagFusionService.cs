using System.Text.Json;
using WatchmenBot.Features.Search.Models;
using WatchmenBot.Features.Llm.Services;

namespace WatchmenBot.Features.Search.Services;

/// <summary>
/// RAG Fusion service: generates query variations and merges results using RRF.
/// Now enhanced with HyDE (Hypothetical Document Embeddings) for better Q→A retrieval.
/// Based on: https://habr.com/ru/companies/postgrespro/articles/979820/
/// HyDE paper: https://arxiv.org/abs/2212.10496
/// </summary>
public class RagFusionService(
    LlmRouter llmRouter,
    EmbeddingService embeddingService,
    HydeService hydeService,
    ILogger<RagFusionService> logger)
{
    // RRF constant (standard value from literature)
    private const int RrfK = 60;

    /// <summary>
    /// Search with RAG Fusion: generate query variations, search each, merge with RRF.
    /// Now enhanced with HyDE for better Q→A retrieval.
    /// </summary>
    /// <param name="chatId">Chat ID</param>
    /// <param name="query">Original query</param>
    /// <param name="participantNames">Names of chat participants for context-aware query variations</param>
    /// <param name="variationCount">Number of query variations to generate</param>
    /// <param name="resultsPerQuery">Results per query</param>
    /// <param name="ct">Cancellation token</param>
    public async Task<RagFusionResponse> SearchWithFusionAsync(
        long chatId,
        string query,
        List<string>? participantNames = null,
        int variationCount = 3,
        int resultsPerQuery = 15,
        CancellationToken ct = default)
    {
        var response = new RagFusionResponse { OriginalQuery = query };
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            // Step 1: Generate query variations AND HyDE answer in parallel
            var variationsSw = System.Diagnostics.Stopwatch.StartNew();

            var variationsTask = GenerateQueryVariationsAsync(query, variationCount, participantNames, ct);
            var hydeTask = hydeService.GenerateHypotheticalAnswerAsync(query, ct);

            await Task.WhenAll(variationsTask, hydeTask);

            var variations = variationsTask.Result;
            var hydeResult = hydeTask.Result;

            variationsSw.Stop();
            response.QueryVariations = variations;

            // Log variations, HyDE answer, and patterns
            logger.LogInformation("[RAG Fusion] Generated {Count} variations + HyDE in {Ms}ms for: {Query}",
                variations.Count, variationsSw.ElapsedMilliseconds, TruncateForLog(query, 50));

            if (hydeResult.Success)
            {
                logger.LogInformation("[RAG Fusion] HyDE: '{Answer}', Patterns: [{Patterns}]",
                    TruncateForLog(hydeResult.HypotheticalAnswer ?? "", 50),
                    string.Join(", ", hydeResult.SearchPatterns.Take(3)));
            }

            // Step 2: Get embeddings for all queries in a SINGLE batch API call
            // Include: original query + variations + HyDE answer + HyDE patterns
            var allQueries = new List<string> { query };
            allQueries.AddRange(variations);

            // Add HyDE answer and patterns if successful
            if (hydeResult.Success)
            {
                if (!string.IsNullOrWhiteSpace(hydeResult.HypotheticalAnswer))
                {
                    allQueries.Add(hydeResult.HypotheticalAnswer);
                    response.HypotheticalAnswer = hydeResult.HypotheticalAnswer;
                }

                // Add search patterns (Q→A Transformation)
                foreach (var pattern in hydeResult.SearchPatterns.Where(p => !string.IsNullOrWhiteSpace(p)))
                {
                    allQueries.Add(pattern);
                }
                response.SearchPatterns = hydeResult.SearchPatterns;
            }

            // Detect bot-directed questions and add specific search patterns
            if (IsBotDirectedQuestion(query))
            {
                logger.LogInformation("[RAG Fusion] Detected bot-directed question, adding bot-specific patterns");
                var botPatterns = new[]
                {
                    "бот создан",
                    "ты создан чтобы",
                    "создан обрабатывать",
                    "бот нужен для",
                    "бот умеет"
                };
                allQueries.AddRange(botPatterns);
                response.SearchPatterns.AddRange(botPatterns);
            }

            var embeddingsSw = System.Diagnostics.Stopwatch.StartNew();
            var allEmbeddings = await embeddingService.GetBatchEmbeddingsAsync(allQueries, ct);
            embeddingsSw.Stop();

            logger.LogInformation("[RAG Fusion] Batch embeddings: {Count} queries in {Ms}ms",
                allQueries.Count, embeddingsSw.ElapsedMilliseconds);

            if (allEmbeddings.Count != allQueries.Count)
            {
                logger.LogWarning("[RAG Fusion] Embedding count mismatch: expected {Expected}, got {Actual}",
                    allQueries.Count, allEmbeddings.Count);
                // Fallback to old behavior
                var searchTasks = allQueries.Select(q =>
                    embeddingService.SearchSimilarAsync(chatId, q, resultsPerQuery, ct));
                var fallbackResults = await Task.WhenAll(searchTasks);
                return ProcessSearchResults(fallbackResults, allQueries, variations, query, sw);
            }

            // Step 3: Search by vector for each embedding in PARALLEL
            // Connection pool has ~95 free connections, safe for controlled parallelism
            var allResults = new List<SearchResult>[allQueries.Count];
            var searchTimings = new long[allQueries.Count];
            var searchErrors = new Exception?[allQueries.Count];
            var searchSw = System.Diagnostics.Stopwatch.StartNew();

            var parallelSearchTasks = allQueries.Select((_, i) => Task.Run(async () =>
            {
                var taskSw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    allResults[i] = await embeddingService.SearchByVectorAsync(
                        chatId, allEmbeddings[i], resultsPerQuery, ct, queryText: allQueries[i]);
                    taskSw.Stop();
                    searchTimings[i] = taskSw.ElapsedMilliseconds;
                }
                catch (Exception ex)
                {
                    taskSw.Stop();
                    searchTimings[i] = taskSw.ElapsedMilliseconds;
                    searchErrors[i] = ex;
                    allResults[i] = []; // Empty result on error
                    logger.LogWarning(ex,
                        "[RAG Fusion] Vector search {Index} failed after {Ms}ms: {Query}",
                        i, taskSw.ElapsedMilliseconds, TruncateForLog(allQueries[i], 50));
                }
            }, ct));

            await Task.WhenAll(parallelSearchTasks);
            searchSw.Stop();

            // Log detailed timing for each search
            var successCount = searchErrors.Count(e => e == null);
            var errorCount = searchErrors.Count(e => e != null);
            var resultCounts = allResults.Select(r => r?.Count ?? 0).ToArray();

            logger.LogInformation(
                "[RAG Fusion] Parallel vector search completed: {Success}/{Total} succeeded in {TotalMs}ms | " +
                "Individual times: [{Times}]ms | Results: [{Counts}]",
                successCount, allQueries.Count, searchSw.ElapsedMilliseconds,
                string.Join(", ", searchTimings),
                string.Join(", ", resultCounts));

            // Log errors summary if any
            if (errorCount > 0)
            {
                var errorTypes = searchErrors
                    .Where(e => e != null)
                    .GroupBy(e => e!.GetType().Name)
                    .Select(g => $"{g.Key}:{g.Count()}");
                logger.LogWarning("[RAG Fusion] {ErrorCount} searches failed: {ErrorTypes}",
                    errorCount, string.Join(", ", errorTypes));
            }

            // Step 3: Apply Reciprocal Rank Fusion
            var fusedResults = ApplyRrfFusion(allResults);

            // Step 3.5: For "who is X" questions, boost results containing name + entity word
            if (IsWhoQuestion(query) && participantNames is { Count: > 0 })
            {
                var entityWord = ExtractEntityWord(query);
                if (!string.IsNullOrEmpty(entityWord))
                {
                    ApplyEntityBoost(fusedResults, entityWord, participantNames);
                    // Re-sort after boost
                    fusedResults = fusedResults.OrderByDescending(r => r.FusedScore).ToList();
                }
            }

            // Step 3.6: Filter out near-exact matches (likely the query itself or similar previous queries)
            var filteredResults = fusedResults
                .Where(r => r.Similarity < 0.98)
                .ToList();

            if (filteredResults.Count < fusedResults.Count)
            {
                logger.LogInformation("[RAG Fusion] Filtered {Count} near-exact matches (sim >= 0.98)",
                    fusedResults.Count - filteredResults.Count);
            }

            response.Results = filteredResults;

            // Step 4: Calculate confidence based on fused scores
            if (filteredResults.Count > 0)
            {
                var bestScore = filteredResults[0].FusedScore;
                var fifthScore = filteredResults.Count >= 5
                    ? filteredResults[4].FusedScore
                    : filteredResults.Last().FusedScore;
                var gap = bestScore - fifthScore;

                response.BestScore = bestScore;
                response.ScoreGap = gap;

                // RRF scores are typically 0.01-0.05 range, so adjust thresholds
                (response.Confidence, response.ConfidenceReason) = EvaluateFusionConfidence(
                    bestScore, filteredResults.Count, allQueries.Count);
            }
            else
            {
                response.Confidence = SearchConfidence.None;
                response.ConfidenceReason = "No results from any query variation";
            }

            sw.Stop();
            response.TotalTimeMs = sw.ElapsedMilliseconds;

            logger.LogInformation(
                "[RAG Fusion] Query: '{Query}' | Variations: {Vars} | Results: {Count} | Best RRF: {Best:F4} | Confidence: {Conf} | Time: {Ms}ms",
                TruncateForLog(query, 30), variations.Count, fusedResults.Count,
                response.BestScore, response.Confidence, response.TotalTimeMs);

            return response;
        }
        catch (Exception ex)
        {
            sw.Stop();
            logger.LogError(ex, "[RAG Fusion] Failed for query: {Query}", query);

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
        string query, int count, List<string>? participantNames, CancellationToken ct)
    {
        try
        {
            // Build participant context if available
            var participantContext = "";
            if (participantNames is { Count: > 0 })
            {
                participantContext = $"""

                    УЧАСТНИКИ ЧАТА: {string.Join(", ", participantNames)}
                    ВАЖНО: Если в запросе встречается слово похожее на имя участника — это ИМЯ ЧЕЛОВЕКА!
                    """;
            }

            var systemPrompt = $"""
                Ты — помощник для улучшения поисковых запросов в чате.

                Сгенерируй {count} альтернативных формулировок запроса для поиска в истории сообщений.
                {participantContext}
                Правила:
                1. Каждая вариация должна искать ту же информацию, но другими словами
                2. Используй синонимы и перефразирования
                3. Если есть имена/ники — добавь варианты написания (Вася/Василий)
                4. ВАЖНО: Включай ТЕКСТОВЫЕ ПАТТЕРНЫ как люди пишут в чатах:
                   - Эмоции: "смеется" → ищи "ахахах", "хахаха", "лол", ")))"
                   - Согласие: "да" → "ага", "угу", "+", "ок"
                   - Удивление: → "ого", "воу", "wtf", "бля"
                5. Используй и русский, и английский если уместно
                6. КРИТИЧНО для вопросов "кто X" / "who is X":
                   - Ищем НЕ вопрос, а ОТВЕТ — утверждение что кто-то является X
                   - Генерируй ПАТТЕРНЫ ОТВЕТОВ с именами участников:
                     • "[имя] X", "[имя] это X", "X это [имя]", "[имя] — X"
                   - Пример: "кто гомик" → ["Вася гомик", "гомик это Петя", "Женя пидор"]
                   - Используй реальные имена из списка участников!

                Отвечай ТОЛЬКО JSON массивом строк, без пояснений:
                ["вариация 1", "вариация 2", "вариация 3"]
                """;

            var response = await llmRouter.CompleteWithFallbackAsync(
                new LlmRequest
                {
                    SystemPrompt = systemPrompt,
                    UserPrompt = query,
                    Temperature = 0.7 // Higher for diversity
                },
                preferredTag: null,
                ct: ct);

            // Parse JSON response - extract JSON array from any surrounding text
            var content = CleanJsonArrayResponse(response.Content);

            var variations = JsonSerializer.Deserialize<List<string>>(content);

            if (variations == null || variations.Count == 0)
            {
                logger.LogWarning("[RAG Fusion] LLM returned empty variations, using fallback");
                return GenerateFallbackVariations(query, participantNames);
            }

            // Filter and limit LLM variations
            var llmVariations = variations
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Take(count)
                .ToList();

            // For "who is X" questions, ensure we have entity-based variations as fallback
            // (in case LLM didn't follow the prompt correctly)
            if (IsWhoQuestion(query) && participantNames is { Count: > 0 })
            {
                var entityWord = ExtractEntityWord(query);
                if (!string.IsNullOrEmpty(entityWord))
                {
                    // Check if LLM variations contain any participant names
                    var hasNameVariations = llmVariations.Any(v =>
                        participantNames.Any(n => v.Contains(n, StringComparison.OrdinalIgnoreCase)));

                    if (!hasNameVariations)
                    {
                        logger.LogInformation("[RAG Fusion] Adding entity variations for 'who is X' question");
                        var entityVariations = GenerateEntityVariations(entityWord, participantNames);
                        llmVariations.AddRange(entityVariations.Take(count)); // Add up to 'count' entity variations
                    }
                }
            }

            return llmVariations.Distinct().ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[RAG Fusion] Failed to generate variations, using fallback");
            return GenerateFallbackVariations(query, participantNames);
        }
    }

    /// <summary>
    /// Fallback variations when LLM fails
    /// </summary>
    private static List<string> GenerateFallbackVariations(string query, List<string>? participantNames = null)
    {
        var variations = new List<string>();

        // For "who is X" questions, generate entity-based variations
        if (IsWhoQuestion(query) && participantNames is { Count: > 0 })
        {
            var entityWord = ExtractEntityWord(query);
            if (!string.IsNullOrEmpty(entityWord))
            {
                variations.AddRange(GenerateEntityVariations(entityWord, participantNames));
            }
        }

        // Simple keyword extraction as additional fallback
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

        return variations.Distinct().ToList();
    }

    /// <summary>
    /// Check if query is a "who is X" type question
    /// </summary>
    private static bool IsWhoQuestion(string query)
    {
        var normalized = query.ToLowerInvariant().Trim();

        // Russian patterns: "кто", "кого", "кому"
        // English patterns: "who", "who's", "who is"
        var whoPatterns = new[] { "кто ", "кто?", "а кто", "кого ", "кому ", "who ", "who's ", "who is " };

        return whoPatterns.Any(p => normalized.StartsWith(p) || normalized.Contains($" {p.Trim()}"));
    }

    /// <summary>
    /// Extract the entity/attribute word from a "who is X" question
    /// Example: "кто гомик" -> "гомик", "who is the leader" -> "leader"
    /// </summary>
    private static string? ExtractEntityWord(string query)
    {
        var normalized = query.ToLowerInvariant().Trim();

        // Remove question words and common fillers
        var stopWords = new[] { "кто", "а", "же", "тут", "здесь", "у", "нас", "из", "них", "там", "who", "is", "the", "a", "an" };

        var words = normalized
            .Split([' ', '?', '!', ',', '.'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2 && !stopWords.Contains(w))
            .ToList();

        // Return the most likely entity word (usually last significant word)
        return words.LastOrDefault();
    }

    /// <summary>
    /// Generate variations combining entity word with participant names
    /// </summary>
    private static List<string> GenerateEntityVariations(string entityWord, List<string> participantNames)
    {
        var variations = new List<string>();

        // Take top 5 participants to avoid too many queries
        foreach (var name in participantNames.Take(5))
        {
            // Pattern: "[name] [entity]" - e.g., "Вася гомик"
            variations.Add($"{name} {entityWord}");

            // Pattern: "[entity] [name]" - e.g., "гомик Вася"
            variations.Add($"{entityWord} {name}");

            // Pattern: "[name] это [entity]" - e.g., "Вася это гомик"
            variations.Add($"{name} это {entityWord}");
        }

        return variations;
    }

    /// <summary>
    /// Apply Reciprocal Rank Fusion to merge results from multiple queries
    /// Formula: score(d) = Σ 1/(k + rank(d, q))
    /// </summary>
    private List<FusedSearchResult> ApplyRrfFusion(
        List<SearchResult>[] allResults)
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
                        [queryIndex]
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

        logger.LogDebug("[RRF] Fused {InputCount} result sets into {OutputCount} unique results",
            allResults.Length, fusedResults.Count);

        // Log top results with their query contributions
        foreach (var result in fusedResults.Take(5))
        {
            var matchedQueries = result.MatchedQueryIndices
                .Select(i => i == 0 ? "original" : $"var{i}")
                .ToList();

            logger.LogDebug("[RRF] MsgId={Id} | RRF={Score:F4} | Sim={Sim:F3} | Queries=[{Queries}]",
                result.MessageId, result.FusedScore, result.Similarity, string.Join(",", matchedQueries));
        }

        return fusedResults;
    }

    /// <summary>
    /// Boost results that contain both a participant name and the entity word
    /// This helps surface "X is Y" statements for "who is Y" questions
    /// </summary>
    private void ApplyEntityBoost(List<FusedSearchResult> results, string entityWord, List<string> participantNames)
    {
        const double entityBoostMultiplier = 1.5; // 50% boost
        var boostedCount = 0;

        foreach (var result in results)
        {
            var text = result.ChunkText?.ToLowerInvariant() ?? "";

            // Check if text contains the entity word
            var hasEntity = text.Contains(entityWord.ToLowerInvariant());
            if (!hasEntity) continue;

            // Check if text contains any participant name
            var matchedName = participantNames.FirstOrDefault(n =>
                text.Contains(n.ToLowerInvariant()));

            if (matchedName != null)
            {
                result.FusedScore *= entityBoostMultiplier;
                boostedCount++;

                logger.LogDebug("[Entity Boost] MsgId={Id} boosted (name={Name}, entity={Entity})",
                    result.MessageId, matchedName, entityWord);
            }
        }

        if (boostedCount > 0)
        {
            logger.LogInformation("[Entity Boost] Boosted {Count} results for entity '{Entity}'",
                boostedCount, entityWord);
        }
    }

    /// <summary>
    /// Evaluate confidence based on RRF scores
    /// RRF scores are typically in 0.01-0.05 range (much lower than similarity scores)
    /// </summary>
    private static (SearchConfidence confidence, string reason) EvaluateFusionConfidence(
        double bestScore, int resultCount, int queryCount)
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

    /// <summary>
    /// Process search results into RagFusionResponse (used for fallback when batch fails)
    /// </summary>
    private RagFusionResponse ProcessSearchResults(
        List<SearchResult>[] allResults,
        List<string> allQueries,
        List<string> variations,
        string originalQuery,
        System.Diagnostics.Stopwatch sw)
    {
        var response = new RagFusionResponse
        {
            OriginalQuery = originalQuery,
            QueryVariations = variations
        };

        var fusedResults = ApplyRrfFusion(allResults);

        var filteredResults = fusedResults
            .Where(r => r.Similarity < 0.98)
            .ToList();

        response.Results = filteredResults;

        if (filteredResults.Count > 0)
        {
            var bestScore = filteredResults[0].FusedScore;
            (response.Confidence, response.ConfidenceReason) = EvaluateFusionConfidence(
                bestScore, filteredResults.Count, allQueries.Count);
            response.BestScore = bestScore;
        }
        else
        {
            response.Confidence = SearchConfidence.None;
            response.ConfidenceReason = "No results from any query variation";
        }

        sw.Stop();
        response.TotalTimeMs = sw.ElapsedMilliseconds;

        logger.LogInformation(
            "[RAG Fusion] Fallback: Query: '{Query}' | Results: {Count} | Time: {Ms}ms",
            TruncateForLog(originalQuery, 30), filteredResults.Count, response.TotalTimeMs);

        return response;
    }

    private static string TruncateForLog(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            return text;
        return text[..(maxLength - 3)] + "...";
    }

    /// <summary>
    /// Clean LLM response to extract JSON array even if surrounded by text.
    /// Handles: markdown code blocks, explanatory text before/after array.
    /// </summary>
    private static string CleanJsonArrayResponse(string content)
    {
        var cleaned = content.Trim();

        // Remove markdown code blocks
        if (cleaned.StartsWith("```"))
        {
            var lines = cleaned.Split('\n');
            cleaned = string.Join("\n", lines.Skip(1).TakeWhile(l => !l.StartsWith("```")));
        }

        // Find JSON array boundaries (handles "Вот варианты: [...]" case)
        var start = cleaned.IndexOf('[');
        var end = cleaned.LastIndexOf(']');

        if (start >= 0 && end > start)
        {
            cleaned = cleaned[start..(end + 1)];
        }

        return cleaned.Trim();
    }

    /// <summary>
    /// Detect if question is directed at the bot (using "ты", "бот", etc.)
    /// </summary>
    private static bool IsBotDirectedQuestion(string query)
    {
        var q = query.ToLowerInvariant();

        // Direct bot address patterns
        var botPatterns = new[]
        {
            "ты создан",
            "ты умеешь",
            "ты можешь",
            "ты знаешь",
            "ты думаешь",
            "зачем ты",
            "для чего ты",
            "кто ты",
            "что ты",
            "как ты",
            "почему ты",
            "твоя цель",
            "твоё назначение",
            "бот ",
            "ботик",
        };

        return botPatterns.Any(p => q.Contains(p));
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
    public List<int> MatchedQueryIndices { get; set; } = [];
}

/// <summary>
/// Response from RAG Fusion search
/// </summary>
public class RagFusionResponse
{
    public string OriginalQuery { get; set; } = string.Empty;

    public List<string> QueryVariations { get; set; } = [];

    /// <summary>
    /// HyDE (Hypothetical Document Embeddings) - generated hypothetical answer.
    /// Used for better Q→A retrieval by searching in "answer space" instead of "question space".
    /// </summary>
    public string? HypotheticalAnswer { get; set; }

    /// <summary>
    /// Q→A Transformation patterns - structural phrases that would appear in real answers.
    /// </summary>
    public List<string> SearchPatterns { get; set; } = [];

    public List<FusedSearchResult> Results { get; set; } = [];

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
