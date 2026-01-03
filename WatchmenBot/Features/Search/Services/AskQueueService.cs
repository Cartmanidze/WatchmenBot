using System.Threading.Channels;
using Telegram.Bot.Types;

namespace WatchmenBot.Features.Search.Services;

/// <summary>
/// Request for background /ask processing
/// </summary>
public class AskQueueItem
{
    public required long ChatId { get; init; }
    public required int ReplyToMessageId { get; init; }
    public required string Question { get; init; }
    public required string Command { get; init; } // "ask" or "smart"
    public required long AskerId { get; init; }
    public required string AskerName { get; init; }
    public string? AskerUsername { get; init; }
    public DateTimeOffset RequestedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Queue service for background /ask processing.
/// Decouples webhook response from LLM processing to avoid Telegram timeout.
/// </summary>
public class AskQueueService(ILogger<AskQueueService> logger)
{
    private readonly Channel<AskQueueItem> _channel = Channel.CreateBounded<AskQueueItem>(
        new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });

    /// <summary>
    /// Enqueue /ask request for background processing
    /// </summary>
    public bool Enqueue(AskQueueItem item)
    {
        if (_channel.Writer.TryWrite(item))
        {
            logger.LogInformation("[AskQueue] Enqueued /{Command} from @{User}: {Question}",
                item.Command, item.AskerUsername ?? item.AskerName,
                item.Question.Length > 50 ? item.Question[..50] + "..." : item.Question);
            return true;
        }

        logger.LogWarning("[AskQueue] Failed to enqueue - queue is full");
        return false;
    }

    /// <summary>
    /// Enqueue from Telegram Message
    /// </summary>
    public bool EnqueueFromMessage(Message message, string command, string question)
    {
        var askerName = message.From?.FirstName ?? message.From?.Username ?? "Unknown";
        var askerUsername = message.From?.Username;
        var askerId = message.From?.Id ?? 0;

        return Enqueue(new AskQueueItem
        {
            ChatId = message.Chat.Id,
            ReplyToMessageId = message.MessageId,
            Question = question,
            Command = command,
            AskerId = askerId,
            AskerName = askerName,
            AskerUsername = askerUsername
        });
    }

    /// <summary>
    /// Channel reader for BackgroundService
    /// </summary>
    public ChannelReader<AskQueueItem> Reader => _channel.Reader;

    /// <summary>
    /// Approximate queue size
    /// </summary>
    public int Count => _channel.Reader.Count;
}
