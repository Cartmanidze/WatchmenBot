using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using WatchmenBot.Features.Profile.Services;
using WatchmenBot.Features.Search.Services;
using WatchmenBot.Models;
using WatchmenBot.Features.Messages.Services;
using WatchmenBot.Features.Admin.Services;

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

public class SaveMessageHandler(
    MessageStore messageStore,
    EmbeddingService embeddingService,
    ProfileQueueService profileQueueService,
    LogCollector logCollector,
    ILogger<SaveMessageHandler> logger)
{
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
            await messageStore.SaveAsync(record);

            // Save chat info (title, type)
            await messageStore.SaveChatAsync(
                message.Chat.Id,
                message.Chat.Title,
                message.Chat.Type.ToString().ToLowerInvariant());

            logger.LogDebug("[DB] Saved msg #{MessageId} to chat {ChatId}",
                message.MessageId, message.Chat.Id);

            // Queue message for profile analysis (fire-and-forget)
            // Skip forwarded messages - they don't represent the user's own thoughts
            if (!string.IsNullOrWhiteSpace(record.Text) && record.Text.Length >= 20 && !record.IsForwarded)
            {
                _ = profileQueueService.EnqueueMessageAsync(
                    record.ChatId, record.Id, record.FromUserId, record.DisplayName, record.Text);
            }

            // Create embedding immediately with timeout (don't block webhook too long)
            // Skip short messages (< 10 chars) - they add noise to embeddings
            if (!string.IsNullOrWhiteSpace(record.Text) && record.Text.Length > 10)
            {
                try
                {
                    // Use separate timeout token to avoid cancellation when webhook completes
                    using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await embeddingService.StoreMessageEmbeddingAsync(record, timeoutCts.Token);
                    logCollector.IncrementEmbeddings();
                    logger.LogDebug("[Embedding] Created embedding for msg #{MessageId}", message.MessageId);
                }
                catch (OperationCanceledException)
                {
                    logger.LogWarning("[Embedding] Timeout creating embedding for msg #{MessageId} - will be picked up by background service", message.MessageId);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "[Embedding] Failed to create embedding for msg #{MessageId} - will retry in background", message.MessageId);
                }
            }

            return SaveMessageResponse.Success(message.MessageId, message.Chat.Id);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save message {MessageId} from chat {ChatId}",
                request.Message.MessageId, request.Message.Chat.Id);
            return SaveMessageResponse.Error($"Failed to save message: {ex.Message}");
        }
    }

    private static MessageRecord CreateMessageRecord(Message message)
    {
        var record = new MessageRecord
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

        // Extract forward information if present
        ExtractForwardInfo(message, record);

        return record;
    }

    /// <summary>
    /// Extract forward origin information from Telegram message
    /// </summary>
    private static void ExtractForwardInfo(Message message, MessageRecord record)
    {
        if (message.ForwardOrigin == null)
            return;

        record.IsForwarded = true;
        record.ForwardDate = message.ForwardOrigin.Date;

        switch (message.ForwardOrigin)
        {
            case Telegram.Bot.Types.MessageOriginUser userOrigin:
                record.ForwardOriginType = "user";
                record.ForwardFromId = userOrigin.SenderUser.Id;
                record.ForwardFromName = string.Join(' ', new[]
                    {
                        userOrigin.SenderUser.FirstName,
                        userOrigin.SenderUser.LastName
                    }.Where(x => !string.IsNullOrWhiteSpace(x)));
                break;

            case Telegram.Bot.Types.MessageOriginChannel channelOrigin:
                record.ForwardOriginType = "channel";
                record.ForwardFromId = channelOrigin.Chat.Id;
                record.ForwardFromName = channelOrigin.Chat.Title ?? channelOrigin.AuthorSignature;
                break;

            case Telegram.Bot.Types.MessageOriginChat chatOrigin:
                record.ForwardOriginType = "chat";
                record.ForwardFromId = chatOrigin.SenderChat.Id;
                record.ForwardFromName = chatOrigin.SenderChat.Title ?? chatOrigin.AuthorSignature;
                break;

            case Telegram.Bot.Types.MessageOriginHiddenUser hiddenOrigin:
                record.ForwardOriginType = "hidden_user";
                record.ForwardFromName = hiddenOrigin.SenderUserName;
                break;
        }
    }
}