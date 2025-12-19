namespace WatchmenBot.Services.Llm;

/// <summary>
/// Абстракция над LLM провайдером (OpenRouter, OpenAI, Ollama, etc.)
/// </summary>
public interface ILlmProvider
{
    /// <summary>
    /// Уникальное имя провайдера (e.g. "openrouter", "openai", "ollama")
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Текущая модель
    /// </summary>
    string Model { get; }

    /// <summary>
    /// Выполнить chat completion
    /// </summary>
    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default);

    /// <summary>
    /// Проверить доступность провайдера
    /// </summary>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);
}

/// <summary>
/// Запрос к LLM
/// </summary>
public class LlmRequest
{
    public required string SystemPrompt { get; init; }
    public required string UserPrompt { get; init; }
    public double Temperature { get; init; } = 0.7;
    public int? MaxTokens { get; init; }

    /// <summary>
    /// Переопределить модель для этого запроса (опционально)
    /// </summary>
    public string? ModelOverride { get; init; }
}

/// <summary>
/// Ответ от LLM
/// </summary>
public class LlmResponse
{
    public required string Content { get; init; }
    public string Provider { get; init; } = "";
    public string Model { get; init; } = "";
    public int PromptTokens { get; init; }
    public int CompletionTokens { get; init; }
    public int TotalTokens => PromptTokens + CompletionTokens;
    public TimeSpan Duration { get; init; }

    public static LlmResponse Empty => new() { Content = "" };
}
