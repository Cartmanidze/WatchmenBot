using Dapper;
using WatchmenBot.Infrastructure.Database;

namespace WatchmenBot.Infrastructure.Settings;

public class PromptSettingsStore(IDbConnectionFactory connectionFactory, ILogger<PromptSettingsStore> logger)
{
    /// <summary>
    /// Default prompts organized by: command → mode → language
    /// Key format: "command:mode:language" (e.g., "ask:funny:ru")
    /// Fallback chain: "ask:funny:ru" → "ask:funny" → "ask" (for backwards compatibility)
    /// </summary>
    private static readonly Dictionary<string, PromptConfig> DefaultPrompts = new()
    {
        // ============================================================
        // ASK COMMAND - Question about chat history
        // ============================================================

        // ASK - FUNNY mode (current behavior with jokes and roasts)
        ["ask:funny:ru"] = new PromptConfig
        {
            Description = "Вопрос по истории чата с подъёбкой (весёлый режим)",
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
                - Имена пиши ТОЧНО как в данных (Gleb Bezrukov ≠ Глеб Безухов!)
                - Используй факты из памяти ТОЛЬКО если они релевантны вопросу

                Формат: просто текст, HTML только для <b> и <i> если нужно.
                """
        },

        // ASK - BUSINESS mode (professional, no profanity)
        ["ask:business:ru"] = new PromptConfig
        {
            Description = "Вопрос по истории чата (деловой режим)",
            LlmTag = null,
            SystemPrompt = """
                Ты — профессиональный ассистент по истории чата. Отвечаешь КОРОТКО и ТОЧНО.

                ПРАВИЛА:
                - Максимум 2-3 предложения
                - Прямой ответ на вопрос без лишней информации
                - Упоминай людей, которые связаны с темой вопроса
                - Никакого мата и грубости — только профессиональный тон
                - Можешь привести релевантную цитату если есть
                - Никакой воды, заголовков, эмодзи в начале

                ВАЖНО:
                - Поле "Спрашивает" — имя того, КТО задаёт вопрос
                - Имена пиши ТОЧНО как в данных
                - Используй факты из памяти ТОЛЬКО если они релевантны вопросу
                - Будь объективен и нейтрален

                Формат: просто текст, HTML только для <b> и <i> если нужно.
                """
        },

        // ASK - BUSINESS mode (English - future)
        ["ask:business:en"] = new PromptConfig
        {
            Description = "Chat history question (business mode)",
            LlmTag = null,
            SystemPrompt = """
                You are a professional chat history assistant. Answer BRIEFLY and ACCURATELY.

                RULES:
                - Maximum 2-3 sentences
                - Direct answer to the question without extra information
                - Mention people related to the question topic
                - No profanity — professional tone only
                - You may include a relevant quote if available
                - No fluff, headers, or leading emojis

                IMPORTANT:
                - "Asked by" field indicates WHO is asking the question
                - Write names EXACTLY as they appear in the data
                - Use memory facts ONLY if relevant to the question
                - Be objective and neutral

                Format: plain text, HTML only for <b> and <i> if needed.
                """
        },

        // ============================================================
        // SMART COMMAND - Internet search (same for all modes)
        // ============================================================

        ["smart"] = new PromptConfig
        {
            Description = "Поиск в интернете (Perplexity, без контекста чата)",
            LlmTag = "factcheck",
            SystemPrompt = """
                Ты — умный ассистент с доступом к интернету.

                ПРАВИЛА:
                - Отвечай кратко и по делу
                - Указывай источники для важных фактов
                - Честно скажи, если не уверен

                ВАЖНО — ФОРМАТИРОВАНИЕ:
                - Используй ТОЛЬКО HTML теги: <b>жирный</b>, <i>курсив</i>, <a href="url">ссылка</a>
                - НЕ используй markdown (**, __, *, _, ```, []()), это сломает отображение
                - Буллеты через •, не через - или *

                Пример ответа:
                <b>Краткий ответ</b>

                Подробности если нужны.

                • <a href="url">Источник 1</a>
                • <a href="url">Источник 2</a>
                """
        },

        // ============================================================
        // SUMMARY COMMAND - Daily chat summary
        // ============================================================

        // SUMMARY - FUNNY mode (sarcastic with humor)
        ["summary:funny:ru"] = new PromptConfig
        {
            Description = "Саммари чата за период (весёлый режим)",
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

        // SUMMARY - BUSINESS mode (professional report)
        ["summary:business:ru"] = new PromptConfig
        {
            Description = "Саммари чата за период (деловой режим)",
            LlmTag = null,
            SystemPrompt = """
                Ты — профессиональный аналитик чата. Составляешь краткий деловой отчёт.

                Твой стиль:
                - Чёткий, структурированный, по делу
                - Никакого мата и сарказма
                - Объективное изложение фактов
                - Выделение ключевых решений и договорённостей

                ПРАВИЛА ФОРМАТИРОВАНИЯ (HTML для Telegram):
                - Используй <b>жирный</b> для заголовков
                - НЕ используй * или ** или _ вообще
                - Используй имена людей (Иван, Мария), НЕ логины (@username)
                - Эмодзи в начале разделов (минимально)

                Формат ответа:

                📊 <b>Итоги дня</b>
                2-3 предложения о ключевых событиях

                📌 <b>Основные темы</b>
                • тема 1 — краткое описание
                • тема 2 — краткое описание

                ✅ <b>Решения и договорённости</b>
                • что решили (если есть)

                👥 <b>Активные участники</b>
                Кто был наиболее активен

                📝 <b>Резюме</b>
                Краткий нейтральный итог
                """
        },

        // SUMMARY - BUSINESS mode (English - future)
        ["summary:business:en"] = new PromptConfig
        {
            Description = "Chat summary (business mode)",
            LlmTag = null,
            SystemPrompt = """
                You are a professional chat analyst. Create a concise business report.

                Your style:
                - Clear, structured, to the point
                - No profanity or sarcasm
                - Objective presentation of facts
                - Highlight key decisions and agreements

                FORMATTING RULES (HTML for Telegram):
                - Use <b>bold</b> for headers
                - DO NOT use * or ** or _ at all
                - Use people's names (John, Mary), NOT usernames (@username)
                - Minimal emojis at section starts

                Response format:

                📊 <b>Day Summary</b>
                2-3 sentences about key events

                📌 <b>Main Topics</b>
                • topic 1 — brief description
                • topic 2 — brief description

                ✅ <b>Decisions & Agreements</b>
                • what was decided (if any)

                👥 <b>Active Participants</b>
                Who was most active

                📝 <b>Summary</b>
                Brief neutral conclusion
                """
        },

        // ============================================================
        // TRUTH COMMAND - Fact-checking (same for all modes)
        // ============================================================

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
        },

        // ============================================================
        // LEGACY KEYS (backwards compatibility)
        // These map to funny:ru for existing behavior
        // ============================================================

        ["ask"] = new PromptConfig
        {
            Description = "Вопрос по истории чата с подъёбкой (RAG + Grok)",
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
                - Имена пиши ТОЧНО как в данных (Gleb Bezrukov ≠ Глеб Безухов!)
                - Используй факты из памяти ТОЛЬКО если они релевантны вопросу

                Формат: просто текст, HTML только для <b> и <i> если нужно.
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
        }
    };

    /// <summary>
    /// Get prompt for command (legacy method, uses funny mode for backwards compatibility)
    /// </summary>
    public async Task<string> GetPromptAsync(string command)
    {
        var settings = await GetSettingsAsync(command);
        return settings.SystemPrompt;
    }

    /// <summary>
    /// Get settings for command (legacy method, uses funny mode for backwards compatibility)
    /// </summary>
    public async Task<PromptSettings> GetSettingsAsync(string command)
    {
        // Legacy: use funny mode for backwards compatibility
        return await GetSettingsAsync(command, ChatMode.Funny, ChatLanguage.Ru);
    }

    /// <summary>
    /// Get settings for command with specific mode and language.
    /// Fallback chain: "ask:funny:ru" → "ask:funny" → "ask"
    /// </summary>
    public async Task<PromptSettings> GetSettingsAsync(string command, ChatMode mode, ChatLanguage language)
    {
        var modeKey = mode.ToPromptKey();
        var langKey = language.ToPromptKey();

        // Try keys in order of specificity (most specific first)
        var keysToTry = new[]
        {
            $"{command}:{modeKey}:{langKey}",  // ask:funny:ru
            $"{command}:{modeKey}",             // ask:funny (fallback for missing language)
            command                              // ask (legacy fallback)
        };

        // First, try to get from database (custom prompts override defaults)
        try
        {
            using var connection = await connectionFactory.CreateConnectionAsync();

            foreach (var key in keysToTry)
            {
                var result = await connection.QuerySingleOrDefaultAsync<(string SystemPrompt, string? LlmTag)>(
                    "SELECT system_prompt, llm_tag FROM prompt_settings WHERE command = @Command",
                    new { Command = key });

                if (!string.IsNullOrEmpty(result.SystemPrompt))
                {
                    logger.LogDebug("Found custom prompt for key: {Key}", key);
                    return new PromptSettings
                    {
                        SystemPrompt = result.SystemPrompt,
                        LlmTag = result.LlmTag
                    };
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to get settings for {Command}:{Mode}:{Lang}, trying defaults",
                command, modeKey, langKey);
        }

        // Try default prompts with same fallback chain
        foreach (var key in keysToTry)
        {
            if (DefaultPrompts.TryGetValue(key, out var config))
            {
                logger.LogDebug("Using default prompt for key: {Key}", key);
                return new PromptSettings
                {
                    SystemPrompt = config.SystemPrompt,
                    LlmTag = config.LlmTag
                };
            }
        }

        logger.LogWarning("No prompt found for {Command}:{Mode}:{Lang}", command, modeKey, langKey);
        return new PromptSettings { SystemPrompt = string.Empty, LlmTag = null };
    }

    public async Task SetPromptAsync(string command, string systemPrompt)
    {
        var description = DefaultPrompts.TryGetValue(command, out var config)
            ? config.Description
            : $"Промпт для /{command}";

        try
        {
            using var connection = await connectionFactory.CreateConnectionAsync();
            await connection.ExecuteAsync(
                """
                INSERT INTO prompt_settings (command, description, system_prompt, updated_at)
                VALUES (@Command, @Description, @SystemPrompt, NOW())
                ON CONFLICT (command) DO UPDATE SET
                    system_prompt = EXCLUDED.system_prompt,
                    updated_at = NOW()
                """,
                new { Command = command, Description = description, SystemPrompt = systemPrompt });

            logger.LogInformation("Updated prompt for {Command}", command);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to set prompt for {Command}", command);
            throw;
        }
    }

    /// <summary>
    /// Set LLM tag for command
    /// </summary>
    public async Task SetLlmTagAsync(string command, string? llmTag)
    {
        var description = DefaultPrompts.TryGetValue(command, out var config)
            ? config.Description
            : $"Промпт для /{command}";

        var defaultPrompt = config?.SystemPrompt ?? "";

        try
        {
            using var connection = await connectionFactory.CreateConnectionAsync();
            await connection.ExecuteAsync(
                """
                INSERT INTO prompt_settings (command, description, system_prompt, llm_tag, updated_at)
                VALUES (@Command, @Description, @SystemPrompt, @LlmTag, NOW())
                ON CONFLICT (command) DO UPDATE SET
                    llm_tag = EXCLUDED.llm_tag,
                    updated_at = NOW()
                """,
                new { Command = command, Description = description, SystemPrompt = defaultPrompt, LlmTag = llmTag });

            logger.LogInformation("Updated LLM tag for {Command}: {Tag}", command, llmTag ?? "(null)");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to set LLM tag for {Command}", command);
            throw;
        }
    }

    public async Task ResetPromptAsync(string command)
    {
        try
        {
            using var connection = await connectionFactory.CreateConnectionAsync();
            await connection.ExecuteAsync(
                "DELETE FROM prompt_settings WHERE command = @Command",
                new { Command = command });

            logger.LogInformation("Reset prompt for {Command} to default", command);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to reset prompt for {Command}", command);
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
            using var connection = await connectionFactory.CreateConnectionAsync();
            var dbPrompts = await connection.QueryAsync<(string Command, string Description, string SystemPrompt, string? LlmTag, DateTimeOffset UpdatedAt)>(
                "SELECT command, description, system_prompt, llm_tag, updated_at FROM prompt_settings");

            foreach (var p in dbPrompts)
            {
                customPrompts[p.Command] = (p.Description, p.SystemPrompt, p.LlmTag, p.UpdatedAt);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to get custom prompts from DB");
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

    /// <summary>
    /// Get list of available modes for a command
    /// </summary>
    public static IEnumerable<ChatMode> GetAvailableModes(string command)
    {
        var modes = new HashSet<ChatMode>();

        foreach (var key in DefaultPrompts.Keys)
        {
            if (key.StartsWith($"{command}:"))
            {
                var parts = key.Split(':');
                if (parts.Length >= 2 && ChatModeExtensions.TryParse(parts[1], out var mode))
                {
                    modes.Add(mode);
                }
            }
        }

        // Always include both modes for flexibility
        modes.Add(ChatMode.Business);
        modes.Add(ChatMode.Funny);

        return modes;
    }

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
/// Full prompt settings including LLM tag
/// </summary>
public class PromptSettings
{
    public required string SystemPrompt { get; init; }
    public string? LlmTag { get; init; }
}
