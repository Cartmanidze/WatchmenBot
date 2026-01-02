using System.Text;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using WatchmenBot.Services;

namespace WatchmenBot.Features.Search;

public class RecallHandler(
    ITelegramBotClient bot,
    MessageStore messageStore,
    ILogger<RecallHandler> logger)
{
    public async Task HandleAsync(Message message, CancellationToken ct)
    {
        var chatId = message.Chat.Id;
        var username = ParseUsername(message.Text);

        if (string.IsNullOrWhiteSpace(username))
        {
            await bot.SendMessage(
                chatId: chatId,
                text: "Использование: <code>/recall @username</code>\n\nПример: <code>/recall @ivan</code>",
                parseMode: ParseMode.Html,
                replyParameters: new ReplyParameters { MessageId = message.MessageId },
                cancellationToken: ct);
            return;
        }

        try
        {
            await bot.SendChatAction(chatId, ChatAction.Typing, cancellationToken: ct);

            logger.LogInformation("[Recall] Username: {Username} in chat {ChatId}", username, chatId);

            // Get messages from the last 7 days
            var endUtc = DateTimeOffset.UtcNow;
            var startUtc = endUtc.AddDays(-7);

            var messages = await messageStore.GetMessagesByUsernameAsync(chatId, username, startUtc, endUtc);

            if (messages.Count == 0)
            {
                await bot.SendMessage(
                    chatId: chatId,
                    text: $"Не нашёл сообщений от <b>@{EscapeHtml(username)}</b> за последнюю неделю.",
                    parseMode: ParseMode.Html,
                    replyParameters: new ReplyParameters { MessageId = message.MessageId },
                    cancellationToken: ct);
                return;
            }

            var response = FormatResponse(username, messages);

            try
            {
                await bot.SendMessage(
                    chatId: chatId,
                    text: response,
                    parseMode: ParseMode.Html,
                    linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true },
                    replyParameters: new ReplyParameters { MessageId = message.MessageId },
                    cancellationToken: ct);
            }
            catch (Telegram.Bot.Exceptions.ApiRequestException)
            {
                // Fallback to plain text
                var plainText = System.Text.RegularExpressions.Regex.Replace(response, "<[^>]+>", "");
                await bot.SendMessage(
                    chatId: chatId,
                    text: plainText,
                    replyParameters: new ReplyParameters { MessageId = message.MessageId },
                    cancellationToken: ct);
            }

            logger.LogInformation("[Recall] Found {Count} messages for @{Username}", messages.Count, username);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Recall] Failed for username: {Username}", username);

            await bot.SendMessage(
                chatId: chatId,
                text: "Произошла ошибка при поиске сообщений. Попробуйте позже.",
                replyParameters: new ReplyParameters { MessageId = message.MessageId },
                cancellationToken: ct);
        }
    }

    private static string ParseUsername(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var spaceIndex = text.IndexOf(' ');
        if (spaceIndex < 0)
            return string.Empty;

        var username = text[(spaceIndex + 1)..].Trim();

        // Remove @ if present
        if (username.StartsWith('@'))
            username = username[1..];

        return username;
    }

    private static string FormatResponse(string username, List<Models.MessageRecord> messages)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"<b>📝 Сообщения @{EscapeHtml(username)} за неделю:</b>");
        sb.AppendLine($"<i>Найдено: {messages.Count} сообщений</i>");
        sb.AppendLine();

        // Group by day
        var groupedByDay = messages
            .GroupBy(m => m.DateUtc.Date)
            .OrderByDescending(g => g.Key)
            .ToList();

        foreach (var dayGroup in groupedByDay.Take(5))
        {
            sb.AppendLine($"<b>📅 {dayGroup.Key:dd.MM.yyyy}</b>");

            var dayMessages = dayGroup.OrderBy(m => m.DateUtc).ToList();
            foreach (var msg in dayMessages.Take(10))
            {
                var time = msg.DateUtc.ToString("HH:mm");
                var text = TruncateText(msg.Text ?? "", 150);
                sb.AppendLine($"  <code>{time}</code> {EscapeHtml(text)}");
            }

            if (dayMessages.Count > 10)
            {
                sb.AppendLine($"  <i>...и ещё {dayMessages.Count - 10} сообщений</i>");
            }

            sb.AppendLine();
        }

        if (groupedByDay.Count > 5)
        {
            sb.AppendLine($"<i>...и ещё {groupedByDay.Count - 5} дней с сообщениями</i>");
        }

        return sb.ToString();
    }

    private static string TruncateText(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            return text;
        return text[..(maxLength - 3)] + "...";
    }

    private static string EscapeHtml(string text)
    {
        return text
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");
    }
}
