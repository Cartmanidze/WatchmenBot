using Hangfire;
using Telegram.Bot;
using Telegram.Bot.Types;
using WatchmenBot.Extensions;
using WatchmenBot.Features.Admin.Services;
using WatchmenBot.Features.Search.Jobs;
using WatchmenBot.Features.Search.Services;

namespace WatchmenBot.Features.Search;

/// <summary>
/// Handler for /truth command - enqueues fact-check requests for background processing.
/// Actual processing is done by TruthJob → TruthProcessingService.
/// </summary>
public class FactCheckHandler(
    ITelegramBotClient bot,
    ChatStatusService chatStatusService,
    IBackgroundJobClient jobClient,
    ILogger<FactCheckHandler> logger)
{
    /// <summary>
    /// Handle /truth command - enqueue fact-check for last N messages
    /// </summary>
    public async Task HandleAsync(Message message, CancellationToken ct)
    {
        var chatId = message.Chat.Id;

        // Parse optional count from command (default 5)
        var count = ParseCount(message.Text, defaultCount: 5, maxCount: 15);

        logger.LogInformation("[TRUTH] Enqueueing fact-check for last {Count} messages in chat {ChatId}", count, chatId);

        // Create queue item for Hangfire job
        var item = new TruthQueueItem
        {
            ChatId = chatId,
            ReplyToMessageId = message.MessageId,
            MessageCount = count,
            RequestedBy = message.From?.Username ?? message.From?.FirstName ?? "Unknown"
        };

        // Enqueue for background processing via Hangfire
        jobClient.Enqueue<TruthJob>(job => job.ProcessAsync(item, CancellationToken.None));

        // Send acknowledgment (safe: handles 403)
        try
        {
            await bot.SendMessageSafeAsync(
                chatStatusService,
                chatId,
                $"🔍 Проверяю последние {count} сообщений на факты...",
                logger,
                replyToMessageId: message.MessageId,
                ct: ct);
        }
        catch (ChatDeactivatedException)
        {
            // Chat was deactivated - job will handle it
        }
    }

    private static int ParseCount(string? text, int defaultCount, int maxCount)
    {
        if (string.IsNullOrWhiteSpace(text))
            return defaultCount;

        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return defaultCount;

        if (int.TryParse(parts[1], out var count) && count > 0)
            return Math.Min(count, maxCount);

        return defaultCount;
    }
}
