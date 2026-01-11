using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WatchmenBot.Features.Search.Models;

namespace WatchmenBot.Features.Search.Services;

/// <summary>
/// Cross-encoder reranker using Cohere Rerank API.
/// Improves search quality by re-scoring candidates with a model that sees query+document together.
/// </summary>
public class CohereRerankService
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly bool _isConfigured;
    private readonly ILogger<CohereRerankService> _logger;

    private const string BaseUrl = "https://api.cohere.com/v2/rerank";
    private const int DefaultTopN = 10;
    private const int MaxDocuments = 100; // Stay well under 1000 limit for performance

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public CohereRerankService(
        HttpClient httpClient,
        string apiKey,
        string model,
        ILogger<CohereRerankService> logger)
    {
        _httpClient = httpClient;
        _apiKey = apiKey;
        _model = string.IsNullOrWhiteSpace(model) ? "rerank-v4.0-pro" : model;
        _isConfigured = !string.IsNullOrWhiteSpace(apiKey);
        _logger = logger;

        if (_isConfigured)
        {
            _logger.LogInformation("[Rerank] Cohere reranker configured with model: {Model}", _model);
        }
        else
        {
            _logger.LogWarning("[Rerank] Cohere API key not configured - reranking disabled");
        }
    }

    /// <summary>
    /// Whether the reranker is properly configured and available
    /// </summary>
    public bool IsConfigured => _isConfigured;

    /// <summary>
    /// Rerank search results using Cohere cross-encoder.
    /// Returns results ordered by relevance with updated scores.
    /// </summary>
    /// <param name="query">The search query</param>
    /// <param name="results">Search results to rerank</param>
    /// <param name="topN">Number of top results to return (default 10)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Reranked results with updated similarity scores</returns>
    public async Task<List<SearchResult>> RerankAsync(
        string query,
        List<SearchResult> results,
        int topN = DefaultTopN,
        CancellationToken ct = default)
    {
        if (!_isConfigured)
        {
            _logger.LogDebug("[Rerank] Skipping rerank - not configured");
            return results.Take(topN).ToList();
        }

        if (results.Count == 0)
        {
            return results;
        }

        // Limit documents for performance
        var documentsToRerank = results.Take(MaxDocuments).ToList();

        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            var request = new CohereRerankRequest
            {
                Model = _model,
                Query = query,
                Documents = documentsToRerank.Select(r => r.ChunkText ?? "").ToList(),
                TopN = Math.Min(topN, documentsToRerank.Count)
            };

            var json = JsonSerializer.Serialize(request, JsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, BaseUrl);
            httpRequest.Content = content;
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            httpRequest.Headers.Add("X-Client-Name", "WatchmenBot");

            using var response = await _httpClient.SendAsync(httpRequest, ct);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("[Rerank] API error {Status}: {Body}",
                    response.StatusCode, errorBody.Length > 200 ? errorBody[..200] : errorBody);
                return results.Take(topN).ToList();
            }

            var responseJson = await response.Content.ReadAsStringAsync(ct);
            var rerankResponse = JsonSerializer.Deserialize<CohereRerankResponse>(responseJson, JsonOptions);

            sw.Stop();

            if (rerankResponse?.Results == null || rerankResponse.Results.Count == 0)
            {
                _logger.LogWarning("[Rerank] Empty response from Cohere API");
                return results.Take(topN).ToList();
            }

            // Map reranked results back to original SearchResult objects
            var rerankedResults = new List<SearchResult>();
            foreach (var item in rerankResponse.Results)
            {
                if (item.Index >= 0 && item.Index < documentsToRerank.Count)
                {
                    var original = documentsToRerank[item.Index];
                    rerankedResults.Add(new SearchResult
                    {
                        ChatId = original.ChatId,
                        MessageId = original.MessageId,
                        ChunkIndex = original.ChunkIndex,
                        ChunkText = original.ChunkText,
                        MetadataJson = original.MetadataJson,
                        Distance = original.Distance,
                        Similarity = item.RelevanceScore, // Use reranker score
                        IsNewsDump = original.IsNewsDump,
                        IsQuestionEmbedding = original.IsQuestionEmbedding, // Preserve Q→A bridge flag for dedup
                        IsContextWindow = original.IsContextWindow // Preserve context window flag to avoid re-expansion
                    });
                }
            }

            // Log token usage if available
            var inputTokens = rerankResponse.Meta?.BilledUnits?.SearchUnits ?? 0;
            _logger.LogInformation(
                "[Rerank] Reranked {Input} → {Output} results in {Ms}ms (units: {Units})",
                documentsToRerank.Count, rerankedResults.Count, sw.ElapsedMilliseconds, inputTokens);

            return rerankedResults;
        }
        catch (TaskCanceledException)
        {
            _logger.LogDebug("[Rerank] Request cancelled");
            return results.Take(topN).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Rerank] Failed to rerank results");
            return results.Take(topN).ToList();
        }
    }

    #region Request/Response Models

    private class CohereRerankRequest
    {
        public string Model { get; set; } = "";
        public string Query { get; set; } = "";
        public List<string> Documents { get; set; } = [];
        public int? TopN { get; set; }
        public int? MaxTokensPerDoc { get; set; }
    }

    private class CohereRerankResponse
    {
        public string? Id { get; set; }
        public List<RerankResult> Results { get; set; } = [];
        public RerankMeta? Meta { get; set; }
    }

    private class RerankResult
    {
        public int Index { get; set; }
        public double RelevanceScore { get; set; }
    }

    private class RerankMeta
    {
        public BilledUnits? BilledUnits { get; set; }
    }

    private class BilledUnits
    {
        public double? SearchUnits { get; set; }
        public double? InputTokens { get; set; }
    }

    #endregion
}
