using System.Text.RegularExpressions;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;
using WatchmenBot.Features.Search.Services;
using WatchmenBot.Infrastructure.Settings;
using WatchmenBot.Features.Messages.Services;
using WatchmenBot.Features.Admin.Services;
using WatchmenBot.Features.Webhook.Services;

namespace WatchmenBot.Features.Admin.Commands;

/// <summary>
/// /admin rename [-chat_id] "Old Name" "New Name" - rename display name in messages and embeddings
/// </summary>
public class RenameCommand(
    ITelegramBotClient bot,
    MessageStore messageStore,
    EmbeddingService embeddingService,
    ILogger<RenameCommand> logger)
    : AdminCommandBase(bot, logger)
{
    public override async Task<bool> ExecuteAsync(AdminCommandContext context, CancellationToken ct)
    {
        // Parse: /admin rename [-1234567] "Old Name" "New Name"
        // or:    /admin rename "Old Name" "New Name" (all chats)
        var regex = new Regex(
            @"/admin\s+rename\s+(?:(-?\d+)\s+)?""([^""]+)""\s+""([^""]+)""",
            RegexOptions.IgnoreCase);

        var match = regex.Match(context.FullText);
        if (!match.Success)
        {
            await SendMessageAsync(context.ChatId, """
                ❌ <b>Неверный формат</b>

                Использование:
                <code>/admin rename -1234567 "Старое имя" "Новое имя"</code>
                <code>/admin rename "Старое имя" "Новое имя"</code> (все чаты)

                💡 Чтобы посмотреть имена: <code>/admin names -1234567</code>
                """, ct);
            return true;
        }

        long? targetChatId = null;
        if (!string.IsNullOrEmpty(match.Groups[1].Value))
        {
            targetChatId = long.Parse(match.Groups[1].Value);
        }

        var oldName = match.Groups[2].Value;
        var newName = match.Groups[3].Value;

        var statusMsg = await Bot.SendMessage(
            chatId: context.ChatId,
            text: "⏳ Переименовываю сообщения...",
            cancellationToken: ct);

        var messagesAffected = await messageStore.RenameDisplayNameAsync(targetChatId, oldName, newName);

        await Bot.EditMessageText(
            chatId: context.ChatId,
            messageId: statusMsg.MessageId,
            text: "⏳ Переименовываю эмбеддинги...",
            cancellationToken: ct);

        var embeddingsAffected = await embeddingService.RenameInEmbeddingsAsync(targetChatId, oldName, newName, ct);

        var scope = targetChatId.HasValue ? $"в чате {targetChatId}" : "во всех чатах";

        await Bot.EditMessageText(
            chatId: context.ChatId,
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
}
