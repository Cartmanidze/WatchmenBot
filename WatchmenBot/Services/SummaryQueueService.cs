using System.Threading.Channels;
using Telegram.Bot.Types;

namespace WatchmenBot.Services;

/// <summary>
/// Запрос на генерацию summary в фоне
/// </summary>
public class SummaryQueueItem
{
    public required long ChatId { get; init; }
    public required int ReplyToMessageId { get; init; }
    public required int Hours { get; init; }
    public required string RequestedBy { get; init; }
    public DateTimeOffset RequestedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Сервис очереди для фоновой генерации summary.
/// Использует in-memory Channel для быстрой обработки.
/// </summary>
public class SummaryQueueService(ILogger<SummaryQueueService> logger)
{
    private readonly Channel<SummaryQueueItem> _channel = Channel.CreateBounded<SummaryQueueItem>(new BoundedChannelOptions(100)
    {
        FullMode = BoundedChannelFullMode.DropOldest // При переполнении удаляем старые
    });

    // Bounded channel с ограничением на 100 запросов в очереди
    // При переполнении удаляем старые

    /// <summary>
    /// Добавить запрос на summary в очередь
    /// </summary>
    private bool Enqueue(SummaryQueueItem item)
    {
        if (_channel.Writer.TryWrite(item))
        {
            logger.LogInformation("[SummaryQueue] Enqueued summary request for chat {ChatId}, {Hours}h, by @{User}",
                item.ChatId, item.Hours, item.RequestedBy);
            return true;
        }

        logger.LogWarning("[SummaryQueue] Failed to enqueue - queue is full");
        return false;
    }

    /// <summary>
    /// Добавить запрос из Telegram Message
    /// </summary>
    public bool EnqueueFromMessage(Message message, int hours)
    {
        var userName = message.From?.Username ?? message.From?.FirstName ?? "unknown";

        return Enqueue(new SummaryQueueItem
        {
            ChatId = message.Chat.Id,
            ReplyToMessageId = message.MessageId,
            Hours = hours,
            RequestedBy = userName
        });
    }

    /// <summary>
    /// Получить reader для обработки очереди (для BackgroundService)
    /// </summary>
    public ChannelReader<SummaryQueueItem> Reader => _channel.Reader;

    /// <summary>
    /// Количество элементов в очереди (примерное)
    /// </summary>
    public int Count => _channel.Reader.Count;
}