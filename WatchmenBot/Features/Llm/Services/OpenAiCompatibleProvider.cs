using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace WatchmenBot.Features.Llm.Services;

/// <summary>
/// Провайдер для OpenAI-совместимых API (OpenAI, OpenRouter, Together, Groq, Ollama, etc.)
/// </summary>
public class OpenAiCompatibleProvider(HttpClient httpClient, OpenAiProviderConfig config, ILogger logger)
    : ILlmProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public string Name => config.Name;
    public string Model => config.Model;

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        var model = request.ModelOverride ?? config.Model;

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{config.BaseUrl}/chat/completions");
        httpRequest.Version = HttpVersion.Version11;
        httpRequest.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;

        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);

        // Дополнительные заголовки для OpenRouter
        if (config.Name.Equals("openrouter", StringComparison.OrdinalIgnoreCase))
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

        logger.LogDebug("[{Provider}] Requesting {Model}: {InputChars} chars",
            Name, model, request.SystemPrompt.Length + request.UserPrompt.Length);

        using var response = await httpClient.SendAsync(httpRequest, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        sw.Stop();

        if (!response.IsSuccessStatusCode)
        {
            logger.LogError("[{Provider}] API error {StatusCode}: {Response}", Name, response.StatusCode, json);
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

        logger.LogInformation("[{Provider}] {Model}: {PromptTokens}+{CompletionTokens} tokens, {Ms}ms",
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
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{config.BaseUrl}/models");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.ApiKey);

            using var response = await httpClient.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[LlmProvider] Health check failed for {Provider}", config.Name);
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
