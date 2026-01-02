using System.Text;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;
using WatchmenBot.Infrastructure.Settings;
using WatchmenBot.Features.Messages.Services;
using WatchmenBot.Features.Admin.Services;
using WatchmenBot.Features.Webhook.Services;

namespace WatchmenBot.Features.Admin.Commands;

/// <summary>
/// /admin chats - list known chats
/// </summary>
public class ChatsCommand(
    ITelegramBotClient bot,
    MessageStore messageStore,
    ILogger<ChatsCommand> logger)
    : AdminCommandBase(bot, logger)
{
    public override async Task<bool> ExecuteAsync(AdminCommandContext context, CancellationToken ct)
    {
        var chats = await messageStore.GetKnownChatsAsync();

        if (chats.Count == 0)
        {
            await SendMessageAsync(context.ChatId, "📭 Нет сохранённых чатов", ct);
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

        await Bot.SendMessage(
            chatId: context.ChatId,
            text: sb.ToString(),
            parseMode: ParseMode.Html,
            cancellationToken: ct);

        return true;
    }
}
