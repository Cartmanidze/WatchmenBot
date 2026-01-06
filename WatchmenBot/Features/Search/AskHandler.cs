using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using WatchmenBot.Features.Search.Services;

namespace WatchmenBot.Features.Search;

/// <summary>
/// Handler for /ask and /smart commands.
/// Enqueues requests for background processing to avoid Telegram webhook timeout.
/// Actual processing is done by BackgroundAskWorker.
/// </summary>
public class AskHandler(
    ITelegramBotClient bot,
    AskQueueService askQueue,
    ILogger<AskHandler> logger)
{
    /// <summary>
    /// Handle /ask command (дерзкий ответ с подъёбкой)
    /// </summary>
    public Task HandleAsync(Message message, CancellationToken ct)
        => HandleAsync(message, "ask", ct);

    /// <summary>
    /// Handle /smart command (серьёзный вопрос)
    /// </summary>
    public Task HandleQuestionAsync(Message message, CancellationToken ct)
        => HandleAsync(message, "smart", ct);

    private async Task HandleAsync(Message message, string command, CancellationToken ct)
    {
        var chatId = message.Chat.Id;
        var question = AskHandlerHelpers.ParseQuestion(message.Text);

        if (string.IsNullOrWhiteSpace(question))
        {
            await SendHelpTextAsync(chatId, command, message.MessageId, ct);
            return;
        }

        // Enqueue for background processing (avoids Telegram webhook timeout)
        if (await askQueue.EnqueueFromMessageAsync(message, command, question))
        {
            logger.LogInformation("[{Command}] Enqueued: {Question} in chat {ChatId}",
                command.ToUpper(), question, chatId);

            // Send typing indicator - response will come from BackgroundAskWorker
            await bot.SendChatAction(chatId, ChatAction.Typing, cancellationToken: ct);
        }
        else
        {
            // Queue is full
            await bot.SendMessage(
                chatId: chatId,
                text: "Слишком много запросов, попробуй через минуту.",
                replyParameters: new ReplyParameters { MessageId = message.MessageId, AllowSendingWithoutReply = true },
                cancellationToken: ct);
        }
    }

    private async Task SendHelpTextAsync(long chatId, string command, int messageId, CancellationToken ct)
    {
        var helpText = command == "smart"
            ? """
                🌐 <b>Умный поиск в интернете</b>

                Задай любой вопрос — отвечу с актуальной инфой из сети:
                • <code>/smart сколько стоит биткоин?</code>
                • <code>/smart последние новости про SpaceX</code>
                • <code>/smart как приготовить борщ?</code>

                <i>Использует Perplexity для поиска</i>
                """
            : """
                🎭 <b>Вопрос по истории чата</b>

                Спроси про людей или события в чате:
                • <code>/ask что за тип этот Глеб?</code>
                • <code>/ask я гондон?</code>
                • <code>/ask о чём вчера спорили?</code>

                <i>Ищет в истории сообщений</i>
                """;

        await bot.SendMessage(
            chatId: chatId,
            text: helpText,
            parseMode: ParseMode.Html,
            replyParameters: new ReplyParameters { MessageId = messageId },
            cancellationToken: ct);
    }
}
