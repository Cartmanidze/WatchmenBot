using WatchmenBot.Services.Llm;

namespace WatchmenBot.Services;

/// <summary>
/// Обёртка для обратной совместимости — делегирует вызовы в LlmRouter
/// </summary>
public class OpenRouterClient(LlmRouter router, ILogger<OpenRouterClient> logger)
{
    public async Task<string> ChatCompletionAsync(
        string systemPrompt,
        string userPrompt,
        double temperature = 0.7,
        CancellationToken ct = default)
    {
        var response = await router.CompleteAsync(new LlmRequest
        {
            SystemPrompt = systemPrompt,
            UserPrompt = userPrompt,
            Temperature = temperature
        }, ct);

        return response.Content;
    }

    public async Task<string> ChatCompletionWithContextAsync(
        string systemPrompt,
        string userPrompt,
        string? ragContext = null,
        double temperature = 0.7,
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

    public Task<(double totalCredits, double totalUsage)> GetCreditsAsync(CancellationToken ct = default)
    {
        // Credits endpoint is OpenRouter-specific, keep legacy implementation
        // This will only work if we have an OpenRouter provider configured
        logger.LogWarning("GetCreditsAsync is deprecated - use provider-specific API");
        return Task.FromResult<(double, double)>((0, 0));
    }
}
