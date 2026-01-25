using Hangfire;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using WatchmenBot.Extensions;
using WatchmenBot.Features.Admin.Services;
using WatchmenBot.Features.Search.Jobs;
using WatchmenBot.Features.Search.Services;
using WatchmenBot.Infrastructure.Queue;

namespace WatchmenBot.Features.Search;

/// <summary>
/// Handler for /ask and /smart commands.
/// Enqueues requests for background processing via Hangfire.
/// Actual processing is done by AskJob → AskProcessingService.
/// </summary>
public class AskHandler(
    ITelegramBotClient bot,
    ChatStatusService chatStatusService,
    IBackgroundJobClient jobClient,
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

        // Create queue item for Hangfire job
        var item = new AskQueueItem
        {
            ChatId = chatId,
            ReplyToMessageId = message.MessageId,
            Question = question,
            Command = command,
            AskerId = message.From?.Id ?? 0,
            AskerName = message.From?.FirstName ?? message.From?.Username ?? "Unknown",
            AskerUsername = message.From?.Username
        };

        // Enqueue for background processing via Hangfire (avoids Telegram webhook timeout)
        jobClient.Enqueue<AskJob>(job => job.ProcessAsync(item, CancellationToken.None));

        logger.LogInformation("[{Command}] Enqueued via Hangfire: {Question} in chat {ChatId}",
            command.ToUpper(), question, chatId);

        // Send typing indicator (safe: ignores if chat deactivated)
        await bot.TrySendChatActionAsync(chatStatusService, chatId, ChatAction.Typing, logger, ct);
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

        // Send help (safe: handles 403 and HTML fallback)
        try
        {
            await bot.SendHtmlMessageSafeAsync(
                chatStatusService,
                chatId,
                helpText,
                logger,
                replyToMessageId: messageId,
                ct: ct);
        }
        catch (ChatDeactivatedException)
        {
            // Chat was deactivated - silently ignore
        }
    }
}
