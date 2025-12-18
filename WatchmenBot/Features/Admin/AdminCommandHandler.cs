using System.IO.Compression;
using System.Text;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using WatchmenBot.Services;

namespace WatchmenBot.Features.Admin;

public class AdminCommandHandler
{
    private readonly ITelegramBotClient _bot;
    private readonly AdminSettingsStore _settings;
    private readonly LogCollector _logCollector;
    private readonly DailyLogReportService _reportService;
    private readonly ChatImportService _importService;
    private readonly MessageStore _messageStore;
    private readonly TelegramExportParser _exportParser;
    private readonly PromptSettingsStore _promptSettings;
    private readonly ILogger<AdminCommandHandler> _logger;

    public AdminCommandHandler(
        ITelegramBotClient bot,
        AdminSettingsStore settings,
        LogCollector logCollector,
        DailyLogReportService reportService,
        ChatImportService importService,
        MessageStore messageStore,
        TelegramExportParser exportParser,
        PromptSettingsStore promptSettings,
        ILogger<AdminCommandHandler> logger)
    {
        _bot = bot;
        _settings = settings;
        _logCollector = logCollector;
        _reportService = reportService;
        _importService = importService;
        _messageStore = messageStore;
        _exportParser = exportParser;
        _promptSettings = promptSettings;
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
                "import" when parts.Length >= 3 => await HandleImportCommandAsync(message.Chat.Id, parts[2], ct),
                "prompts" => await HandlePromptsListAsync(message.Chat.Id, ct),
                "prompt" when parts.Length >= 3 => await HandlePromptShowAsync(message.Chat.Id, parts[2], ct),
                "prompt_reset" when parts.Length >= 3 => await HandlePromptResetAsync(message.Chat.Id, parts[2], ct),
                "set_summary_time" when parts.Length >= 3 => await HandleSetSummaryTimeAsync(message.Chat.Id, parts[2], ct),
                "set_report_time" when parts.Length >= 3 => await HandleSetReportTimeAsync(message.Chat.Id, parts[2], ct),
                "set_timezone" when parts.Length >= 3 => await HandleSetTimezoneAsync(message.Chat.Id, parts[2], ct),
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

        var sb = new StringBuilder();
        sb.AppendLine("<b>⚙️ Текущие настройки</b>");
        sb.AppendLine();
        sb.AppendLine($"🕐 <b>Время саммари:</b> {settings["summary_time"]}");
        sb.AppendLine($"📋 <b>Время отчёта:</b> {settings["report_time"]}");
        sb.AppendLine($"🌍 <b>Часовой пояс:</b> UTC+{tz:hh\\:mm}");
        sb.AppendLine();
        sb.AppendLine($"👤 <b>Admin ID:</b> {_settings.GetAdminUserId()}");

        await _bot.SendMessage(
            chatId: chatId,
            text: sb.ToString(),
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
            sb.AppendLine($"<b>/{prompt.Command}</b> — {prompt.Description}");
            sb.AppendLine($"   {status}");
            if (prompt.IsCustom && prompt.UpdatedAt.HasValue)
            {
                sb.AppendLine($"   📅 {prompt.UpdatedAt.Value:dd.MM.yyyy HH:mm}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("💡 <b>Команды:</b>");
        sb.AppendLine("<code>/admin prompt ask</code> — показать промпт");
        sb.AppendLine("<code>/admin prompt_reset ask</code> — сбросить на дефолт");
        sb.AppendLine("\n📎 Отправь TXT файл с caption:");
        sb.AppendLine("<code>/admin prompt ask</code>");

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

            <b>Импорт истории:</b>
            /admin import &lt;chat_id&gt; — инструкция по импорту

            Отправь ZIP с экспортом и caption:
            <code>/admin import -1001234567890</code>

            <b>🎭 Промпты:</b>
            /admin prompts — список всех промптов
            /admin prompt &lt;cmd&gt; — показать промпт
            /admin prompt_reset &lt;cmd&gt; — сбросить на дефолт

            📎 Изменить промпт — отправь TXT файл:
            <code>/admin prompt ask</code>

            <b>Настройки:</b>
            /admin set_summary_time HH:mm — время саммари
            /admin set_report_time HH:mm — время отчёта
            /admin set_timezone +N — часовой пояс

            <b>Примеры:</b>
            <code>/admin set_summary_time 21:00</code>
            <code>/admin set_timezone +6</code>
            """;

        await _bot.SendMessage(
            chatId: chatId,
            text: help,
            parseMode: ParseMode.Html,
            cancellationToken: ct);

        return true;
    }
}
