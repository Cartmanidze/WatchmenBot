using System.Text.RegularExpressions;
using WatchmenBot.Features.Search.Models;

namespace WatchmenBot.Features.Search.Services;

/// <summary>
/// Simplified RAG Fusion service: Keyword + Vector search with RRF fusion and Reranking.
///
/// Pipeline:
/// Query → Keyword Search (GIN) ─┬─→ RRF Fusion → Reranker → Top-N
///       → Vector Search         ─┘
///
/// No query variations — single vector search + keyword search.
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
    /// Search with RAG Fusion: vector search + keyword search + reranking.
    /// No query variations — single embedding for better performance.
    /// </summary>
    public async Task<RagFusionResponse> SearchWithFusionAsync(
        long chatId,
        string query,
        int variationCount = 3, // Kept for API compatibility, ignored
        int resultsPerQuery = ResultsPerQuery,
        CancellationToken ct = default)
    {
        var response = new RagFusionResponse { OriginalQuery = query };
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            // No variations — just the original query
            response.QueryVariations = [];

            // Step 1: Get single embedding for query
            var embeddingsSw = System.Diagnostics.Stopwatch.StartNew();
            var embeddings = await embeddingService.GetBatchEmbeddingsAsync([query], ct);
            embeddingsSw.Stop();

            if (embeddings.Count == 0)
            {
                logger.LogWarning("[RAG Fusion] Failed to get embedding for query");
                return CreateEmptyResponse(response, sw, "Embedding generation failed");
            }

            var queryEmbedding = embeddings[0];
            logger.LogInformation("[RAG Fusion] Embedding in {Ms}ms for: {Query}",
                embeddingsSw.ElapsedMilliseconds, TruncateForLog(query, 50));

            // Step 2: Parallel search - Vector + Keyword
            var searchSw = System.Diagnostics.Stopwatch.StartNew();

            // 2.1: Vector search
            var vectorTask = embeddingService.SearchByVectorAsync(
                chatId, queryEmbedding, resultsPerQuery, ct, queryText: query);

            // 2.2: Keyword search
            var keywordTask = Task.Run(async () =>
            {
                var keywords = ExtractKeywords(query);
                if (string.IsNullOrEmpty(keywords))
                    return new List<SearchResult>();

                try
                {
                    return await embeddingService.HybridSearchAsync(
                        chatId, queryEmbedding, keywords, resultsPerQuery * 2, ct);
                }
                catch (OperationCanceledException)
                {
                    // Propagate cancellation - don't swallow
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "[RAG Fusion] Keyword search failed for: {Keywords}", keywords);
                    return new List<SearchResult>();
                }
            }, ct);

            await Task.WhenAll(vectorTask, keywordTask);

            var vectorResults = await vectorTask;
            var keywordResults = await keywordTask;

            searchSw.Stop();

            logger.LogInformation(
                "[RAG Fusion] Search completed in {Ms}ms: vector={VectorCount}, keyword={KeywordCount}",
                searchSw.ElapsedMilliseconds, vectorResults.Count, keywordResults.Count);

            // Step 3: RRF Fusion (merge vector + keyword results)
            var allResultsForFusion = new List<List<SearchResult>> { vectorResults };
            if (keywordResults.Count > 0)
            {
                allResultsForFusion.Add(keywordResults);
            }

            var fusedResults = ApplyRrfFusion(allResultsForFusion.ToArray());

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
                    IsQuestionEmbedding = r.IsQuestionEmbedding, // Preserve Q→A bridge flag for dedup
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
                    // queryCount = 2 (vector + keyword search)
                    (response.Confidence, response.ConfidenceReason) = EvaluateFusionConfidence(
                        bestScore, response.Results.Count, queryCount: 2);
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
        catch (OperationCanceledException)
        {
            // Propagate cancellation properly - don't treat as error
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            logger.LogError(ex, "[RAG Fusion] Failed for query: {Query}", query);
            return CreateEmptyResponse(response, sw, "Search failed");
        }
    }

    /// <summary>
    /// Extract keywords for hybrid search from query.
    /// Returns OR query for PostgreSQL tsquery.
    /// </summary>
    private static string ExtractKeywords(string query)
    {
        var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "а", "и", "в", "на", "с", "к", "о", "у", "за", "из", "по", "до", "от", "для",
            "не", "что", "как", "это", "так", "все", "он", "она", "они", "мы", "вы",
            "кто", "где", "когда", "почему", "зачем", "какой", "какая", "какие",
            "был", "была", "было", "были", "есть", "будет", "может", "нужно", "надо"
        };

        var words = Regex.Matches(query.ToLowerInvariant(), @"[\p{L}]+")
            .Select(m => m.Value)
            .Where(w => w.Length >= 3 && !stopWords.Contains(w))
            .Distinct()
            .ToList();

        if (words.Count == 0)
            return "";

        return string.Join(" | ", words);
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
                    // Prefer non-question embeddings over question embeddings for better dedup later.
                    // If both are same type (both question or both non-question), use higher similarity.
                    var preferredResult = SelectBetterResult(existing.Result, result);
                    fusionScores[result.MessageId] = (
                        existing.Score + rrfScore,
                        preferredResult,
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
                IsQuestionEmbedding = kv.Value.Result.IsQuestionEmbedding, // Preserve Q→A bridge flag for dedup
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
    /// Select the better result when merging duplicates.
    /// Prefers non-question embeddings over question embeddings (Q→A bridge).
    /// If both are same type, selects higher similarity.
    /// </summary>
    private static SearchResult SelectBetterResult(SearchResult existing, SearchResult candidate)
    {
        // Case 1: existing is non-question, candidate is question → keep existing
        if (!existing.IsQuestionEmbedding && candidate.IsQuestionEmbedding)
            return existing;

        // Case 2: existing is question, candidate is non-question → prefer candidate
        if (existing.IsQuestionEmbedding && !candidate.IsQuestionEmbedding)
            return candidate;

        // Case 3: both same type → use higher similarity
        return candidate.Similarity > existing.Similarity ? candidate : existing;
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
