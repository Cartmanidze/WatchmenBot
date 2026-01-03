using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace WatchmenBot.Features.Search.Services;

public enum EmbeddingProvider
{
    OpenAI,
    HuggingFace,
    Jina
}

/// <summary>
/// Task type for Jina embeddings (affects embedding optimization)
/// </summary>
public enum EmbeddingTask
{
    /// <summary>For indexing documents in vector DB</summary>
    RetrievalPassage,
    /// <summary>For search queries</summary>
    RetrievalQuery
}

public class EmbeddingClient
{
    private readonly HttpClient _httpClient;
    private readonly string _model;
    private readonly string _apiKey;
    private readonly string _baseUrl;
    private readonly int _dimensions;
    private readonly EmbeddingProvider _provider;
    private readonly ILogger<EmbeddingClient> _logger;
    private readonly bool _isConfigured;

    // Usage tracking (static to persist across scopes)
    private static long _totalTokensUsed;
    private static int _totalRequests;
    private static readonly object _statsLock = new();

    // Pricing varies by provider
    private const double OpenAiPricePerThousandTokens = 0.00002; // text-embedding-3-small
    private const double HuggingFacePricePerThousandTokens = 0.0; // Free tier
    private const double JinaPricePerThousandTokens = 0.00002; // jina-embeddings-v3

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public EmbeddingClient(
        HttpClient httpClient,
        string apiKey,
        string baseUrl,
        string model,
        int dimensions,
        EmbeddingProvider provider,
        ILogger<EmbeddingClient> logger)
    {
        _httpClient = httpClient;
        _apiKey = apiKey;
        _provider = provider;
        _logger = logger;
        _isConfigured = !string.IsNullOrWhiteSpace(apiKey);

        // Set defaults based on provider
        if (_provider == EmbeddingProvider.Jina)
        {
            _baseUrl = string.IsNullOrWhiteSpace(baseUrl)
                ? "https://api.jina.ai/v1"
                : baseUrl.TrimEnd('/');
            _model = string.IsNullOrWhiteSpace(model) ? "jina-embeddings-v3" : model;
            _dimensions = dimensions > 0 ? dimensions : 1024;
        }
        else if (_provider == EmbeddingProvider.HuggingFace)
        {
            _baseUrl = string.IsNullOrWhiteSpace(baseUrl)
                ? "https://router.huggingface.co/hf-inference/models/deepvk/USER-bge-m3/pipeline/feature-extraction"
                : baseUrl.TrimEnd('/');
            _model = model; // Not used in HuggingFace requests
            _dimensions = dimensions > 0 ? dimensions : 1024;
        }
        else // OpenAI
        {
            _baseUrl = string.IsNullOrWhiteSpace(baseUrl)
                ? "https://api.openai.com/v1"
                : baseUrl.TrimEnd('/');
            _model = string.IsNullOrWhiteSpace(model) ? "text-embedding-3-small" : model;
            _dimensions = dimensions > 0 ? dimensions : 1536;
        }

        _logger.LogInformation("Embeddings configured: Provider={Provider}, Dimensions={Dimensions}",
            _provider, _dimensions);
    }

    public bool IsConfigured => _isConfigured;
    public EmbeddingProvider Provider => _provider;
    public int Dimensions => _dimensions;

    /// <summary>
    /// Get embedding for a single text
    /// </summary>
    /// <param name="text">Text to embed</param>
    /// <param name="task">Task type (for Jina: retrieval.query or retrieval.passage)</param>
    /// <param name="ct">Cancellation token</param>
    public async Task<float[]> GetEmbeddingAsync(
        string text,
        EmbeddingTask task = EmbeddingTask.RetrievalQuery,
        CancellationToken ct = default)
    {
        var embeddings = await GetEmbeddingsAsync([text], task, ct);
        return embeddings.FirstOrDefault() ?? [];
    }

    /// <summary>
    /// Get embeddings for multiple texts
    /// </summary>
    /// <param name="texts">Texts to embed</param>
    /// <param name="task">Task type (for Jina: retrieval.query or retrieval.passage)</param>
    /// <param name="ct">Cancellation token</param>
    public async Task<List<float[]>> GetEmbeddingsAsync(
        IEnumerable<string> texts,
        EmbeddingTask task = EmbeddingTask.RetrievalPassage,
        CancellationToken ct = default)
    {
        var textList = texts.ToList();
        if (textList.Count == 0)
            return [];

        if (!_isConfigured)
        {
            _logger.LogDebug("[Embeddings] Skipping - API key not configured");
            return [];
        }

        return _provider switch
        {
            EmbeddingProvider.Jina => await GetEmbeddingsJinaAsync(textList, task, ct),
            EmbeddingProvider.HuggingFace => await GetEmbeddingsHuggingFaceAsync(textList, ct),
            _ => await GetEmbeddingsOpenAiAsync(textList, ct)
        };
    }

    private async Task<List<float[]>> GetEmbeddingsOpenAiAsync(List<string> textList, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/embeddings");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        var body = new
        {
            model = _model,
            input = textList,
            dimensions = _dimensions
        };

        request.Content = new StringContent(
            JsonSerializer.Serialize(body, JsonOptions),
            Encoding.UTF8,
            "application/json");

        var sw = Stopwatch.StartNew();

        using var response = await _httpClient.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        sw.Stop();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("[OpenAI] Embeddings API error {StatusCode}: {Response}", response.StatusCode, json);
            response.EnsureSuccessStatusCode();
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var result = new List<float[]>();
        var dataArray = root.GetProperty("data");

        foreach (var item in dataArray.EnumerateArray().OrderBy(x => x.GetProperty("index").GetInt32()))
        {
            var embeddingArray = item.GetProperty("embedding");
            var embedding = new float[embeddingArray.GetArrayLength()];
            var i = 0;
            foreach (var value in embeddingArray.EnumerateArray())
            {
                embedding[i++] = value.GetSingle();
            }
            result.Add(embedding);
        }

        // Log usage and track stats
        var tokens = 0;
        if (root.TryGetProperty("usage", out var usage))
        {
            tokens = usage.GetProperty("total_tokens").GetInt32();
            lock (_statsLock)
            {
                _totalTokensUsed += tokens;
                _totalRequests++;
            }
        }

        _logger.LogDebug("[OpenAI] Embeddings: {Count} texts, {Tokens} tokens, {Ms}ms",
            textList.Count, tokens, sw.ElapsedMilliseconds);

        return result;
    }

    private async Task<List<float[]>> GetEmbeddingsHuggingFaceAsync(List<string> textList, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        // HuggingFace format: {"inputs": ["text1", "text2"]}
        var body = new { inputs = textList };

        request.Content = new StringContent(
            JsonSerializer.Serialize(body, JsonOptions),
            Encoding.UTF8,
            "application/json");

        var sw = Stopwatch.StartNew();

        using var response = await _httpClient.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        sw.Stop();

        if (!response.IsSuccessStatusCode)
        {
            // HuggingFace may return model loading status
            if (response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
            {
                _logger.LogWarning("[HuggingFace] Model is loading, retry in 30s: {Response}", json);
            }
            else
            {
                _logger.LogError("[HuggingFace] Embeddings API error {StatusCode}: {Response}", response.StatusCode, json);
            }
            response.EnsureSuccessStatusCode();
        }

        // HuggingFace response format: [[0.123, -0.456, ...], [0.789, -0.012, ...]]
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var result = new List<float[]>();

        foreach (var embeddingArray in root.EnumerateArray())
        {
            var embedding = new float[embeddingArray.GetArrayLength()];
            var i = 0;
            foreach (var value in embeddingArray.EnumerateArray())
            {
                embedding[i++] = value.GetSingle();
            }
            result.Add(embedding);
        }

        // Track stats (HuggingFace doesn't return token count, estimate from chars)
        var estimatedTokens = textList.Sum(t => t.Length) / 4; // rough estimate
        lock (_statsLock)
        {
            _totalTokensUsed += estimatedTokens;
            _totalRequests++;
        }

        _logger.LogDebug("[HuggingFace] Embeddings: {Count} texts, ~{Tokens} tokens, {Ms}ms, dim={Dim}",
            textList.Count, estimatedTokens, sw.ElapsedMilliseconds, result.FirstOrDefault()?.Length ?? 0);

        return result;
    }

    private async Task<List<float[]>> GetEmbeddingsJinaAsync(
        List<string> textList,
        EmbeddingTask task,
        CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/embeddings");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        // Jina API format with task-specific adapter
        var taskString = task switch
        {
            EmbeddingTask.RetrievalQuery => "retrieval.query",
            EmbeddingTask.RetrievalPassage => "retrieval.passage",
            _ => "retrieval.passage"
        };

        var body = new
        {
            model = _model,
            task = taskString,
            dimensions = _dimensions,
            input = textList
        };

        request.Content = new StringContent(
            JsonSerializer.Serialize(body, JsonOptions),
            Encoding.UTF8,
            "application/json");

        var sw = Stopwatch.StartNew();

        using var response = await _httpClient.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        sw.Stop();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("[Jina] Embeddings API error {StatusCode}: {Response}", response.StatusCode, json);
            response.EnsureSuccessStatusCode();
        }

        // Jina response format is OpenAI-compatible
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var result = new List<float[]>();
        var dataArray = root.GetProperty("data");

        foreach (var item in dataArray.EnumerateArray().OrderBy(x => x.GetProperty("index").GetInt32()))
        {
            var embeddingArray = item.GetProperty("embedding");
            var embedding = new float[embeddingArray.GetArrayLength()];
            var i = 0;
            foreach (var value in embeddingArray.EnumerateArray())
            {
                embedding[i++] = value.GetSingle();
            }
            result.Add(embedding);
        }

        // Track usage
        var tokens = 0;
        if (root.TryGetProperty("usage", out var usage) &&
            usage.TryGetProperty("total_tokens", out var totalTokens))
        {
            tokens = totalTokens.GetInt32();
            lock (_statsLock)
            {
                _totalTokensUsed += tokens;
                _totalRequests++;
            }
        }

        _logger.LogDebug("[Jina] Embeddings: {Count} texts, {Tokens} tokens, {Ms}ms, task={Task}",
            textList.Count, tokens, sw.ElapsedMilliseconds, taskString);

        return result;
    }

    /// <summary>
    /// Get usage statistics since app start
    /// </summary>
    public EmbeddingUsageStats GetUsageStats()
    {
        lock (_statsLock)
        {
            var pricePerK = _provider switch
            {
                EmbeddingProvider.HuggingFace => HuggingFacePricePerThousandTokens,
                EmbeddingProvider.Jina => JinaPricePerThousandTokens,
                _ => OpenAiPricePerThousandTokens
            };

            return new EmbeddingUsageStats
            {
                TotalTokens = _totalTokensUsed,
                TotalRequests = _totalRequests,
                EstimatedCost = _totalTokensUsed / 1000.0 * pricePerK,
                Provider = _provider.ToString()
            };
        }
    }

    /// <summary>
    /// Reset usage statistics
    /// </summary>
    public static void ResetStats()
    {
        lock (_statsLock)
        {
            _totalTokensUsed = 0;
            _totalRequests = 0;
        }
    }
}

public class EmbeddingUsageStats
{
    public long TotalTokens { get; set; }
    public int TotalRequests { get; set; }
    public double EstimatedCost { get; set; }
    public string Provider { get; set; } = "OpenAI";

    public string ToTelegramHtml()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"<b>Embeddings ({Provider}):</b>");
        sb.AppendLine($"  Токенов: {TotalTokens:N0}");
        sb.AppendLine($"  Запросов: {TotalRequests:N0}");
        if (EstimatedCost > 0)
            sb.AppendLine($"  Потрачено: ~${EstimatedCost:F4}");
        return sb.ToString();
    }
}
