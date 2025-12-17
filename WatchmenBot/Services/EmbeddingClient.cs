using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace WatchmenBot.Services;

public class EmbeddingClient
{
    private readonly HttpClient _httpClient;
    private readonly string _model;
    private readonly string _apiKey;
    private readonly string _baseUrl;
    private readonly int _dimensions;
    private readonly ILogger<EmbeddingClient> _logger;
    private readonly bool _isConfigured;

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
        _baseUrl = string.IsNullOrWhiteSpace(baseUrl) ? "https://api.openai.com/v1" : baseUrl.TrimEnd('/');
        _model = string.IsNullOrWhiteSpace(model) ? "text-embedding-3-small" : model;
        _dimensions = dimensions > 0 ? dimensions : 1536;
        _logger = logger;
        _isConfigured = !string.IsNullOrWhiteSpace(apiKey);
    }

    public bool IsConfigured => _isConfigured;

    public async Task<float[]> GetEmbeddingAsync(string text, CancellationToken ct = default)
    {
        var embeddings = await GetEmbeddingsAsync(new[] { text }, ct);
        return embeddings.FirstOrDefault() ?? Array.Empty<float>();
    }

    public async Task<List<float[]>> GetEmbeddingsAsync(IEnumerable<string> texts, CancellationToken ct = default)
    {
        var textList = texts.ToList();
        if (textList.Count == 0)
            return new List<float[]>();

        if (!_isConfigured)
        {
            _logger.LogDebug("[OpenAI] Skipping embeddings - API key not configured");
            return new List<float[]>();
        }

        var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/embeddings");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        var totalChars = textList.Sum(t => t.Length);
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

        // Log usage
        var tokens = 0;
        if (root.TryGetProperty("usage", out var usage))
        {
            tokens = usage.GetProperty("total_tokens").GetInt32();
        }

        _logger.LogDebug("[OpenAI] Embeddings: {Count} texts, {Tokens} tokens, {Ms}ms",
            textList.Count, tokens, sw.ElapsedMilliseconds);

        return result;
    }
}
