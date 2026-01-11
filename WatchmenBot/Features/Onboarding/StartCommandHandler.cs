using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace WatchmenBot.Features.Onboarding;

/// <summary>
/// Handles /start command - onboarding for new users.
/// Shows welcome message with bot capabilities and "Add to group" button.
/// </summary>
public class StartCommandHandler(
    ITelegramBotClient bot,
    IConfiguration configuration,
    ILogger<StartCommandHandler> logger)
{
    private const int GroupMessageDeleteDelayMs = 15_000; // 15 seconds

    /// <summary>
    /// Handle /start command.
    /// In private chat: full onboarding with inline button.
    /// In group chat: short message that auto-deletes.
    /// </summary>
    public async Task HandleAsync(Message message, CancellationToken ct = default)
    {
        if (message.Chat.Type == ChatType.Private)
        {
            await HandlePrivateChatAsync(message, ct);
        }
        else
        {
            await HandleGroupChatAsync(message, ct);
        }
    }

    private async Task HandlePrivateChatAsync(Message message, CancellationToken ct)
    {
        var userId = message.From?.Id ?? 0;
        var userName = message.From?.Username ?? message.From?.FirstName ?? "unknown";

        logger.LogInformation("[Start] Private chat onboarding for @{User} (ID: {UserId})", userName, userId);

        var botUsername = await GetBotUsernameAsync(ct);
        var welcomeText = BuildWelcomeMessage();
        var keyboard = BuildInlineKeyboard(botUsername);

        await bot.SendMessage(
            chatId: message.Chat.Id,
            text: welcomeText,
            parseMode: ParseMode.Html,
            replyMarkup: keyboard,
            cancellationToken: ct);
    }

    private async Task HandleGroupChatAsync(Message message, CancellationToken ct)
    {
        var chatName = message.Chat.Title ?? message.Chat.Id.ToString();
        logger.LogInformation("[Start] Group chat /start in {Chat}", chatName);

        // Short message for groups - don't spam
        var shortMessage = "👋 Я готов! Команды: /summary, /ask, /smart, /truth";

        var sentMessage = await bot.SendMessage(
            chatId: message.Chat.Id,
            text: shortMessage,
            replyParameters: new ReplyParameters
            {
                MessageId = message.MessageId,
                AllowSendingWithoutReply = true
            },
            cancellationToken: ct);

        // Auto-delete after delay to not clutter the chat
        _ = DeleteMessageAfterDelayAsync(message.Chat.Id, sentMessage.MessageId, ct);
    }

    private static string BuildWelcomeMessage()
    {
        return """
            <b>👋 Привет! Я WatchmenBot</b> — бот с памятью для групповых чатов.

            Добавь меня в чат, и я буду:
            • <b>/summary</b> — делать выжимки обсуждений
            • <b>/ask</b> — отвечать на вопросы по истории чата
            • <b>/smart</b> — искать информацию в интернете
            • <b>/truth</b> — проверять факты в сообщениях

            📝 <i>Помню историю с момента добавления</i>
            ⚠️ <i>Нужны права на чтение сообщений</i>
            """;
    }

    private static InlineKeyboardMarkup BuildInlineKeyboard(string botUsername)
    {
        var addToGroupUrl = $"https://t.me/{botUsername}?startgroup=welcome";

        return new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithUrl("➕ Добавить в чат", addToGroupUrl)
            }
        });
    }

    private async Task<string> GetBotUsernameAsync(CancellationToken ct)
    {
        // Try config first (faster)
        var configUsername = configuration["Telegram:BotUsername"];
        if (!string.IsNullOrEmpty(configUsername))
        {
            return configUsername.TrimStart('@');
        }

        // Fallback to API call
        var me = await bot.GetMe(ct);
        return me.Username ?? "WatchmenBot";
    }

    private async Task DeleteMessageAfterDelayAsync(long chatId, int messageId, CancellationToken ct)
    {
        try
        {
            await Task.Delay(GroupMessageDeleteDelayMs, ct);
            await bot.DeleteMessage(chatId, messageId, ct);
        }
        catch (OperationCanceledException)
        {
            // App shutting down, ignore
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[Start] Failed to delete message {MessageId} in {ChatId}", messageId, chatId);
        }
    }
}