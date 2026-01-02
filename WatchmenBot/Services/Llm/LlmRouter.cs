namespace WatchmenBot.Services.Llm;

/// <summary>
/// Роутер для LLM запросов — выбирает провайдера по тегам, fallback, комбинация
/// </summary>
public class LlmRouter(ILogger<LlmRouter> logger)
{
    private readonly Dictionary<string, ILlmProvider> _providers = new();
    private readonly Dictionary<string, LlmProviderOptions> _options = new();
    private string _defaultProviderName = "";

    /// <summary>
    /// Зарегистрировать провайдера
    /// </summary>
    public void Register(ILlmProvider provider, LlmProviderOptions options)
    {
        _providers[options.Name] = provider;
        _options[options.Name] = options;

        if (string.IsNullOrEmpty(_defaultProviderName) || options.Priority < (_options.GetValueOrDefault(_defaultProviderName)?.Priority ?? int.MaxValue))
        {
            _defaultProviderName = options.Name;
        }

        logger.LogInformation("[LlmRouter] Registered provider '{Name}' ({Model}), priority={Priority}, tags=[{Tags}]",
            options.Name, options.Model, options.Priority, string.Join(", ", options.Tags));
    }

    /// <summary>
    /// Получить провайдера по имени
    /// </summary>
    public ILlmProvider? GetProvider(string name)
    {
        return _providers.GetValueOrDefault(name);
    }

    /// <summary>
    /// Получить провайдера по тегу (e.g. "uncensored", "fast")
    /// </summary>
    public ILlmProvider? GetProviderByTag(string tag)
    {
        var match = _options
            .Where(kv => kv.Value.Enabled && kv.Value.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            .OrderBy(kv => kv.Value.Priority)
            .FirstOrDefault();

        return match.Key != null ? _providers.GetValueOrDefault(match.Key) : null;
    }

    /// <summary>
    /// Получить дефолтного провайдера
    /// </summary>
    public ILlmProvider GetDefault()
    {
        if (string.IsNullOrEmpty(_defaultProviderName) || !_providers.TryGetValue(_defaultProviderName, out var provider))
        {
            throw new InvalidOperationException("No LLM providers registered");
        }
        return provider;
    }

    /// <summary>
    /// Имя дефолтного провайдера
    /// </summary>
    public string DefaultProviderName => _defaultProviderName;

    /// <summary>
    /// Установить дефолтного провайдера по имени
    /// </summary>
    public bool SetDefaultProvider(string name)
    {
        if (!_providers.ContainsKey(name))
        {
            logger.LogWarning("[LlmRouter] Cannot set default: provider '{Name}' not found", name);
            return false;
        }

        var oldDefault = _defaultProviderName;
        _defaultProviderName = name;

        logger.LogInformation("[LlmRouter] Default provider changed: {Old} -> {New}", oldDefault, name);
        return true;
    }

    /// <summary>
    /// Включить/выключить провайдера
    /// </summary>
    public bool SetProviderEnabled(string name, bool enabled)
    {
        if (!_options.TryGetValue(name, out var options))
        {
            return false;
        }

        // Создаём новый объект с изменённым Enabled (record-like update)
        _options[name] = new LlmProviderOptions
        {
            Name = options.Name,
            Type = options.Type,
            ApiKey = options.ApiKey,
            BaseUrl = options.BaseUrl,
            Model = options.Model,
            Enabled = enabled,
            Priority = options.Priority,
            Tags = options.Tags
        };

        logger.LogInformation("[LlmRouter] Provider '{Name}' {Action}", name, enabled ? "enabled" : "disabled");
        return true;
    }

    /// <summary>
    /// Выполнить запрос с fallback на другие провайдеры при ошибке
    /// </summary>
    public async Task<LlmResponse> CompleteWithFallbackAsync(
        LlmRequest request,
        string? preferredProvider = null,
        string? preferredTag = null,
        CancellationToken ct = default)
    {
        var providers = GetProvidersInOrder(preferredProvider, preferredTag);

        Exception? lastException = null;

        foreach (var provider in providers)
        {
            try
            {
                return await provider.CompleteAsync(request, ct);
            }
            catch (Exception ex)
            {
                lastException = ex;
                logger.LogWarning(ex, "[LlmRouter] Provider '{Provider}' failed, trying next...", provider.Name);
            }
        }

        throw new InvalidOperationException("All LLM providers failed", lastException);
    }

    /// <summary>
    /// Упрощённый метод — просто выполнить запрос на дефолтном провайдере
    /// </summary>
    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        return GetDefault().CompleteAsync(request, ct);
    }

    /// <summary>
    /// Упрощённый метод как у старого OpenRouterClient
    /// </summary>
    public async Task<string> ChatCompletionAsync(
        string systemPrompt,
        string userPrompt,
        double temperature = 0.7,
        CancellationToken ct = default)
    {
        var response = await CompleteAsync(new LlmRequest
        {
            SystemPrompt = systemPrompt,
            UserPrompt = userPrompt,
            Temperature = temperature
        }, ct);

        return response.Content;
    }

    /// <summary>
    /// Список всех провайдеров
    /// </summary>
    public IReadOnlyDictionary<string, LlmProviderOptions> GetAllProviders() => _options;

    private IEnumerable<ILlmProvider> GetProvidersInOrder(string? preferredProvider, string? preferredTag)
    {
        // Сначала предпочитаемый по имени
        if (!string.IsNullOrEmpty(preferredProvider) && _providers.TryGetValue(preferredProvider, out var preferred))
        {
            yield return preferred;
        }

        // Потом по тегу
        if (!string.IsNullOrEmpty(preferredTag))
        {
            var byTag = GetProviderByTag(preferredTag);
            if (byTag != null && byTag.Name != preferredProvider)
            {
                yield return byTag;
            }
        }

        // Остальные по приоритету
        foreach (var kv in _options.Where(kv => kv.Value.Enabled).OrderBy(kv => kv.Value.Priority))
        {
            if (kv.Key != preferredProvider && _providers.TryGetValue(kv.Key, out var provider))
            {
                yield return provider;
            }
        }
    }
}
