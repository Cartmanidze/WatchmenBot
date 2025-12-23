using System.IO.Compression;
using System.Text;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using WatchmenBot.Services;
using WatchmenBot.Services.Llm;

namespace WatchmenBot.Features.Admin;

public class AdminCommandHandler
{
    private readonly ITelegramBotClient _bot;
    private readonly AdminSettingsStore _settings;
    private readonly LogCollector _logCollector;
    private readonly DailyLogReportService _reportService;
    private readonly ChatImportService _importService;
    private readonly MessageStore _messageStore;
    private readonly EmbeddingService _embeddingService;
    private readonly TelegramExportParser _exportParser;
    private readonly PromptSettingsStore _promptSettings;
    private readonly LlmRouter _llmRouter;
    private readonly ILogger<AdminCommandHandler> _logger;

    public AdminCommandHandler(
        ITelegramBotClient bot,
        AdminSettingsStore settings,
        LogCollector logCollector,
        DailyLogReportService reportService,
        ChatImportService importService,
        MessageStore messageStore,
        EmbeddingService embeddingService,
        TelegramExportParser exportParser,
        PromptSettingsStore promptSettings,
        LlmRouter llmRouter,
        ILogger<AdminCommandHandler> logger)
    {
        _bot = bot;
        _settings = settings;
        _logCollector = logCollector;
        _reportService = reportService;
        _importService = importService;
        _messageStore = messageStore;
        _embeddingService = embeddingService;
        _exportParser = exportParser;
        _promptSettings = promptSettings;
        _llmRouter = llmRouter;
        _logger = logger;
    }

    public async Task<bool> HandleAsync(Message message, CancellationToken ct)
    {
        var text = message.Text?.Trim() ?? message.Caption?.Trim() ?? "";
        var userId = message.From?.Id ?? 0;
        var username = message.From?.Username;

        // Check admin access
        if (!_settings.IsAdmin(userId, username))
        {
            _logger.LogWarning("[Admin] Unauthorized access attempt from {UserId} (@{Username})", userId, username);
            return false;
        }

        // Handle file upload for import
        if (message.Document != null && text.StartsWith("/admin import", StringComparison.OrdinalIgnoreCase))
        {
            return await HandleImportFileAsync(message, ct);
        }

        // Handle file upload for prompt (TXT file)
        if (message.Document != null && text.StartsWith("/admin prompt", StringComparison.OrdinalIgnoreCase))
        {
            return await HandlePromptFileAsync(message, ct);
        }

        // Parse command
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            await SendHelpAsync(message.Chat.Id, ct);
            return true;
        }

        var subCommand = parts[1].ToLowerInvariant();

        try
        {
            return subCommand switch
            {
                "status" => await HandleStatusAsync(message.Chat.Id, ct),
                "report" => await HandleReportAsync(message.Chat.Id, ct),
                "chats" => await HandleChatsAsync(message.Chat.Id, ct),
                "debug" when parts.Length >= 3 => await HandleDebugAsync(message.Chat.Id, parts[2], ct),
                "debug" => await HandleDebugStatusAsync(message.Chat.Id, ct),
                "import" when parts.Length >= 3 => await HandleImportCommandAsync(message.Chat.Id, parts[2], ct),
                "prompts" => await HandlePromptsListAsync(message.Chat.Id, ct),
                "prompt" when parts.Length >= 3 => await HandlePromptShowAsync(message.Chat.Id, parts[2], ct),
                "prompt_reset" when parts.Length >= 3 => await HandlePromptResetAsync(message.Chat.Id, parts[2], ct),
                "set_summary_time" when parts.Length >= 3 => await HandleSetSummaryTimeAsync(message.Chat.Id, parts[2], ct),
                "set_report_time" when parts.Length >= 3 => await HandleSetReportTimeAsync(message.Chat.Id, parts[2], ct),
                "set_timezone" when parts.Length >= 3 => await HandleSetTimezoneAsync(message.Chat.Id, parts[2], ct),
                "llm" => await HandleLlmListAsync(message.Chat.Id, ct),
                "llm_test" when parts.Length >= 3 => await HandleLlmTestAsync(message.Chat.Id, parts[2], ct),
                "llm_test" => await HandleLlmTestAsync(message.Chat.Id, null, ct),
                "llm_set" when parts.Length >= 3 => await HandleLlmSetAsync(message.Chat.Id, parts[2], ct),
                "llm_on" when parts.Length >= 3 => await HandleLlmToggleAsync(message.Chat.Id, parts[2], true, ct),
                "llm_off" when parts.Length >= 3 => await HandleLlmToggleAsync(message.Chat.Id, parts[2], false, ct),
                "prompt_tag" when parts.Length >= 4 => await HandlePromptTagAsync(message.Chat.Id, parts[2], parts[3], ct),
                "prompt_tag" when parts.Length >= 3 => await HandlePromptTagAsync(message.Chat.Id, parts[2], null, ct),
                "names" when parts.Length >= 3 => await HandleNamesAsync(message.Chat.Id, parts[2], ct),
                "rename" => await HandleRenameAsync(message.Chat.Id, text, ct),
                "reindex" when parts.Length >= 4 && parts[3] == "confirm" => await HandleReindexConfirmAsync(message.Chat.Id, parts[2], ct),
                "reindex" when parts.Length >= 3 => await HandleReindexAsync(message.Chat.Id, parts[2], ct),
                "reindex" => await HandleReindexAllAsync(message.Chat.Id, ct),
                "help" => await SendHelpAsync(message.Chat.Id, ct),
                _ => await SendHelpAsync(message.Chat.Id, ct)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Admin] Error handling command: {Command}", text);
            _logCollector.LogError("AdminCommand", $"Error: {text}", ex);

            await _bot.SendMessage(
                chatId: message.Chat.Id,
                text: $"❌ Ошибка: {ex.Message}",
                cancellationToken: ct);
            return true;
        }
    }

    private async Task<bool> HandleChatsAsync(long chatId, CancellationToken ct)
    {
        var chats = await _messageStore.GetKnownChatsAsync();

        if (chats.Count == 0)
        {
            await _bot.SendMessage(
                chatId: chatId,
                text: "📭 Нет сохранённых чатов",
                cancellationToken: ct);
            return true;
        }

        var sb = new StringBuilder();
        sb.AppendLine("<b>📋 Известные чаты</b>\n");

        foreach (var chat in chats)
        {
            var title = !string.IsNullOrWhiteSpace(chat.Title) ? chat.Title : "(без названия)";
            sb.AppendLine($"<b>{title}</b>");
            sb.AppendLine($"   🆔 <code>{chat.ChatId}</code>");
            sb.AppendLine($"   📨 {chat.MessageCount} сообщений");
            sb.AppendLine($"   📅 {chat.FirstMessage:dd.MM.yyyy} — {chat.LastMessage:dd.MM.yyyy}");
            sb.AppendLine();
        }

        sb.AppendLine("💡 Для импорта используй Chat ID из списка выше.");

        await _bot.SendMessage(
            chatId: chatId,
            text: sb.ToString(),
            parseMode: ParseMode.Html,
            cancellationToken: ct);

        return true;
    }

    private async Task<bool> HandleImportCommandAsync(long chatId, string chatIdStr, CancellationToken ct)
    {
        await _bot.SendMessage(
            chatId: chatId,
            text: $"""
                📦 <b>Импорт истории чата</b>

                Отправь ZIP-архив экспорта из Telegram Desktop с caption:
                <code>/admin import {chatIdStr}</code>

                <b>Как экспортировать:</b>
                1. Telegram Desktop → Чат → ⋮ → Export chat history
                2. Выбрать формат: HTML
                3. Запаковать папку в ZIP

                ⚠️ Лимит файла: 20 МБ
                """,
            parseMode: ParseMode.Html,
            cancellationToken: ct);
        return true;
    }

    private async Task<bool> HandleImportFileAsync(Message message, CancellationToken ct)
    {
        var chatId = message.Chat.Id;
        var caption = message.Caption ?? "";

        // Parse chat ID from caption: /admin import -1001234567890
        var parts = caption.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || !long.TryParse(parts[2], out var targetChatId))
        {
            await _bot.SendMessage(
                chatId: chatId,
                text: "❌ Укажи Chat ID в caption: <code>/admin import -1001234567890</code>",
                parseMode: ParseMode.Html,
                cancellationToken: ct);
            return true;
        }

        var document = message.Document!;

        // Validate file
        if (!document.FileName?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ?? true)
        {
            await _bot.SendMessage(
                chatId: chatId,
                text: "❌ Файл должен быть ZIP-архивом",
                cancellationToken: ct);
            return true;
        }

        if (document.FileSize > 20 * 1024 * 1024)
        {
            await _bot.SendMessage(
                chatId: chatId,
                text: "❌ Файл слишком большой (лимит 20 МБ)",
                cancellationToken: ct);
            return true;
        }

        var statusMsg = await _bot.SendMessage(
            chatId: chatId,
            text: "⏳ Скачиваю файл...",
            cancellationToken: ct);

        try
        {
            // Create temp directory
            var tempDir = Path.Combine(Path.GetTempPath(), $"tg_import_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            var zipPath = Path.Combine(tempDir, "export.zip");
            var extractPath = Path.Combine(tempDir, "extracted");

            try
            {
                // Download file
                var file = await _bot.GetFile(document.FileId, ct);
                await using (var fileStream = System.IO.File.Create(zipPath))
                {
                    await _bot.DownloadFile(file.FilePath!, fileStream, ct);
                }

                await _bot.EditMessageText(
                    chatId: chatId,
                    messageId: statusMsg.MessageId,
                    text: "⏳ Распаковываю архив...",
                    cancellationToken: ct);

                // Extract ZIP
                ZipFile.ExtractToDirectory(zipPath, extractPath);

                // Find messages*.html files (might be in subdirectory)
                var exportDir = FindExportDirectory(extractPath);
                if (exportDir == null)
                {
                    await _bot.EditMessageText(
                        chatId: chatId,
                        messageId: statusMsg.MessageId,
                        text: "❌ В архиве не найдены файлы messages*.html",
                        cancellationToken: ct);
                    return true;
                }

                // Get chat title from export
                var exportChatTitle = _exportParser.GetChatTitleFromExport(exportDir);
                var chatTitleInfo = !string.IsNullOrEmpty(exportChatTitle)
                    ? $"\n📝 Чат из экспорта: <b>{exportChatTitle}</b>"
                    : "";

                await _bot.EditMessageText(
                    chatId: chatId,
                    messageId: statusMsg.MessageId,
                    text: $"⏳ Импортирую сообщения...{chatTitleInfo}",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct);

                // Import
                var result = await _importService.ImportFromDirectoryAsync(exportDir, targetChatId, true, ct);

                if (result.IsSuccess)
                {
                    await _bot.EditMessageText(
                        chatId: chatId,
                        messageId: statusMsg.MessageId,
                        text: $"""
                            ✅ <b>Импорт завершён!</b>

                            📊 <b>Результат:</b>
                            • Распознано: {result.TotalParsed}
                            • Импортировано: {result.Imported}
                            • Пропущено (дубли): {result.SkippedExisting}

                            💡 Эмбеддинги создадутся автоматически в фоне.
                            """,
                        parseMode: ParseMode.Html,
                        cancellationToken: ct);
                }
                else
                {
                    await _bot.EditMessageText(
                        chatId: chatId,
                        messageId: statusMsg.MessageId,
                        text: $"❌ Ошибка импорта: {result.ErrorMessage}",
                        cancellationToken: ct);
                }
            }
            finally
            {
                // Cleanup temp files
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Admin] Import failed");

            await _bot.EditMessageText(
                chatId: chatId,
                messageId: statusMsg.MessageId,
                text: $"❌ Ошибка: {ex.Message}",
                cancellationToken: ct);
        }

        return true;
    }

    private string? FindExportDirectory(string basePath)
    {
        // Check if messages*.html exists in base path
        if (Directory.GetFiles(basePath, "messages*.html").Length > 0)
            return basePath;

        // Check subdirectories
        foreach (var dir in Directory.GetDirectories(basePath))
        {
            if (Directory.GetFiles(dir, "messages*.html").Length > 0)
                return dir;
        }

        return null;
    }

    private async Task<bool> HandleStatusAsync(long chatId, CancellationToken ct)
    {
        var settings = await _settings.GetAllSettingsAsync();
        var tz = await _settings.GetTimezoneOffsetAsync();
        var debugMode = await _settings.IsDebugModeEnabledAsync();

        var sb = new StringBuilder();
        sb.AppendLine("<b>⚙️ Текущие настройки</b>");
        sb.AppendLine();
        sb.AppendLine($"🕐 <b>Время саммари:</b> {settings["summary_time"]}");
        sb.AppendLine($"📋 <b>Время отчёта:</b> {settings["report_time"]}");
        sb.AppendLine($"🌍 <b>Часовой пояс:</b> UTC+{tz:hh\\:mm}");
        sb.AppendLine($"🔍 <b>Debug mode:</b> {(debugMode ? "✅ ON" : "❌ OFF")}");
        sb.AppendLine();
        sb.AppendLine($"👤 <b>Admin ID:</b> {_settings.GetAdminUserId()}");

        await _bot.SendMessage(
            chatId: chatId,
            text: sb.ToString(),
            parseMode: ParseMode.Html,
            cancellationToken: ct);

        return true;
    }

    private async Task<bool> HandleDebugAsync(long chatId, string mode, CancellationToken ct)
    {
        var enable = mode.ToLowerInvariant() switch
        {
            "on" or "1" or "true" or "enable" => true,
            "off" or "0" or "false" or "disable" => false,
            _ => (bool?)null
        };

        if (enable == null)
        {
            await _bot.SendMessage(
                chatId: chatId,
                text: "❌ Используй: <code>/admin debug on</code> или <code>/admin debug off</code>",
                parseMode: ParseMode.Html,
                cancellationToken: ct);
            return true;
        }

        await _settings.SetDebugModeAsync(enable.Value);

        var status = enable.Value ? "✅ включён" : "❌ выключен";
        var info = enable.Value
            ? "\n\n📊 Теперь при каждом /ask, /q, /summary, /truth ты будешь получать отчёт:\n• Результаты поиска (score, текст)\n• Контекст для LLM\n• Промпты (system + user)\n• Ответ LLM (токены, время)"
            : "";

        await _bot.SendMessage(
            chatId: chatId,
            text: $"🔍 Debug mode {status}{info}",
            parseMode: ParseMode.Html,
            cancellationToken: ct);

        return true;
    }

    private async Task<bool> HandleDebugStatusAsync(long chatId, CancellationToken ct)
    {
        var enabled = await _settings.IsDebugModeEnabledAsync();

        await _bot.SendMessage(
            chatId: chatId,
            text: $"""
                🔍 <b>Debug Mode</b>

                Статус: {(enabled ? "✅ ON" : "❌ OFF")}

                <b>Команды:</b>
                <code>/admin debug on</code> — включить
                <code>/admin debug off</code> — выключить

                <b>Что показывает:</b>
                • Query (запрос пользователя)
                • TopK результаты поиска (score, message_ids, текст)
                • Контекст для LLM (токены, сообщения)
                • Промпты (system + user)
                • Ответ LLM (провайдер, токены, время)
                """,
            parseMode: ParseMode.Html,
            cancellationToken: ct);

        return true;
    }

    private async Task<bool> HandleReportAsync(long chatId, CancellationToken ct)
    {
        await _reportService.SendImmediateReportAsync(chatId, ct);
        return true;
    }

    private async Task<bool> HandleSetSummaryTimeAsync(long chatId, string time, CancellationToken ct)
    {
        if (!TimeSpan.TryParse(time, out var parsedTime) || parsedTime.TotalHours >= 24)
        {
            await _bot.SendMessage(
                chatId: chatId,
                text: "❌ Неверный формат времени. Используй HH:mm (например: 21:00)",
                cancellationToken: ct);
            return true;
        }

        await _settings.SetSummaryTimeAsync(time);

        await _bot.SendMessage(
            chatId: chatId,
            text: $"✅ Время ежедневного саммари изменено на <b>{time}</b>\n\n⚠️ Изменения вступят в силу после перезапуска бота.",
            parseMode: ParseMode.Html,
            cancellationToken: ct);

        return true;
    }

    private async Task<bool> HandleSetReportTimeAsync(long chatId, string time, CancellationToken ct)
    {
        if (!TimeSpan.TryParse(time, out var parsedTime) || parsedTime.TotalHours >= 24)
        {
            await _bot.SendMessage(
                chatId: chatId,
                text: "❌ Неверный формат времени. Используй HH:mm (например: 10:00)",
                cancellationToken: ct);
            return true;
        }

        await _settings.SetReportTimeAsync(time);

        await _bot.SendMessage(
            chatId: chatId,
            text: $"✅ Время отчёта в личку изменено на <b>{time}</b>",
            parseMode: ParseMode.Html,
            cancellationToken: ct);

        return true;
    }

    private async Task<bool> HandleSetTimezoneAsync(long chatId, string offset, CancellationToken ct)
    {
        // Accept formats: +6, +06, +06:00, 6
        var cleanOffset = offset.TrimStart('+');
        if (!cleanOffset.Contains(':'))
            cleanOffset += ":00";

        if (!TimeSpan.TryParse(cleanOffset, out var parsedOffset) || parsedOffset.TotalHours > 14 || parsedOffset.TotalHours < -12)
        {
            await _bot.SendMessage(
                chatId: chatId,
                text: "❌ Неверный часовой пояс. Используй формат: +6, +06:00 и т.д.",
                cancellationToken: ct);
            return true;
        }

        await _settings.SetTimezoneOffsetAsync($"+{cleanOffset}");

        await _bot.SendMessage(
            chatId: chatId,
            text: $"✅ Часовой пояс изменён на <b>UTC+{parsedOffset:hh\\:mm}</b>",
            parseMode: ParseMode.Html,
            cancellationToken: ct);

        return true;
    }

    private async Task<bool> HandlePromptsListAsync(long chatId, CancellationToken ct)
    {
        var prompts = await _promptSettings.GetAllPromptsAsync();

        var sb = new StringBuilder();
        sb.AppendLine("<b>🎭 Промпты команд</b>\n");

        foreach (var prompt in prompts)
        {
            var status = prompt.IsCustom ? "✏️ кастомный" : "📋 дефолтный";
            var tagInfo = !string.IsNullOrEmpty(prompt.LlmTag) ? $" 🏷️ {prompt.LlmTag}" : "";
            sb.AppendLine($"<b>/{prompt.Command}</b> — {prompt.Description}{tagInfo}");
            sb.AppendLine($"   {status}");
            if (prompt.IsCustom && prompt.UpdatedAt.HasValue)
            {
                sb.AppendLine($"   📅 {prompt.UpdatedAt.Value:dd.MM.yyyy HH:mm}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("💡 <b>Команды:</b>");
        sb.AppendLine("<code>/admin prompt ask</code> — показать промпт");
        sb.AppendLine("<code>/admin prompt_tag ask uncensored</code> — тег LLM");
        sb.AppendLine("<code>/admin prompt_reset ask</code> — сбросить на дефолт");

        await _bot.SendMessage(
            chatId: chatId,
            text: sb.ToString(),
            parseMode: ParseMode.Html,
            cancellationToken: ct);

        return true;
    }

    private async Task<bool> HandlePromptShowAsync(long chatId, string command, CancellationToken ct)
    {
        var defaults = _promptSettings.GetDefaults();
        if (!defaults.ContainsKey(command))
        {
            await _bot.SendMessage(
                chatId: chatId,
                text: $"❌ Неизвестная команда: {command}\n\nДоступные: {string.Join(", ", defaults.Keys)}",
                cancellationToken: ct);
            return true;
        }

        var currentPrompt = await _promptSettings.GetPromptAsync(command);
        var prompts = await _promptSettings.GetAllPromptsAsync();
        var promptInfo = prompts.FirstOrDefault(p => p.Command == command);
        var isCustom = promptInfo?.IsCustom ?? false;

        var sb = new StringBuilder();
        sb.AppendLine($"<b>🎭 Промпт для /{command}</b>");
        sb.AppendLine(isCustom ? "✏️ Кастомный" : "📋 Дефолтный");
        sb.AppendLine();
        sb.AppendLine("<b>Текущий промпт:</b>");
        sb.AppendLine("───────────────");

        await _bot.SendMessage(
            chatId: chatId,
            text: sb.ToString(),
            parseMode: ParseMode.Html,
            cancellationToken: ct);

        // Send prompt as separate message (may be long)
        await _bot.SendMessage(
            chatId: chatId,
            text: $"<pre>{EscapeHtml(currentPrompt)}</pre>",
            parseMode: ParseMode.Html,
            cancellationToken: ct);

        await _bot.SendMessage(
            chatId: chatId,
            text: $"📎 Чтобы изменить — отправь TXT файл с caption:\n<code>/admin prompt {command}</code>",
            parseMode: ParseMode.Html,
            cancellationToken: ct);

        return true;
    }

    private async Task<bool> HandlePromptResetAsync(long chatId, string command, CancellationToken ct)
    {
        var defaults = _promptSettings.GetDefaults();
        if (!defaults.ContainsKey(command))
        {
            await _bot.SendMessage(
                chatId: chatId,
                text: $"❌ Неизвестная команда: {command}\n\nДоступные: {string.Join(", ", defaults.Keys)}",
                cancellationToken: ct);
            return true;
        }

        await _promptSettings.ResetPromptAsync(command);

        await _bot.SendMessage(
            chatId: chatId,
            text: $"✅ Промпт для <b>/{command}</b> сброшен на дефолтный",
            parseMode: ParseMode.Html,
            cancellationToken: ct);

        return true;
    }

    private async Task<bool> HandlePromptFileAsync(Message message, CancellationToken ct)
    {
        var chatId = message.Chat.Id;
        var caption = message.Caption ?? "";

        // Parse command from caption: /admin prompt ask
        var parts = caption.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            await _bot.SendMessage(
                chatId: chatId,
                text: "❌ Укажи команду в caption: <code>/admin prompt ask</code>",
                parseMode: ParseMode.Html,
                cancellationToken: ct);
            return true;
        }

        var command = parts[2].ToLowerInvariant();
        var defaults = _promptSettings.GetDefaults();

        if (!defaults.ContainsKey(command))
        {
            await _bot.SendMessage(
                chatId: chatId,
                text: $"❌ Неизвестная команда: {command}\n\nДоступные: {string.Join(", ", defaults.Keys)}",
                cancellationToken: ct);
            return true;
        }

        var document = message.Document!;

        // Validate file
        if (!document.FileName?.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ?? true)
        {
            await _bot.SendMessage(
                chatId: chatId,
                text: "❌ Файл должен быть TXT",
                cancellationToken: ct);
            return true;
        }

        if (document.FileSize > 100 * 1024) // 100KB limit
        {
            await _bot.SendMessage(
                chatId: chatId,
                text: "❌ Файл слишком большой (лимит 100 КБ)",
                cancellationToken: ct);
            return true;
        }

        try
        {
            // Download file
            var file = await _bot.GetFile(document.FileId, ct);
            using var stream = new MemoryStream();
            await _bot.DownloadFile(file.FilePath!, stream, ct);

            var promptText = Encoding.UTF8.GetString(stream.ToArray()).Trim();

            if (string.IsNullOrWhiteSpace(promptText))
            {
                await _bot.SendMessage(
                    chatId: chatId,
                    text: "❌ Файл пустой",
                    cancellationToken: ct);
                return true;
            }

            // Save prompt
            await _promptSettings.SetPromptAsync(command, promptText);

            await _bot.SendMessage(
                chatId: chatId,
                text: $"✅ Промпт для <b>/{command}</b> обновлён!\n\n📝 Размер: {promptText.Length} символов",
                parseMode: ParseMode.Html,
                cancellationToken: ct);

            _logger.LogInformation("[Admin] Prompt for {Command} updated by admin, size: {Size}", command, promptText.Length);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Admin] Failed to update prompt for {Command}", command);
            await _bot.SendMessage(
                chatId: chatId,
                text: $"❌ Ошибка: {ex.Message}",
                cancellationToken: ct);
        }

        return true;
    }

    private async Task<bool> HandleLlmListAsync(long chatId, CancellationToken ct)
    {
        var providers = _llmRouter.GetAllProviders();
        var defaultName = _llmRouter.DefaultProviderName;

        var sb = new StringBuilder();
        sb.AppendLine("<b>🤖 LLM Провайдеры</b>\n");

        if (providers.Count == 0)
        {
            sb.AppendLine("❌ Нет зарегистрированных провайдеров");
        }
        else
        {
            foreach (var (name, options) in providers.OrderBy(p => p.Value.Priority))
            {
                var status = options.Enabled ? "✅" : "⏸️";
                var isDefault = name == defaultName ? " ⭐ <i>(default)</i>" : "";

                sb.AppendLine($"{status} <b>{name}</b>{isDefault}");
                sb.AppendLine($"   📦 {options.Model}");
                sb.AppendLine($"   🏷️ [{string.Join(", ", options.Tags)}]");
                sb.AppendLine();
            }
        }

        sb.AppendLine("💡 <code>/admin llm_set &lt;name&gt;</code> — сменить дефолтный");

        await _bot.SendMessage(
            chatId: chatId,
            text: sb.ToString(),
            parseMode: ParseMode.Html,
            cancellationToken: ct);

        return true;
    }

    private async Task<bool> HandleLlmTestAsync(long chatId, string? providerName, CancellationToken ct)
    {
        var statusMsg = await _bot.SendMessage(
            chatId: chatId,
            text: "⏳ Тестирую LLM...",
            cancellationToken: ct);

        try
        {
            ILlmProvider provider;
            if (string.IsNullOrEmpty(providerName))
            {
                provider = _llmRouter.GetDefault();
            }
            else
            {
                provider = _llmRouter.GetProvider(providerName)
                    ?? throw new ArgumentException($"Провайдер '{providerName}' не найден");
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();

            var response = await provider.CompleteAsync(new LlmRequest
            {
                SystemPrompt = "Ты тестовый бот. Отвечай кратко.",
                UserPrompt = "Скажи 'Привет, я работаю!' и добавь одну случайную шутку про программистов.",
                Temperature = 0.8
            }, ct);

            sw.Stop();

            var sb = new StringBuilder();
            sb.AppendLine($"✅ <b>Тест пройден!</b>\n");
            sb.AppendLine($"📦 <b>Провайдер:</b> {response.Provider}");
            sb.AppendLine($"🤖 <b>Модель:</b> {response.Model}");
            sb.AppendLine($"⏱️ <b>Время:</b> {sw.ElapsedMilliseconds}ms");
            sb.AppendLine($"📊 <b>Токены:</b> {response.PromptTokens} + {response.CompletionTokens} = {response.TotalTokens}");
            sb.AppendLine();
            sb.AppendLine("<b>Ответ:</b>");
            sb.AppendLine($"<i>{EscapeHtml(response.Content)}</i>");

            await _bot.EditMessageText(
                chatId: chatId,
                messageId: statusMsg.MessageId,
                text: sb.ToString(),
                parseMode: ParseMode.Html,
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            await _bot.EditMessageText(
                chatId: chatId,
                messageId: statusMsg.MessageId,
                text: $"❌ <b>Ошибка теста</b>\n\n{EscapeHtml(ex.Message)}",
                parseMode: ParseMode.Html,
                cancellationToken: ct);
        }

        return true;
    }

    private async Task<bool> HandleLlmSetAsync(long chatId, string providerName, CancellationToken ct)
    {
        var providers = _llmRouter.GetAllProviders();

        if (!providers.ContainsKey(providerName))
        {
            await _bot.SendMessage(
                chatId: chatId,
                text: $"❌ Провайдер <b>{providerName}</b> не найден\n\nДоступные: {string.Join(", ", providers.Keys)}",
                parseMode: ParseMode.Html,
                cancellationToken: ct);
            return true;
        }

        var oldDefault = _llmRouter.DefaultProviderName;
        var success = _llmRouter.SetDefaultProvider(providerName);

        if (success)
        {
            var newProvider = providers[providerName];
            await _bot.SendMessage(
                chatId: chatId,
                text: $"""
                    ✅ <b>Дефолтный провайдер изменён</b>

                    {oldDefault} → <b>{providerName}</b>
                    📦 Модель: {newProvider.Model}
                    """,
                parseMode: ParseMode.Html,
                cancellationToken: ct);
        }
        else
        {
            await _bot.SendMessage(
                chatId: chatId,
                text: "❌ Не удалось изменить провайдера",
                cancellationToken: ct);
        }

        return true;
    }

    private async Task<bool> HandleLlmToggleAsync(long chatId, string providerName, bool enabled, CancellationToken ct)
    {
        var success = _llmRouter.SetProviderEnabled(providerName, enabled);

        if (success)
        {
            var status = enabled ? "✅ включён" : "❌ выключен";
            await _bot.SendMessage(
                chatId: chatId,
                text: $"Провайдер <b>{providerName}</b> {status}",
                parseMode: ParseMode.Html,
                cancellationToken: ct);
        }
        else
        {
            var providers = _llmRouter.GetAllProviders();
            await _bot.SendMessage(
                chatId: chatId,
                text: $"❌ Провайдер <b>{providerName}</b> не найден\n\nДоступные: {string.Join(", ", providers.Keys)}",
                parseMode: ParseMode.Html,
                cancellationToken: ct);
        }

        return true;
    }

    private async Task<bool> HandlePromptTagAsync(long chatId, string command, string? tag, CancellationToken ct)
    {
        var defaults = _promptSettings.GetDefaults();
        if (!defaults.ContainsKey(command))
        {
            await _bot.SendMessage(
                chatId: chatId,
                text: $"❌ Неизвестная команда: {command}\n\nДоступные: {string.Join(", ", defaults.Keys)}",
                cancellationToken: ct);
            return true;
        }

        // Если тег не указан — сбросить на null
        var tagToSet = string.IsNullOrWhiteSpace(tag) || tag == "null" || tag == "default" ? null : tag;

        await _promptSettings.SetLlmTagAsync(command, tagToSet);

        var providers = _llmRouter.GetAllProviders();
        var availableTags = providers.Values.SelectMany(p => p.Tags).Distinct().ToList();

        if (tagToSet == null)
        {
            await _bot.SendMessage(
                chatId: chatId,
                text: $"✅ Тег для <b>/{command}</b> сброшен (будет использоваться дефолтный провайдер)",
                parseMode: ParseMode.Html,
                cancellationToken: ct);
        }
        else
        {
            var hasProvider = providers.Values.Any(p => p.Tags.Contains(tagToSet, StringComparer.OrdinalIgnoreCase));
            var warning = hasProvider ? "" : $"\n\n⚠️ Провайдер с тегом '{tagToSet}' не найден! Доступные теги: {string.Join(", ", availableTags)}";

            await _bot.SendMessage(
                chatId: chatId,
                text: $"✅ Тег для <b>/{command}</b> установлен: <code>{tagToSet}</code>{warning}",
                parseMode: ParseMode.Html,
                cancellationToken: ct);
        }

        return true;
    }

    private async Task<bool> HandleReindexAsync(long chatId, string chatIdStr, CancellationToken ct)
    {
        if (!long.TryParse(chatIdStr, out var targetChatId))
        {
            await _bot.SendMessage(
                chatId: chatId,
                text: "❌ Неверный формат Chat ID",
                cancellationToken: ct);
            return true;
        }

        var stats = await _embeddingService.GetStatsAsync(targetChatId, ct);

        await _bot.SendMessage(
            chatId: chatId,
            text: $"""
                ⚠️ <b>Переиндексация эмбеддингов</b>

                Чат: <code>{targetChatId}</code>
                Текущих эмбеддингов: {stats.TotalEmbeddings}

                Это удалит все эмбеддинги чата и BackgroundService пересоздаст их в новом формате.

                Для подтверждения: <code>/admin reindex {targetChatId} confirm</code>
                """,
            parseMode: ParseMode.Html,
            cancellationToken: ct);

        return true;
    }

    private async Task<bool> HandleReindexConfirmAsync(long chatId, string chatIdStr, CancellationToken ct)
    {
        // Handle "all" for all chats
        if (chatIdStr.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            var statusMsg = await _bot.SendMessage(
                chatId: chatId,
                text: "⏳ Удаляю ВСЕ эмбеддинги...",
                cancellationToken: ct);

            await _embeddingService.DeleteAllEmbeddingsAsync(ct);

            var (total, _, _) = await _messageStore.GetEmbeddingStatsAsync();

            await _bot.EditMessageText(
                chatId: chatId,
                messageId: statusMsg.MessageId,
                text: $"""
                    ✅ <b>Все эмбеддинги удалены</b>

                    BackgroundService начнёт переиндексацию автоматически.
                    Сообщений для индексации: {total}

                    💡 Следить за прогрессом можно в логах.
                    """,
                parseMode: ParseMode.Html,
                cancellationToken: ct);

            return true;
        }

        if (!long.TryParse(chatIdStr, out var targetChatId))
        {
            await _bot.SendMessage(
                chatId: chatId,
                text: "❌ Неверный формат Chat ID",
                cancellationToken: ct);
            return true;
        }

        var statusMessage = await _bot.SendMessage(
            chatId: chatId,
            text: $"⏳ Удаляю эмбеддинги чата {targetChatId}...",
            cancellationToken: ct);

        await _embeddingService.DeleteChatEmbeddingsAsync(targetChatId, ct);

        await _bot.EditMessageText(
            chatId: chatId,
            messageId: statusMessage.MessageId,
            text: $"""
                ✅ <b>Эмбеддинги чата удалены</b>

                Чат: <code>{targetChatId}</code>

                BackgroundService начнёт переиндексацию автоматически.
                💡 Следить за прогрессом можно в логах.
                """,
            parseMode: ParseMode.Html,
            cancellationToken: ct);

        return true;
    }

    private async Task<bool> HandleReindexAllAsync(long chatId, CancellationToken ct)
    {
        var (total, indexed, pending) = await _messageStore.GetEmbeddingStatsAsync();

        await _bot.SendMessage(
            chatId: chatId,
            text: $"""
                ⚠️ <b>Переиндексация ВСЕХ эмбеддингов</b>

                Всего эмбеддингов: {indexed}
                Сообщений для индексации: {total}

                Использование:
                • <code>/admin reindex -1234567</code> — конкретный чат
                • <code>/admin reindex all confirm</code> — ВСЕ чаты

                ⚠️ Полная переиндексация может занять много времени и стоить денег (API calls).
                """,
            parseMode: ParseMode.Html,
            cancellationToken: ct);

        return true;
    }

    private async Task<bool> HandleNamesAsync(long chatId, string chatIdStr, CancellationToken ct)
    {
        if (!long.TryParse(chatIdStr, out var targetChatId))
        {
            await _bot.SendMessage(
                chatId: chatId,
                text: "❌ Неверный формат Chat ID",
                cancellationToken: ct);
            return true;
        }

        var names = await _messageStore.GetUniqueDisplayNamesAsync(targetChatId);

        if (names.Count == 0)
        {
            await _bot.SendMessage(
                chatId: chatId,
                text: "❌ Нет сообщений в этом чате",
                cancellationToken: ct);
            return true;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"<b>👥 Имена в чате {targetChatId}</b>\n");

        foreach (var (name, count) in names.Take(50))
        {
            sb.AppendLine($"• <code>{EscapeHtml(name)}</code> — {count} сообщ.");
        }

        if (names.Count > 50)
        {
            sb.AppendLine($"\n... и ещё {names.Count - 50} имён");
        }

        sb.AppendLine("\n💡 Чтобы переименовать:");
        sb.AppendLine("<code>/admin rename -1234567 \"Старое\" \"Новое\"</code>");

        await _bot.SendMessage(
            chatId: chatId,
            text: sb.ToString(),
            parseMode: ParseMode.Html,
            cancellationToken: ct);

        return true;
    }

    private async Task<bool> HandleRenameAsync(long chatId, string fullText, CancellationToken ct)
    {
        // Parse: /admin rename [-1234567] "Old Name" "New Name"
        // or:    /admin rename "Old Name" "New Name" (all chats)
        var regex = new System.Text.RegularExpressions.Regex(
            @"/admin\s+rename\s+(?:(-?\d+)\s+)?""([^""]+)""\s+""([^""]+)""",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        var match = regex.Match(fullText);
        if (!match.Success)
        {
            await _bot.SendMessage(
                chatId: chatId,
                text: """
                    ❌ <b>Неверный формат</b>

                    Использование:
                    <code>/admin rename -1234567 "Старое имя" "Новое имя"</code>
                    <code>/admin rename "Старое имя" "Новое имя"</code> (все чаты)

                    💡 Чтобы посмотреть имена: <code>/admin names -1234567</code>
                    """,
                parseMode: ParseMode.Html,
                cancellationToken: ct);
            return true;
        }

        long? targetChatId = null;
        if (!string.IsNullOrEmpty(match.Groups[1].Value))
        {
            targetChatId = long.Parse(match.Groups[1].Value);
        }

        var oldName = match.Groups[2].Value;
        var newName = match.Groups[3].Value;

        var statusMsg = await _bot.SendMessage(
            chatId: chatId,
            text: "⏳ Переименовываю сообщения...",
            cancellationToken: ct);

        var messagesAffected = await _messageStore.RenameDisplayNameAsync(targetChatId, oldName, newName);

        await _bot.EditMessageText(
            chatId: chatId,
            messageId: statusMsg.MessageId,
            text: "⏳ Переименовываю эмбеддинги...",
            cancellationToken: ct);

        var embeddingsAffected = await _embeddingService.RenameInEmbeddingsAsync(targetChatId, oldName, newName, ct);

        var scope = targetChatId.HasValue ? $"в чате {targetChatId}" : "во всех чатах";

        await _bot.EditMessageText(
            chatId: chatId,
            messageId: statusMsg.MessageId,
            text: $"""
                ✅ <b>Переименование выполнено</b>

                {EscapeHtml(oldName)} → <b>{EscapeHtml(newName)}</b>
                📊 Обновлено: {messagesAffected} сообщений, {embeddingsAffected} эмбеддингов {scope}
                """,
            parseMode: ParseMode.Html,
            cancellationToken: ct);

        return true;
    }

    private static string EscapeHtml(string text)
    {
        return text
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");
    }

    private async Task<bool> SendHelpAsync(long chatId, CancellationToken ct)
    {
        var help = """
            <b>🔧 Админ-команды</b>

            <b>Просмотр:</b>
            /admin status — текущие настройки
            /admin report — отчёт по логам прямо сейчас
            /admin chats — список известных чатов

            <b>🔍 Debug:</b>
            /admin debug — статус debug mode
            /admin debug on — включить (отчёты в личку)
            /admin debug off — выключить

            <b>Импорт истории:</b>
            /admin import &lt;chat_id&gt; — инструкция по импорту

            <b>🤖 LLM:</b>
            /admin llm — список провайдеров
            /admin llm_set &lt;name&gt; — сменить дефолтный
            /admin llm_on &lt;name&gt; — включить провайдера
            /admin llm_off &lt;name&gt; — выключить провайдера
            /admin llm_test — тест дефолтного
            /admin llm_test &lt;name&gt; — тест конкретного

            <b>🎭 Промпты:</b>
            /admin prompts — список всех промптов
            /admin prompt &lt;cmd&gt; — показать промпт
            /admin prompt_tag &lt;cmd&gt; &lt;tag&gt; — установить LLM тег
            /admin prompt_reset &lt;cmd&gt; — сбросить на дефолт

            <b>👥 Имена (для исправления импорта):</b>
            /admin names &lt;chat_id&gt; — список имён в чате
            /admin rename &lt;chat_id&gt; "Старое" "Новое" — переименовать

            <b>🔄 Переиндексация эмбеддингов:</b>
            /admin reindex &lt;chat_id&gt; — инфо + подтверждение
            /admin reindex all confirm — пересоздать ВСЕ

            <b>Настройки:</b>
            /admin set_summary_time HH:mm — время саммари
            /admin set_report_time HH:mm — время отчёта
            /admin set_timezone +N — часовой пояс
            """;

        await _bot.SendMessage(
            chatId: chatId,
            text: help,
            parseMode: ParseMode.Html,
            cancellationToken: ct);

        return true;
    }
}
