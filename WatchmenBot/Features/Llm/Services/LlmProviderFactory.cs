namespace WatchmenBot.Features.Llm.Services;

/// <summary>
/// Фабрика для создания LLM провайдеров
/// </summary>
public class LlmProviderFactory(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory)
{
    public ILlmProvider CreateOpenAiCompatible(OpenAiProviderConfig config)
    {
        var httpClient = httpClientFactory.CreateClient($"llm-{config.Name}");
        var logger = loggerFactory.CreateLogger<OpenAiCompatibleProvider>();
        return new OpenAiCompatibleProvider(httpClient, config, logger);
    }

    /// <summary>
    /// Создать провайдер из конфигурации
    /// </summary>
    public ILlmProvider Create(LlmProviderOptions options)
    {
        return options.Type.ToLowerInvariant() switch
        {
            "openai" or "openrouter" or "together" or "groq" or "ollama" =>
                CreateOpenAiCompatible(new OpenAiProviderConfig
                {
                    Name = options.Name,
                    ApiKey = options.ApiKey,
                    BaseUrl = options.BaseUrl,
                    Model = options.Model
                }),
            _ => throw new ArgumentException($"Unknown provider type: {options.Type}")
        };
    }
}

/// <summary>
/// Опции провайдера из конфигурации
/// </summary>
public class LlmProviderOptions
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public required string ApiKey { get; init; }
    public required string BaseUrl { get; init; }
    public required string Model { get; init; }
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Приоритет (меньше = выше приоритет)
    /// </summary>
    public int Priority { get; init; } = 100;

    /// <summary>
    /// Теги для роутинга (e.g. "uncensored", "fast", "cheap")
    /// </summary>
    public string[] Tags { get; init; } = [];
}
