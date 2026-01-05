using System.Text;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;
using WatchmenBot.Features.Messages.Services;
using WatchmenBot.Infrastructure.Settings;

namespace WatchmenBot.Features.Admin.Commands;

/// <summary>
/// /admin mode - manage chat modes (business/funny)
/// Usage:
///   /admin mode                    - list all chats with their modes
///   /admin mode <chat_id>          - show mode for specific chat
///   /admin mode <chat_id> business - set business mode
///   /admin mode <chat_id> funny    - set funny mode
/// </summary>
public class ModeCommand(
    ITelegramBotClient bot,
    MessageStore messageStore,
    ChatSettingsStore chatSettings,
    ILogger<ModeCommand> logger)
    : AdminCommandBase(bot, logger)
{
    public override async Task<bool> ExecuteAsync(AdminCommandContext context, CancellationToken ct)
    {
        var args = context.Args;

        // /admin mode - list all chats with modes
        if (args.Length == 0)
        {
            return await ListAllModesAsync(context, ct);
        }

        // Parse chat_id
        if (!long.TryParse(args[0], out var chatId))
        {
            await SendMessageAsync(context.ChatId,
                "❌ Неверный формат chat_id. Используй число, например: <code>-1001234567890</code>", ct);
            return false;
        }

        // /admin mode <chat_id> - show mode for chat
        if (args.Length == 1)
        {
            return await ShowChatModeAsync(context, chatId, ct);
        }

        // /admin mode <chat_id> <mode> - set mode
        var modeArg = args[1];
        if (!ChatModeExtensions.TryParse(modeArg, out var newMode))
        {
            await SendMessageAsync(context.ChatId, $"""
                ❌ Неизвестный режим: <code>{EscapeHtml(modeArg)}</code>

                Доступные режимы:
                • <code>business</code> — деловой
                • <code>funny</code> — весёлый
                """, ct);
            return false;
        }

        return await SetChatModeAsync(context, chatId, newMode, ct);
    }

    private async Task<bool> ListAllModesAsync(AdminCommandContext context, CancellationToken ct)
    {
        var chats = await messageStore.GetKnownChatsAsync();

        if (chats.Count == 0)
        {
            await SendMessageAsync(context.ChatId, "📭 Нет сохранённых чатов", ct);
            return true;
        }

        var sb = new StringBuilder();
        sb.AppendLine("<b>🎭 Режимы чатов</b>\n");

        foreach (var chat in chats)
        {
            var settings = await chatSettings.GetSettingsAsync(chat.ChatId);
            var modeEmoji = settings.Mode.GetEmoji();
            var modeName = settings.Mode.GetDisplayName(settings.Language);
            var title = !string.IsNullOrWhiteSpace(chat.Title) ? chat.Title : "(без названия)";

            sb.AppendLine($"{modeEmoji} <b>{EscapeHtml(title)}</b>");
            sb.AppendLine($"   🆔 <code>{chat.ChatId}</code>");
            sb.AppendLine($"   📋 Режим: {modeName}");
            sb.AppendLine();
        }

        sb.AppendLine("""
            <b>Команды:</b>
            • <code>/admin mode &lt;chat_id&gt; business</code> — деловой
            • <code>/admin mode &lt;chat_id&gt; funny</code> — весёлый
            """);

        await Bot.SendMessage(
            chatId: context.ChatId,
            text: sb.ToString(),
            parseMode: ParseMode.Html,
            cancellationToken: ct);

        return true;
    }

    private async Task<bool> ShowChatModeAsync(AdminCommandContext context, long chatId, CancellationToken ct)
    {
        var settings = await chatSettings.GetSettingsAsync(chatId);
        var chat = (await messageStore.GetKnownChatsAsync())
            .FirstOrDefault(c => c.ChatId == chatId);

        var title = chat?.Title ?? "(неизвестный чат)";
        var modeEmoji = settings.Mode.GetEmoji();
        var modeName = settings.Mode.GetDisplayName(settings.Language);
        var modeDesc = settings.Mode.GetDescription(settings.Language);

        await SendMessageAsync(context.ChatId, $"""
            {modeEmoji} <b>{EscapeHtml(title)}</b>

            🆔 <code>{chatId}</code>
            📋 Режим: <b>{modeName}</b>
            📝 {modeDesc}

            <b>Изменить:</b>
            • <code>/admin mode {chatId} business</code>
            • <code>/admin mode {chatId} funny</code>
            """, ct);

        return true;
    }

    private async Task<bool> SetChatModeAsync(AdminCommandContext context, long chatId, ChatMode newMode, CancellationToken ct)
    {
        var oldSettings = await chatSettings.GetSettingsAsync(chatId);
        var oldMode = oldSettings.Mode;

        if (oldMode == newMode)
        {
            await SendMessageAsync(context.ChatId,
                $"{newMode.GetEmoji()} Режим <b>{newMode.GetDisplayName()}</b> уже активен для этого чата.", ct);
            return true;
        }

        await chatSettings.SetModeAsync(chatId, newMode);

        var chat = (await messageStore.GetKnownChatsAsync())
            .FirstOrDefault(c => c.ChatId == chatId);
        var title = chat?.Title ?? "(неизвестный чат)";

        Logger.LogInformation("[AdminMode] Chat {ChatId} ({Title}) mode changed: {OldMode} → {NewMode}",
            chatId, title, oldMode, newMode);

        await SendMessageAsync(context.ChatId, $"""
            ✅ <b>Режим изменён</b>

            🏠 <b>{EscapeHtml(title)}</b>
            🆔 <code>{chatId}</code>

            {oldMode.GetEmoji()} {oldMode.GetDisplayName()} → {newMode.GetEmoji()} <b>{newMode.GetDisplayName()}</b>

            <i>{newMode.GetDescription()}</i>
            """, ct);

        return true;
    }
}
