using System.IO.Compression;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;
using WatchmenBot.Features.Messages.Services;
using WatchmenBot.Infrastructure.Settings;
using WatchmenBot.Features.Admin.Services;
using WatchmenBot.Features.Webhook.Services;

namespace WatchmenBot.Features.Admin.Commands;

/// <summary>
/// /admin import <chat_id> - import Telegram export
/// Handles both text command (shows instructions) and file upload (performs import)
/// </summary>
public class ImportCommand(
    ITelegramBotClient bot,
    ChatImportService importService,
    TelegramExportParser exportParser,
    ILogger<ImportCommand> logger)
    : AdminCommandBase(bot, logger)
{
    public override async Task<bool> ExecuteAsync(AdminCommandContext context, CancellationToken ct)
    {
        // Check if this is a file upload
        if (context.Message.Document != null)
        {
            return await HandleFileUploadAsync(context, ct);
        }

        // Show instructions
        if (context.Args.Length == 0)
        {
            await SendMessageAsync(context.ChatId, "❌ Укажи Chat ID: <code>/admin import -1001234567890</code>", ct);
            return true;
        }

        var chatIdStr = context.Args[0];

        await SendMessageAsync(context.ChatId, $"""
            📦 <b>Импорт истории чата</b>

            Отправь ZIP-архив экспорта из Telegram Desktop с caption:
            <code>/admin import {chatIdStr}</code>

            <b>Как экспортировать:</b>
            1. Telegram Desktop → Чат → ⋮ → Export chat history
            2. Выбрать формат: HTML
            3. Запаковать папку в ZIP

            ⚠️ Лимит файла: 20 МБ
            """, ct);

        return true;
    }

    private async Task<bool> HandleFileUploadAsync(AdminCommandContext context, CancellationToken ct)
    {
        var caption = context.Message.Caption ?? "";
        var chatId = context.ChatId;

        // Parse chat ID from caption: /admin import -1001234567890
        var parts = caption.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || !long.TryParse(parts[2], out var targetChatId))
        {
            await SendMessageAsync(chatId, "❌ Укажи Chat ID в caption: <code>/admin import -1001234567890</code>", ct);
            return true;
        }

        var document = context.Message.Document!;

        // Validate file
        if (!document.FileName?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ?? true)
        {
            await SendMessageAsync(chatId, "❌ Файл должен быть ZIP-архивом", ct);
            return true;
        }

        if (document.FileSize > 20 * 1024 * 1024)
        {
            await SendMessageAsync(chatId, "❌ Файл слишком большой (лимит 20 МБ)", ct);
            return true;
        }

        var statusMsg = await Bot.SendMessage(
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
                var file = await Bot.GetFile(document.FileId, ct);
                await using (var fileStream = File.Create(zipPath))
                {
                    await Bot.DownloadFile(file.FilePath!, fileStream, ct);
                }

                await Bot.EditMessageText(
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
                    await Bot.EditMessageText(
                        chatId: chatId,
                        messageId: statusMsg.MessageId,
                        text: "❌ В архиве не найдены файлы messages*.html",
                        cancellationToken: ct);
                    return true;
                }

                // Get chat title from export
                var exportChatTitle = exportParser.GetChatTitleFromExport(exportDir);
                var chatTitleInfo = !string.IsNullOrEmpty(exportChatTitle)
                    ? $"\n📝 Чат из экспорта: <b>{exportChatTitle}</b>"
                    : "";

                await Bot.EditMessageText(
                    chatId: chatId,
                    messageId: statusMsg.MessageId,
                    text: $"⏳ Импортирую сообщения...{chatTitleInfo}",
                    parseMode: ParseMode.Html,
                    cancellationToken: ct);

                // Import
                var result = await importService.ImportFromDirectoryAsync(exportDir, targetChatId, true, ct);

                if (result.IsSuccess)
                {
                    await Bot.EditMessageText(
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
                    await Bot.EditMessageText(
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
            Logger.LogError(ex, "[Admin] Import failed");

            await Bot.EditMessageText(
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
}
