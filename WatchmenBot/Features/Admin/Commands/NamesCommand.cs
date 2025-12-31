using System.Text;
using Telegram.Bot;
using WatchmenBot.Services;

namespace WatchmenBot.Features.Admin.Commands;

/// <summary>
/// /admin names <chat_id> - show unique display names in chat
/// </summary>
public class NamesCommand : AdminCommandBase
{
    private readonly MessageStore _messageStore;

    public NamesCommand(
        ITelegramBotClient bot,
        MessageStore messageStore,
        ILogger<NamesCommand> logger) : base(bot, logger)
    {
        _messageStore = messageStore;
    }

    public override async Task<bool> ExecuteAsync(AdminCommandContext context, CancellationToken ct)
    {
        if (context.Args.Length == 0)
        {
            await SendMessageAsync(context.ChatId, "❌ Укажи Chat ID: <code>/admin names -1001234567890</code>", ct);
            return true;
        }

        if (!long.TryParse(context.Args[0], out var targetChatId))
        {
            await SendMessageAsync(context.ChatId, "❌ Неверный формат Chat ID", ct);
            return true;
        }

        var names = await _messageStore.GetUniqueDisplayNamesAsync(targetChatId);

        if (names.Count == 0)
        {
            await SendMessageAsync(context.ChatId, "❌ Нет сообщений в этом чате", ct);
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

        await SendMessageAsync(context.ChatId, sb.ToString(), ct);

        return true;
    }
}
