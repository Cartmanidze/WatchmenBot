using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace WatchmenBot.Features.Search.Services;


public class EmbeddingClient
{
    private readonly HttpClient _httpClient;
    private readonly string _model;
    private readonly string _apiKey;
    private readonly string _baseUrl;
    private readonly int _dimensions;
    private readonly ILogger<EmbeddingClient> _logger;
    private readonly bool _isConfigured;

    // Usage tracking (static to persist across scopes)
    private static long _totalTokensUsed;
    private static int _totalRequests;
    private static readonly object _statsLock = new();

    private const double PricePerThousandTokens = 0.00002; // text-embedding-3-small

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
        ILogger<EmbeddingClient> logger)
    {
        _httpClient = httpClient;
        _apiKey = apiKey;
        _logger = logger;
        _isConfigured = !string.IsNullOrWhiteSpace(apiKey);

        _baseUrl = string.IsNullOrWhiteSpace(baseUrl)
            ? "https://api.openai.com/v1"
            : baseUrl.TrimEnd('/');
        _model = string.IsNullOrWhiteSpace(model) ? "text-embedding-3-small" : model;
        _dimensions = dimensions > 0 ? dimensions : 1536;

        // Debug level to avoid log spam (EmbeddingClient is scoped, created per request)
        _logger.LogDebug("Embeddings configured: BaseUrl={BaseUrl}, Dimensions={Dimensions}",
            _baseUrl, _dimensions);
    }

    public bool IsConfigured => _isConfigured;
    public int Dimensions => _dimensions;

    /// <summary>
    /// Get embedding for a single text
    /// </summary>
    public async Task<float[]> GetEmbeddingAsync(string text, CancellationToken ct = default)
    {
        var embeddings = await GetEmbeddingsAsync([text], ct);
        return embeddings.FirstOrDefault() ?? [];
    }

    /// <summary>
    /// Get embeddings for multiple texts
    /// </summary>
    public async Task<List<float[]>> GetEmbeddingsAsync(
        IEnumerable<string> texts,
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

        return await GetEmbeddingsOpenAiAsync(textList, ct);
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

    /// <summary>
    /// Get usage statistics since app start
    /// </summary>
    public EmbeddingUsageStats GetUsageStats()
    {
        lock (_statsLock)
        {
            return new EmbeddingUsageStats
            {
                TotalTokens = _totalTokensUsed,
                TotalRequests = _totalRequests,
                EstimatedCost = _totalTokensUsed / 1000.0 * PricePerThousandTokens,
                Provider = "OpenAI"
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
