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
            Description = "Отвечает на вопросы про участников чата",
            SystemPrompt = """
                Ты — ОЧЕНЬ остроумный и саркастичный летописец чата. Твои ответы должны быть уровня стендап-комика.

                Твой стиль:
                - Будь ОСТРОУМНЫМ — не просто смешным, а с умными подколами и неожиданными поворотами
                - Используй иронию, сарказм, игру слов, двусмысленности
                - Мат органично вплетай в речь — хуй, блядь, пиздец, ебать, нахуй
                - Делай неожиданные сравнения и метафоры (чем абсурднее, тем лучше)
                - Подмечай противоречия в поведении людей
                - Цитируй самые идиотские или гениальные высказывания
                - Упоминай людей по имени, создавай им "образы" и "титулы"
                - Если информации мало — выкрути это в шутку

                ФОРМАТ (HTML):
                🎭 <b>Остроумный заголовок-панчлайн</b>

                Основной текст — живой, с подколами, как будто рассказываешь историю в баре.

                💬 <i>«убойная цитата»</i> — комментарий

                Пиши так, чтобы человек заржал. НЕ используй markdown (* _ **).
                """
        },
        ["summary"] = new PromptConfig
        {
            Description = "Генерирует саммари чата за период",
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
            Description = "Вспоминает контекст по теме",
            SystemPrompt = """
                Ты помогаешь вспомнить, что обсуждалось в чате по заданной теме.

                Правила:
                - Кратко изложи суть обсуждения
                - Упоминай участников по имени
                - Цитируй ключевые сообщения
                - Если информации мало — честно скажи
                - Формат: HTML (<b>, <i>), без markdown
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
        try
        {
            using var connection = await _connectionFactory.CreateConnectionAsync();
            var result = await connection.QuerySingleOrDefaultAsync<string>(
                "SELECT system_prompt FROM prompt_settings WHERE command = @Command",
                new { Command = command });

            if (!string.IsNullOrEmpty(result))
                return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get prompt for {Command}, using default", command);
        }

        // Return default if not found in DB
        return DefaultPrompts.TryGetValue(command, out var config) ? config.SystemPrompt : string.Empty;
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
        Dictionary<string, (string Description, string Prompt, DateTimeOffset UpdatedAt)> customPrompts = new();

        try
        {
            using var connection = await _connectionFactory.CreateConnectionAsync();
            var dbPrompts = await connection.QueryAsync<(string Command, string Description, string SystemPrompt, DateTimeOffset UpdatedAt)>(
                "SELECT command, description, system_prompt, updated_at FROM prompt_settings");

            foreach (var p in dbPrompts)
            {
                customPrompts[p.Command] = (p.Description, p.SystemPrompt, p.UpdatedAt);
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
                PromptPreview = TruncateText(isCustom ? custom.Prompt : config.SystemPrompt, 100)
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
}

public class PromptInfo
{
    public required string Command { get; init; }
    public required string Description { get; init; }
    public bool IsCustom { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public string PromptPreview { get; init; } = "";
}
