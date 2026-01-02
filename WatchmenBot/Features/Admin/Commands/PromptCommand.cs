using System.Text;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;
using WatchmenBot.Infrastructure.Settings;
using WatchmenBot.Features.Messages.Services;
using WatchmenBot.Features.Admin.Services;
using WatchmenBot.Features.Webhook.Services;

namespace WatchmenBot.Features.Admin.Commands;

/// <summary>
/// /admin prompt <command> - show prompt for command
/// Also handles file upload to update prompt
/// </summary>
public class PromptCommand(
    ITelegramBotClient bot,
    PromptSettingsStore promptSettings,
    ILogger<PromptCommand> logger)
    : AdminCommandBase(bot, logger)
{
    public override async Task<bool> ExecuteAsync(AdminCommandContext context, CancellationToken ct)
    {
        // Check if this is a file upload
        if (context.Message.Document != null)
        {
            return await HandleFileUploadAsync(context, ct);
        }

        // Show prompt
        if (context.Args.Length == 0)
        {
            await SendMessageAsync(context.ChatId, "❌ Укажи команду: <code>/admin prompt ask</code>", ct);
            return true;
        }

        var command = context.Args[0];
        var defaults = promptSettings.GetDefaults();

        if (!defaults.ContainsKey(command))
        {
            await SendMessageAsync(context.ChatId,
                $"❌ Неизвестная команда: {command}\n\nДоступные: {string.Join(", ", defaults.Keys)}", ct);
            return true;
        }

        var currentPrompt = await promptSettings.GetPromptAsync(command);
        var prompts = await promptSettings.GetAllPromptsAsync();
        var promptInfo = prompts.FirstOrDefault(p => p.Command == command);
        var isCustom = promptInfo?.IsCustom ?? false;

        var sb = new StringBuilder();
        sb.AppendLine($"<b>🎭 Промпт для /{command}</b>");
        sb.AppendLine(isCustom ? "✏️ Кастомный" : "📋 Дефолтный");
        sb.AppendLine();
        sb.AppendLine("<b>Текущий промпт:</b>");
        sb.AppendLine("───────────────");

        await SendMessageAsync(context.ChatId, sb.ToString(), ct);

        // Send prompt as separate message (may be long)
        await Bot.SendMessage(
            chatId: context.ChatId,
            text: $"<pre>{EscapeHtml(currentPrompt)}</pre>",
            parseMode: ParseMode.Html,
            cancellationToken: ct);

        await SendMessageAsync(context.ChatId,
            $"📎 Чтобы изменить — отправь TXT файл с caption:\n<code>/admin prompt {command}</code>", ct);

        return true;
    }

    private async Task<bool> HandleFileUploadAsync(AdminCommandContext context, CancellationToken ct)
    {
        var caption = context.Message.Caption ?? "";
        var chatId = context.ChatId;

        // Parse command from caption: /admin prompt ask
        var parts = caption.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            await SendMessageAsync(chatId, "❌ Укажи команду в caption: <code>/admin prompt ask</code>", ct);
            return true;
        }

        var command = parts[2].ToLowerInvariant();
        var defaults = promptSettings.GetDefaults();

        if (!defaults.ContainsKey(command))
        {
            await SendMessageAsync(chatId,
                $"❌ Неизвестная команда: {command}\n\nДоступные: {string.Join(", ", defaults.Keys)}", ct);
            return true;
        }

        var document = context.Message.Document!;

        // Validate file
        if (!document.FileName?.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ?? true)
        {
            await SendMessageAsync(chatId, "❌ Файл должен быть TXT", ct);
            return true;
        }

        if (document.FileSize > 100 * 1024) // 100KB limit
        {
            await SendMessageAsync(chatId, "❌ Файл слишком большой (лимит 100 КБ)", ct);
            return true;
        }

        try
        {
            // Download file
            var file = await Bot.GetFile(document.FileId, ct);
            using var stream = new MemoryStream();
            await Bot.DownloadFile(file.FilePath!, stream, ct);

            var promptText = Encoding.UTF8.GetString(stream.ToArray()).Trim();

            if (string.IsNullOrWhiteSpace(promptText))
            {
                await SendMessageAsync(chatId, "❌ Файл пустой", ct);
                return true;
            }

            // Save prompt
            await promptSettings.SetPromptAsync(command, promptText);

            await SendMessageAsync(chatId,
                $"✅ Промпт для <b>/{command}</b> обновлён!\n\n📝 Размер: {promptText.Length} символов", ct);

            Logger.LogInformation("[Admin] Prompt for {Command} updated by admin, size: {Size}", command, promptText.Length);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[Admin] Failed to update prompt for {Command}", command);
            await SendMessageAsync(chatId, $"❌ Ошибка: {ex.Message}", ct);
        }

        return true;
    }
}
