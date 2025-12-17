using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace WatchmenBot.Services;

public class OpenRouterClient
{
    private readonly HttpClient _httpClient;
    private readonly string _model;
    private readonly string _apiKey;
    private readonly string _baseUrl;
    private readonly ILogger<OpenRouterClient> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public OpenRouterClient(
        HttpClient httpClient,
        string apiKey,
        string baseUrl,
        string model,
        ILogger<OpenRouterClient> logger)
    {
        _httpClient = httpClient;
        _apiKey = apiKey;
        _baseUrl = string.IsNullOrWhiteSpace(baseUrl) ? "https://openrouter.ai/api" : baseUrl.TrimEnd('/');
        _model = string.IsNullOrWhiteSpace(model) ? "deepseek/deepseek-chat" : model;
        _logger = logger;

        _logger.LogInformation("[LLM] OpenRouter initialized: model={Model}", _model);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string relativePath)
    {
        var req = new HttpRequestMessage(method, $"{_baseUrl}{relativePath}")
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        req.Headers.Add("HTTP-Referer", "https://github.com/watchmenbot");
        req.Headers.Add("X-Title", "WatchmenBot");
        return req;
    }

    public async Task<string> ChatCompletionAsync(
        string systemPrompt,
        string userPrompt,
        double temperature = 0.6,
        CancellationToken ct = default)
    {
        using var req = CreateRequest(HttpMethod.Post, "/v1/chat/completions");

        var body = new
        {
            model = _model,
            temperature,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            }
        };

        req.Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");

        var inputChars = systemPrompt.Length + userPrompt.Length;
        _logger.LogInformation("[LLM] Requesting completion: {InputChars} chars input...", inputChars);

        var sw = Stopwatch.StartNew();
        using var resp = await _httpClient.SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        sw.Stop();

        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogError("[LLM] API error {StatusCode}: {Response}", resp.StatusCode, json);
            resp.EnsureSuccessStatusCode();
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var content = root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();

        // Log usage if available
        var promptTokens = 0;
        var completionTokens = 0;
        if (root.TryGetProperty("usage", out var usage))
        {
            promptTokens = usage.GetProperty("prompt_tokens").GetInt32();
            completionTokens = usage.GetProperty("completion_tokens").GetInt32();
        }

        _logger.LogInformation("[LLM] Completion done: {PromptTokens}+{CompletionTokens} tokens, {Ms}ms, {OutputChars} chars output",
            promptTokens, completionTokens, sw.ElapsedMilliseconds, content?.Length ?? 0);

        return content ?? string.Empty;
    }

    public async Task<string> ChatCompletionWithContextAsync(
        string systemPrompt,
        string userPrompt,
        string? ragContext = null,
        double temperature = 0.6,
        CancellationToken ct = default)
    {
        var fullUserPrompt = userPrompt;

        if (!string.IsNullOrWhiteSpace(ragContext))
        {
            fullUserPrompt = $"""
                Релевантный контекст из истории чата:
                ---
                {ragContext}
                ---

                {userPrompt}
                """;
        }

        return await ChatCompletionAsync(systemPrompt, fullUserPrompt, temperature, ct);
    }

    public async Task<(double totalCredits, double totalUsage)> GetCreditsAsync(CancellationToken ct = default)
    {
        using var req = CreateRequest(HttpMethod.Get, "/v1/credits");
        using var resp = await _httpClient.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var data = doc.RootElement.GetProperty("data");
        var credits = data.GetProperty("total_credits").GetDouble();
        var usage = data.GetProperty("total_usage").GetDouble();
        return (credits, usage);
    }
}
