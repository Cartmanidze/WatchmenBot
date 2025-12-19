using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace WatchmenBot.Services.Llm;

/// <summary>
/// Провайдер для OpenAI-совместимых API (OpenAI, OpenRouter, Together, Groq, Ollama, etc.)
/// </summary>
public class OpenAiCompatibleProvider : ILlmProvider
{
    private readonly HttpClient _httpClient;
    private readonly OpenAiProviderConfig _config;
    private readonly ILogger _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public string Name => _config.Name;
    public string Model => _config.Model;

    public OpenAiCompatibleProvider(HttpClient httpClient, OpenAiProviderConfig config, ILogger logger)
    {
        _httpClient = httpClient;
        _config = config;
        _logger = logger;
    }

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        var model = request.ModelOverride ?? _config.Model;

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{_config.BaseUrl}/chat/completions")
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };

        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.ApiKey);

        // Дополнительные заголовки для OpenRouter
        if (_config.Name.Equals("openrouter", StringComparison.OrdinalIgnoreCase))
        {
            httpRequest.Headers.Add("HTTP-Referer", "https://github.com/watchmenbot");
            httpRequest.Headers.Add("X-Title", "WatchmenBot");
        }

        var body = new Dictionary<string, object>
        {
            ["model"] = model,
            ["temperature"] = request.Temperature,
            ["messages"] = new[]
            {
                new { role = "system", content = request.SystemPrompt },
                new { role = "user", content = request.UserPrompt }
            }
        };

        if (request.MaxTokens.HasValue)
            body["max_tokens"] = request.MaxTokens.Value;

        httpRequest.Content = new StringContent(
            JsonSerializer.Serialize(body, JsonOptions),
            Encoding.UTF8,
            "application/json");

        var sw = Stopwatch.StartNew();

        _logger.LogDebug("[{Provider}] Requesting {Model}: {InputChars} chars",
            Name, model, request.SystemPrompt.Length + request.UserPrompt.Length);

        using var response = await _httpClient.SendAsync(httpRequest, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        sw.Stop();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("[{Provider}] API error {StatusCode}: {Response}", Name, response.StatusCode, json);
            response.EnsureSuccessStatusCode();
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var content = root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";

        var promptTokens = 0;
        var completionTokens = 0;
        if (root.TryGetProperty("usage", out var usage))
        {
            promptTokens = usage.TryGetProperty("prompt_tokens", out var pt) ? pt.GetInt32() : 0;
            completionTokens = usage.TryGetProperty("completion_tokens", out var ct2) ? ct2.GetInt32() : 0;
        }

        _logger.LogInformation("[{Provider}] {Model}: {PromptTokens}+{CompletionTokens} tokens, {Ms}ms",
            Name, model, promptTokens, completionTokens, sw.ElapsedMilliseconds);

        return new LlmResponse
        {
            Content = content,
            Provider = Name,
            Model = model,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            Duration = sw.Elapsed
        };
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            // Простой запрос для проверки доступности
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{_config.BaseUrl}/models");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.ApiKey);

            using var response = await _httpClient.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// Конфигурация OpenAI-совместимого провайдера
/// </summary>
public class OpenAiProviderConfig
{
    public required string Name { get; init; }
    public required string ApiKey { get; init; }
    public required string BaseUrl { get; init; }
    public required string Model { get; init; }
}
