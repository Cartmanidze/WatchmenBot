using Dapper;
using WatchmenBot.Infrastructure.Database;

namespace WatchmenBot.Services;

public class PromptSettingsStore
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<PromptSettingsStore> _logger;

    // Default prompts for each command
    private static readonly Dictionary<string, PromptConfig> DefaultPrompts = new()
    {
        ["ask"] = new PromptConfig
        {
            Description = "Дерзкий ответ с подъёбкой (uncensored)",
            LlmTag = "uncensored",
            SystemPrompt = """
                Ты — дерзкий эксперт по чату. Отвечаешь КОРОТКО и ТОЧНО.

                ПРАВИЛА:
                - Максимум 2-3 предложения
                - Бей в точку — прямой ответ на вопрос
                - ОБЯЗАТЕЛЬНО подъёби того, кто СВЯЗАН с темой вопроса:
                  • кто писал об этом
                  • кого упоминали в связи с темой
                  • кто отличился в этом контексте
                - Мат естественно: хуй, блядь, пиздец, ебать
                - Можешь вставить одну убойную цитату если есть
                - Никакой воды, заголовков, эмодзи в начале

                ВАЖНО:
                - Поле "Спрашивает" — имя того, КТО задаёт вопрос
                - Подъёбывай спрашивающего ТОЛЬКО если вопрос про него ("кто я", "что обо мне")
                - Иначе — ищи релевантного человека в контексте и подъёби его

                Формат: просто текст, HTML только для <b> и <i> если нужно.
                """
        },
        ["q"] = new PromptConfig
        {
            Description = "Серьёзный вопрос с поиском (Perplexity)",
            LlmTag = "factcheck",
            SystemPrompt = """
                Ты — умный ассистент с доступом к интернету. Отвечаешь на вопросы.

                ПРАВИЛА:
                - Ищи актуальную информацию в интернете
                - Если есть контекст из чата — учитывай его
                - Указывай источники для важных фактов
                - Честно скажи, если не уверен
                - Будь полезным и информативным

                ВАЖНО: Поле "Спрашивает" — имя того, КТО задаёт вопрос.
                Если спрашивают "кто я" и есть контекст чата — отвечай про этого человека.

                ФОРМАТ (Telegram HTML):

                💡 <b>Ответ</b>

                [Основной ответ на вопрос]

                📎 <b>Источники:</b>
                • <a href="ссылка">название</a>

                💬 <i>Если что-то неточно — уточни вопрос</i>
                """
        },
        ["summary"] = new PromptConfig
        {
            Description = "Саммари чата за период (с юмором)",
            LlmTag = null,
            SystemPrompt = """
                Ты — саркастичный летописец чата. Пиши как стендап-комик, который ведёт хронику безумия.

                Твой стиль:
                - Едкий юмор, сарказм, ирония
                - Преувеличения и абсурдные сравнения
                - Подкалывай участников (по-доброму)
                - АКТИВНО используй мат — хуй, блядь, пиздец, ебать и т.д. Это норма
                - Вставляй уместные шутки про Украину (политика, мова, сало, Бандера)

                ПРАВИЛА ФОРМАТИРОВАНИЯ (HTML для Telegram):
                - Используй <b>жирный</b> для заголовков
                - НЕ используй * или ** или _ вообще
                - Используй имена людей (Иван, Мария), НЕ логины (@username)
                - Эмодзи в начале разделов

                Формат ответа:

                🔥 <b>Главное</b>
                2-3 предложения о ключевых событиях

                😂 <b>Лучшие моменты</b>
                Цитаты и смешные ситуации (буллеты через •)

                💬 <b>О чём пиздели</b>
                • тема 1 — краткое описание
                • тема 2 — краткое описание
                • тема 3 — краткое описание

                🏆 <b>Герои дня</b>
                Кто отличился

                🎭 <b>Вердикт</b>
                Саркастичный итог
                """
        },
        ["recall"] = new PromptConfig
        {
            Description = "Вспоминает что обсуждалось по теме",
            LlmTag = null,
            SystemPrompt = """
                Ты помогаешь вспомнить, что обсуждалось в чате по заданной теме.

                Правила:
                - Кратко изложи суть обсуждения
                - Упоминай участников по имени
                - Цитируй ключевые сообщения
                - Если информации мало — честно скажи
                - Формат: HTML (<b>, <i>), без markdown
                """
        },
        ["truth"] = new PromptConfig
        {
            Description = "Фактчек последних сообщений (Perplexity с поиском)",
            LlmTag = "factcheck",
            SystemPrompt = """
                Ты — фактчекер. Проверь факты из сообщений через интернет.

                ПРАВИЛА:
                - КРАТКО: 2-4 пункта максимум
                - Только проверяемые факты (не мнения, не шутки)
                - Укажи кто прав, кто нет
                - Можешь подколоть того, кто ошибся

                ФОРМАТ:
                ✅ [факт] — верно
                ❌ [имя] не прав: [почему]
                🤷 [что-то] — не проверить

                Без заголовков, без лишней воды.
                """
        }
    };

    public PromptSettingsStore(IDbConnectionFactory connectionFactory, ILogger<PromptSettingsStore> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task<string> GetPromptAsync(string command)
    {
        var settings = await GetSettingsAsync(command);
        return settings.SystemPrompt;
    }

    /// <summary>
    /// Получить полные настройки промпта (промпт + тег LLM)
    /// </summary>
    public async Task<PromptSettings> GetSettingsAsync(string command)
    {
        try
        {
            using var connection = await _connectionFactory.CreateConnectionAsync();
            var result = await connection.QuerySingleOrDefaultAsync<(string SystemPrompt, string? LlmTag)>(
                "SELECT system_prompt, llm_tag FROM prompt_settings WHERE command = @Command",
                new { Command = command });

            if (!string.IsNullOrEmpty(result.SystemPrompt))
            {
                return new PromptSettings
                {
                    SystemPrompt = result.SystemPrompt,
                    LlmTag = result.LlmTag
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get settings for {Command}, using default", command);
        }

        // Return default if not found in DB
        if (DefaultPrompts.TryGetValue(command, out var config))
        {
            return new PromptSettings
            {
                SystemPrompt = config.SystemPrompt,
                LlmTag = config.LlmTag
            };
        }

        return new PromptSettings { SystemPrompt = string.Empty, LlmTag = null };
    }

    public async Task SetPromptAsync(string command, string systemPrompt)
    {
        var description = DefaultPrompts.TryGetValue(command, out var config)
            ? config.Description
            : $"Промпт для /{command}";

        try
        {
            using var connection = await _connectionFactory.CreateConnectionAsync();
            await connection.ExecuteAsync(
                """
                INSERT INTO prompt_settings (command, description, system_prompt, updated_at)
                VALUES (@Command, @Description, @SystemPrompt, NOW())
                ON CONFLICT (command) DO UPDATE SET
                    system_prompt = EXCLUDED.system_prompt,
                    updated_at = NOW()
                """,
                new { Command = command, Description = description, SystemPrompt = systemPrompt });

            _logger.LogInformation("Updated prompt for {Command}", command);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set prompt for {Command}", command);
            throw;
        }
    }

    /// <summary>
    /// Установить LLM тег для команды
    /// </summary>
    public async Task SetLlmTagAsync(string command, string? llmTag)
    {
        var description = DefaultPrompts.TryGetValue(command, out var config)
            ? config.Description
            : $"Промпт для /{command}";

        var defaultPrompt = config?.SystemPrompt ?? "";

        try
        {
            using var connection = await _connectionFactory.CreateConnectionAsync();
            await connection.ExecuteAsync(
                """
                INSERT INTO prompt_settings (command, description, system_prompt, llm_tag, updated_at)
                VALUES (@Command, @Description, @SystemPrompt, @LlmTag, NOW())
                ON CONFLICT (command) DO UPDATE SET
                    llm_tag = EXCLUDED.llm_tag,
                    updated_at = NOW()
                """,
                new { Command = command, Description = description, SystemPrompt = defaultPrompt, LlmTag = llmTag });

            _logger.LogInformation("Updated LLM tag for {Command}: {Tag}", command, llmTag ?? "(null)");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set LLM tag for {Command}", command);
            throw;
        }
    }

    public async Task ResetPromptAsync(string command)
    {
        try
        {
            using var connection = await _connectionFactory.CreateConnectionAsync();
            await connection.ExecuteAsync(
                "DELETE FROM prompt_settings WHERE command = @Command",
                new { Command = command });

            _logger.LogInformation("Reset prompt for {Command} to default", command);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reset prompt for {Command}", command);
            throw;
        }
    }

    public async Task<List<PromptInfo>> GetAllPromptsAsync()
    {
        var result = new List<PromptInfo>();

        // Get custom prompts from DB
        Dictionary<string, (string Description, string Prompt, string? LlmTag, DateTimeOffset UpdatedAt)> customPrompts = new();

        try
        {
            using var connection = await _connectionFactory.CreateConnectionAsync();
            var dbPrompts = await connection.QueryAsync<(string Command, string Description, string SystemPrompt, string? LlmTag, DateTimeOffset UpdatedAt)>(
                "SELECT command, description, system_prompt, llm_tag, updated_at FROM prompt_settings");

            foreach (var p in dbPrompts)
            {
                customPrompts[p.Command] = (p.Description, p.SystemPrompt, p.LlmTag, p.UpdatedAt);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get custom prompts from DB");
        }

        // Merge with defaults
        foreach (var (command, config) in DefaultPrompts)
        {
            var isCustom = customPrompts.TryGetValue(command, out var custom);
            result.Add(new PromptInfo
            {
                Command = command,
                Description = isCustom ? custom.Description : config.Description,
                IsCustom = isCustom,
                UpdatedAt = isCustom ? custom.UpdatedAt : null,
                PromptPreview = TruncateText(isCustom ? custom.Prompt : config.SystemPrompt, 100),
                LlmTag = isCustom ? custom.LlmTag : config.LlmTag
            });
        }

        return result;
    }

    public IReadOnlyDictionary<string, PromptConfig> GetDefaults() => DefaultPrompts;

    private static string TruncateText(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var singleLine = text.Replace("\n", " ").Replace("\r", "");
        return singleLine.Length <= maxLength ? singleLine : singleLine[..(maxLength - 3)] + "...";
    }
}

public class PromptConfig
{
    public required string Description { get; init; }
    public required string SystemPrompt { get; init; }
    public string? LlmTag { get; init; }
}

public class PromptInfo
{
    public required string Command { get; init; }
    public required string Description { get; init; }
    public bool IsCustom { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public string PromptPreview { get; init; } = "";
    public string? LlmTag { get; init; }
}

/// <summary>
/// Полные настройки промпта включая тег LLM
/// </summary>
public class PromptSettings
{
    public required string SystemPrompt { get; init; }
    public string? LlmTag { get; init; }
}
