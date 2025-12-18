using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using WatchmenBot.Models;
using WatchmenBot.Services;

namespace WatchmenBot.Features.Messages;

public class SaveMessageRequest
{
    public required Message Message { get; init; }
}

public class SaveMessageResponse
{
    public bool IsSuccess { get; init; }
    public string? ErrorMessage { get; init; }
    public long? MessageId { get; init; }
    public long? ChatId { get; init; }
    
    public static SaveMessageResponse Success(long messageId, long chatId) => new()
    {
        IsSuccess = true,
        MessageId = messageId,
        ChatId = chatId
    };
    
    public static SaveMessageResponse Error(string errorMessage) => new()
    {
        IsSuccess = false,
        ErrorMessage = errorMessage
    };
}

public class SaveMessageHandler
{
    private readonly MessageStore _messageStore;
    private readonly EmbeddingService _embeddingService;
    private readonly LogCollector _logCollector;
    private readonly ILogger<SaveMessageHandler> _logger;

    public SaveMessageHandler(
        MessageStore messageStore,
        EmbeddingService embeddingService,
        LogCollector logCollector,
        ILogger<SaveMessageHandler> logger)
    {
        _messageStore = messageStore;
        _embeddingService = embeddingService;
        _logCollector = logCollector;
        _logger = logger;
    }

    public async Task<SaveMessageResponse> HandleAsync(SaveMessageRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var message = request.Message;

            // Only process group/supergroup messages
            if (message.Chat.Type is not ChatType.Group and not ChatType.Supergroup)
            {
                return SaveMessageResponse.Success(message.MessageId, message.Chat.Id);
            }

            var record = CreateMessageRecord(message);
            await _messageStore.SaveAsync(record);

            // Save chat info (title, type)
            await _messageStore.SaveChatAsync(
                message.Chat.Id,
                message.Chat.Title,
                message.Chat.Type.ToString().ToLowerInvariant());

            _logger.LogDebug("[DB] Saved msg #{MessageId} to chat {ChatId}",
                message.MessageId, message.Chat.Id);

            // Create embedding immediately (fire and forget, don't block message saving)
            if (!string.IsNullOrWhiteSpace(record.Text) && record.Text.Length > 5)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _embeddingService.StoreMessageEmbeddingAsync(record, cancellationToken);
                        _logCollector.IncrementEmbeddings();
                        _logger.LogDebug("[Embedding] Created embedding for msg #{MessageId}", message.MessageId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[Embedding] Failed to create embedding for msg #{MessageId}", message.MessageId);
                    }
                }, cancellationToken);
            }

            return SaveMessageResponse.Success(message.MessageId, message.Chat.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save message {MessageId} from chat {ChatId}",
                request.Message.MessageId, request.Message.Chat.Id);
            return SaveMessageResponse.Error($"Failed to save message: {ex.Message}");
        }
    }

    private static MessageRecord CreateMessageRecord(Message message)
    {
        return new MessageRecord
        {
            Id = message.MessageId,
            ChatId = message.Chat.Id,
            ThreadId = message.MessageThreadId,
            FromUserId = message.From?.Id ?? 0,
            Username = message.From?.Username,
            DisplayName = string.Join(' ', new[] { message.From?.FirstName, message.From?.LastName }
                .Where(x => !string.IsNullOrWhiteSpace(x))),
            Text = message.Text ?? message.Caption,
            DateUtc = message.Date.ToUniversalTime(),
            HasLinks = MessageStore.DetectLinks(message.Text ?? message.Caption),
            HasMedia = message.Type is MessageType.Photo or 
                      MessageType.Video or 
                      MessageType.Document or 
                      MessageType.Audio or 
                      MessageType.Voice or 
                      MessageType.VideoNote,
            ReplyToMessageId = message.ReplyToMessage?.MessageId,
            MessageType = message.Type.ToString().ToLowerInvariant()
        };
    }
}