using System.Text.RegularExpressions;
using WatchmenBot.Features.Search.Models;

namespace WatchmenBot.Features.Search.Services;

/// <summary>
/// Simplified RAG Fusion service: Keyword + Vector search with RRF fusion and Reranking.
///
/// Pipeline:
/// Query → Keyword Search (GIN) ─────────────┐
///       → Vector Search (original)          │
///       → Vector Search (structural vars)   ├─→ RRF Fusion → Reranker → Top-N
///       → Vector Search (entity vars)       │
///
/// No LLM calls for variations (faster, no hallucinations).
/// Cross-encoder reranker provides final relevance scoring.
/// </summary>
public partial class RagFusionService(
    EmbeddingService embeddingService,
    CohereRerankService reranker,
    ILogger<RagFusionService> logger)
{
    // RRF constant (standard value from literature)
    private const int RrfK = 60;

    // Increased pool sizes to catch semantically distant but relevant results
    // Cross-encoder reranker will filter out noise, so bigger pool = better recall
    private const int ResultsPerQuery = 60;
    private const int RerankerTopN = 100;

    /// <summary>
    /// Search with simplified RAG Fusion: structural variations + keyword search + reranking.
    /// </summary>
    public async Task<RagFusionResponse> SearchWithFusionAsync(
        long chatId,
        string query,
        List<string>? participantNames = null,
        int variationCount = 3,
        int resultsPerQuery = ResultsPerQuery,
        CancellationToken ct = default)
    {
        var response = new RagFusionResponse { OriginalQuery = query };
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            // Step 1: Generate structural variations (NO LLM - just patterns)
            var variations = GenerateStructuralVariations(query, participantNames);
            response.QueryVariations = variations;

            logger.LogInformation("[RAG Fusion] Generated {Count} structural variations for: {Query}",
                variations.Count, TruncateForLog(query, 50));

            // Step 2: Build all queries for embedding
            var allQueries = new List<string> { query };
            allQueries.AddRange(variations);

            // Step 3: Get embeddings in batch
            var embeddingsSw = System.Diagnostics.Stopwatch.StartNew();
            var allEmbeddings = await embeddingService.GetBatchEmbeddingsAsync(allQueries, ct);
            embeddingsSw.Stop();

            logger.LogInformation("[RAG Fusion] Batch embeddings: {Count} queries in {Ms}ms",
                allQueries.Count, embeddingsSw.ElapsedMilliseconds);

            if (allEmbeddings.Count != allQueries.Count)
            {
                logger.LogWarning("[RAG Fusion] Embedding count mismatch: expected {Expected}, got {Actual}",
                    allQueries.Count, allEmbeddings.Count);
                return CreateEmptyResponse(response, sw, "Embedding generation failed");
            }

            // Step 4: Parallel search - Vector + Keyword
            var searchSw = System.Diagnostics.Stopwatch.StartNew();

            // 4.1: Vector searches in parallel
            var vectorResults = new List<SearchResult>[allQueries.Count];
            var vectorTasks = allQueries.Select((q, i) => Task.Run(async () =>
            {
                try
                {
                    vectorResults[i] = await embeddingService.SearchByVectorAsync(
                        chatId, allEmbeddings[i], resultsPerQuery, ct, queryText: q);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "[RAG Fusion] Vector search {Index} failed", i);
                    vectorResults[i] = [];
                }
            }, ct));

            // 4.2: Keyword search (extracts significant words from query)
            var keywordTask = Task.Run(async () =>
            {
                var keywords = ExtractKeywords(query, variations);
                if (string.IsNullOrEmpty(keywords))
                    return new List<SearchResult>();

                try
                {
                    return await embeddingService.HybridSearchAsync(
                        chatId, allEmbeddings[0], keywords, resultsPerQuery * 2, ct);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "[RAG Fusion] Keyword search failed for: {Keywords}", keywords);
                    return new List<SearchResult>();
                }
            }, ct);

            await Task.WhenAll(vectorTasks.Concat([keywordTask]));
            var keywordResults = await keywordTask;

            searchSw.Stop();

            // Log search results
            var vectorTotalResults = vectorResults.Sum(r => r?.Count ?? 0);
            logger.LogInformation(
                "[RAG Fusion] Search completed in {Ms}ms: vector={VectorCount} from {Queries} queries, keyword={KeywordCount}",
                searchSw.ElapsedMilliseconds, vectorTotalResults, allQueries.Count, keywordResults.Count);

            // Step 5: RRF Fusion
            var allResultsForFusion = vectorResults.ToList();
            if (keywordResults.Count > 0)
            {
                allResultsForFusion.Add(keywordResults);
            }

            var fusedResults = ApplyRrfFusion(allResultsForFusion.ToArray());

            // Step 5.5: Entity boost for "who is X" questions
            if (IsWhoQuestion(query) && participantNames is { Count: > 0 })
            {
                var entityWord = ExtractEntityWord(query);
                if (!string.IsNullOrEmpty(entityWord))
                {
                    ApplyEntityBoost(fusedResults, entityWord, participantNames);
                    fusedResults = fusedResults.OrderByDescending(r => r.FusedScore).ToList();
                }
            }

            // Filter near-exact matches
            var filteredResults = fusedResults
                .Where(r => r.Similarity < 0.98)
                .ToList();

            if (filteredResults.Count < fusedResults.Count)
            {
                logger.LogInformation("[RAG Fusion] Filtered {Count} near-exact matches",
                    fusedResults.Count - filteredResults.Count);
            }

            // Step 6: Rerank with cross-encoder
            List<SearchResult> finalResults;
            if (reranker.IsConfigured && filteredResults.Count > 0)
            {
                var rerankSw = System.Diagnostics.Stopwatch.StartNew();

                // Convert FusedSearchResult to SearchResult for reranker
                var resultsForRerank = filteredResults.Cast<SearchResult>().ToList();
                var reranked = await reranker.RerankAsync(query, resultsForRerank, RerankerTopN, ct);

                rerankSw.Stop();
                logger.LogInformation("[RAG Fusion] Reranked {Input} → {Output} results in {Ms}ms",
                    filteredResults.Count, reranked.Count, rerankSw.ElapsedMilliseconds);

                // Convert back to FusedSearchResult preserving reranker scores
                finalResults = reranked;

                // Update response with reranked results
                response.Results = reranked.Select(r => new FusedSearchResult
                {
                    MessageId = r.MessageId,
                    ChatId = r.ChatId,
                    ChunkIndex = r.ChunkIndex,
                    ChunkText = r.ChunkText,
                    MetadataJson = r.MetadataJson,
                    Distance = r.Distance,
                    Similarity = r.Similarity, // Now contains reranker score
                    IsNewsDump = r.IsNewsDump,
                    FusedScore = r.Similarity, // Use reranker score as fused score
                    MatchedQueryCount = 1,
                    MatchedQueryIndices = [0]
                }).ToList();
            }
            else
            {
                response.Results = filteredResults;
                finalResults = filteredResults.Cast<SearchResult>().ToList();
            }

            // Step 7: Calculate confidence
            if (response.Results.Count > 0)
            {
                var bestScore = response.Results[0].FusedScore;
                response.BestScore = bestScore;

                // With reranker, scores are 0-1 relevance scores
                if (reranker.IsConfigured)
                {
                    (response.Confidence, response.ConfidenceReason) = EvaluateRerankerConfidence(
                        bestScore, response.Results.Count);
                }
                else
                {
                    (response.Confidence, response.ConfidenceReason) = EvaluateFusionConfidence(
                        bestScore, response.Results.Count, allQueries.Count);
                }
            }
            else
            {
                response.Confidence = SearchConfidence.None;
                response.ConfidenceReason = "No results found";
            }

            sw.Stop();
            response.TotalTimeMs = sw.ElapsedMilliseconds;

            logger.LogInformation(
                "[RAG Fusion] Query: '{Query}' | Results: {Count} | Best: {Best:F3} | Confidence: {Conf} | Time: {Ms}ms",
                TruncateForLog(query, 30), response.Results.Count,
                response.BestScore, response.Confidence, response.TotalTimeMs);

            return response;
        }
        catch (Exception ex)
        {
            sw.Stop();
            logger.LogError(ex, "[RAG Fusion] Failed for query: {Query}", query);
            return CreateEmptyResponse(response, sw, "Search failed");
        }
    }

    /// <summary>
    /// Generate structural variations without LLM.
    /// Uses morphology patterns and entity-based variations.
    /// </summary>
    private static List<string> GenerateStructuralVariations(string query, List<string>? participantNames)
    {
        var variations = new List<string>();
        var normalized = query.ToLowerInvariant().Trim();

        // 1. For "who is X" questions - generate "[name] X" patterns
        if (IsWhoQuestion(normalized) && participantNames is { Count: > 0 })
        {
            var entityWord = ExtractEntityWord(query);
            if (!string.IsNullOrEmpty(entityWord))
            {
                variations.AddRange(GenerateEntityVariations(entityWord, participantNames));
            }
        }

        // 2. Extract significant words and create variations
        var words = ExtractSignificantWords(normalized);

        if (words.Count >= 1)
        {
            // Pattern: just the main keyword(s)
            variations.Add(string.Join(" ", words.Take(2)));

            // Pattern: reversed order
            if (words.Count >= 2)
            {
                variations.Add(string.Join(" ", words.Take(2).Reverse()));
            }
        }

        // 3. For questions with specific structure, add answer patterns
        // "кто сосун" → "сосун", "[name] сосун"
        // "что обсуждали" → "обсуждали", "говорили о"

        return variations.Distinct().Take(10).ToList(); // Limit to avoid too many queries
    }

    /// <summary>
    /// Extract keywords for hybrid search from query.
    /// Returns OR query for PostgreSQL tsquery.
    /// </summary>
    private static string ExtractKeywords(string query, List<string> variations)
    {
        var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "а", "и", "в", "на", "с", "к", "о", "у", "за", "из", "по", "до", "от", "для",
            "не", "что", "как", "это", "так", "все", "он", "она", "они", "мы", "вы",
            "кто", "где", "когда", "почему", "зачем", "какой", "какая", "какие",
            "был", "была", "было", "были", "есть", "будет", "может", "нужно", "надо"
        };

        var allWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Extract from query
        var queryWords = Regex.Matches(query.ToLowerInvariant(), @"[\p{L}]+")
            .Select(m => m.Value)
            .Where(w => w.Length >= 3 && !stopWords.Contains(w));

        foreach (var word in queryWords)
            allWords.Add(word);

        // Extract from variations
        foreach (var variation in variations)
        {
            var varWords = Regex.Matches(variation.ToLowerInvariant(), @"[\p{L}]+")
                .Select(m => m.Value)
                .Where(w => w.Length >= 3 && !stopWords.Contains(w));

            foreach (var word in varWords)
                allWords.Add(word);
        }

        if (allWords.Count == 0)
            return "";

        return string.Join(" | ", allWords);
    }

    /// <summary>
    /// Extract significant words from query (filter stop words).
    /// </summary>
    private static List<string> ExtractSignificantWords(string text)
    {
        var stopWords = new HashSet<string>
        {
            "а", "и", "в", "на", "с", "к", "о", "у", "кто", "что", "как", "где", "когда",
            "не", "это", "за", "из", "по", "до", "от", "для", "же", "ли", "бы"
        };

        return Regex.Matches(text, @"[\p{L}]+")
            .Select(m => m.Value)
            .Where(w => w.Length >= 3 && !stopWords.Contains(w))
            .ToList();
    }

    /// <summary>
    /// Check if query is a "who is X" type question.
    /// </summary>
    private static bool IsWhoQuestion(string query)
    {
        var normalized = query.ToLowerInvariant().Trim();
        var whoPatterns = new[] { "кто ", "кто?", "а кто", "кого ", "кому ", "who ", "who's ", "who is " };
        return whoPatterns.Any(p => normalized.StartsWith(p) || normalized.Contains($" {p.Trim()}"));
    }

    /// <summary>
    /// Extract the entity word from a "who is X" question.
    /// </summary>
    private static string? ExtractEntityWord(string query)
    {
        var normalized = query.ToLowerInvariant().Trim();
        var stopWords = new[] { "кто", "а", "же", "тут", "здесь", "у", "нас", "из", "них", "там", "who", "is", "the" };

        var words = normalized
            .Split([' ', '?', '!', ',', '.'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2 && !stopWords.Contains(w))
            .ToList();

        return words.LastOrDefault();
    }

    /// <summary>
    /// Generate "[name] [entity]" variations for "who is X" questions.
    /// </summary>
    private static List<string> GenerateEntityVariations(string entityWord, List<string> participantNames)
    {
        var variations = new List<string>();

        foreach (var name in participantNames.Take(5))
        {
            variations.Add($"{name} {entityWord}");
            variations.Add($"{entityWord} {name}");
            variations.Add($"{name} это {entityWord}");
        }

        return variations;
    }

    /// <summary>
    /// Apply RRF fusion to merge results from multiple queries.
    /// </summary>
    private List<FusedSearchResult> ApplyRrfFusion(List<SearchResult>[] allResults)
    {
        var fusionScores = new Dictionary<long, (double Score, SearchResult Result, List<int> QueryIndices)>();

        for (var queryIndex = 0; queryIndex < allResults.Length; queryIndex++)
        {
            var results = allResults[queryIndex];
            if (results == null) continue;

            for (var rank = 0; rank < results.Count; rank++)
            {
                var result = results[rank];
                var rrfScore = 1.0 / (RrfK + rank + 1);

                if (fusionScores.TryGetValue(result.MessageId, out var existing))
                {
                    existing.QueryIndices.Add(queryIndex);
                    fusionScores[result.MessageId] = (
                        existing.Score + rrfScore,
                        result.Similarity > existing.Result.Similarity ? result : existing.Result,
                        existing.QueryIndices
                    );
                }
                else
                {
                    fusionScores[result.MessageId] = (rrfScore, result, [queryIndex]);
                }
            }
        }

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

        return fusedResults;
    }

    /// <summary>
    /// Boost results containing both entity word and participant name.
    /// </summary>
    private void ApplyEntityBoost(List<FusedSearchResult> results, string entityWord, List<string> participantNames)
    {
        const double boostMultiplier = 1.5;
        var boostedCount = 0;

        foreach (var result in results)
        {
            var text = result.ChunkText?.ToLowerInvariant() ?? "";
            if (!text.Contains(entityWord.ToLowerInvariant())) continue;

            var matchedName = participantNames.FirstOrDefault(n => text.Contains(n.ToLowerInvariant()));
            if (matchedName != null)
            {
                result.FusedScore *= boostMultiplier;
                boostedCount++;
            }
        }

        if (boostedCount > 0)
        {
            logger.LogInformation("[Entity Boost] Boosted {Count} results for '{Entity}'", boostedCount, entityWord);
        }
    }

    /// <summary>
    /// Evaluate confidence based on reranker scores (0-1 range).
    /// </summary>
    private static (SearchConfidence, string) EvaluateRerankerConfidence(double bestScore, int resultCount)
    {
        // Reranker scores are 0-1 relevance scores
        if (bestScore >= 0.8)
            return (SearchConfidence.High, $"Rerank={bestScore:F3}, high relevance");
        if (bestScore >= 0.5)
            return (SearchConfidence.Medium, $"Rerank={bestScore:F3}, medium relevance");
        if (bestScore >= 0.3 || resultCount >= 5)
            return (SearchConfidence.Low, $"Rerank={bestScore:F3}, low relevance");
        return (SearchConfidence.None, $"Rerank={bestScore:F3}, no confident match");
    }

    /// <summary>
    /// Evaluate confidence based on RRF scores (fallback when reranker disabled).
    /// </summary>
    private static (SearchConfidence, string) EvaluateFusionConfidence(double bestScore, int resultCount, int queryCount)
    {
        var maxPossible = queryCount * (1.0 / (RrfK + 1));
        var normalizedBest = bestScore / maxPossible;
        var appearsInMultiple = bestScore > (2.0 / (RrfK + 5));

        if (normalizedBest >= 0.7 || appearsInMultiple)
            return (SearchConfidence.High, $"RRF={bestScore:F4}, multi-query match");
        if (normalizedBest >= 0.4)
            return (SearchConfidence.Medium, $"RRF={bestScore:F4}");
        if (normalizedBest >= 0.2 || resultCount >= 5)
            return (SearchConfidence.Low, $"RRF={bestScore:F4}, weak match");
        return (SearchConfidence.None, $"RRF={bestScore:F4} too low");
    }

    private static RagFusionResponse CreateEmptyResponse(RagFusionResponse response, System.Diagnostics.Stopwatch sw, string reason)
    {
        sw.Stop();
        response.Confidence = SearchConfidence.None;
        response.ConfidenceReason = reason;
        response.TotalTimeMs = sw.ElapsedMilliseconds;
        return response;
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
    public double FusedScore { get; set; }
    public int MatchedQueryCount { get; set; }
    public List<int> MatchedQueryIndices { get; set; } = [];
}

/// <summary>
/// Response from RAG Fusion search
/// </summary>
public class RagFusionResponse
{
    public string OriginalQuery { get; set; } = string.Empty;
    public List<string> QueryVariations { get; set; } = [];
    public List<FusedSearchResult> Results { get; set; } = [];
    public SearchConfidence Confidence { get; set; }
    public string? ConfidenceReason { get; set; }
    public double BestScore { get; set; }
    public double ScoreGap { get; set; }
    public long TotalTimeMs { get; set; }

    public SearchResponse ToSearchResponse()
    {
        return new SearchResponse
        {
            Results = Results.Cast<SearchResult>().ToList(),
            Confidence = Confidence,
            ConfidenceReason = ConfidenceReason,
            BestScore = BestScore,
            ScoreGap = ScoreGap,
            HasFullTextMatch = false
        };
    }
}
