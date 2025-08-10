using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using WatchmenBot.Models;

namespace WatchmenBot.Services;

public class TelegramBotRunner : IHostedService
{
    private readonly ITelegramBotClient _botClient;
    private readonly MessageStore _messageStore;
    private CancellationTokenSource? _receiverCts;

    public TelegramBotRunner(ITelegramBotClient botClient, MessageStore messageStore)
    {
        _botClient = botClient;
        _messageStore = messageStore;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _receiverCts = new CancellationTokenSource();

        var receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = new[] { UpdateType.Message }
        };

        _botClient.StartReceiving(HandleUpdateAsync, HandleErrorAsync, receiverOptions, _receiverCts.Token);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _receiverCts?.Cancel();
        return Task.CompletedTask;
    }

    private Task HandleErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    private async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {
        if (update.Message is not { } message) return;
        if (message.Chat.Type is not ChatType.Group and not ChatType.Supergroup) return;

        var record = new MessageRecord
        {
            Id = message.MessageId,
            ChatId = message.Chat.Id,
            ThreadId = message.MessageThreadId,
            FromUserId = message.From?.Id ?? 0,
            Username = message.From?.Username,
            DisplayName = string.Join(' ', new[] { message.From?.FirstName, message.From?.LastName }.Where(x => !string.IsNullOrWhiteSpace(x))),
            Text = message.Text ?? message.Caption,
            DateUtc = message.Date.ToUniversalTime(),
            HasLinks = MessageStore.DetectLinks(message.Text ?? message.Caption),
            HasMedia = message.Type is MessageType.Photo or MessageType.Video or MessageType.Document or MessageType.Audio or MessageType.Voice or MessageType.VideoNote,
            ReplyToMessageId = message.ReplyToMessage?.MessageId,
            MessageType = message.Type.ToString().ToLowerInvariant()
        };

        await _messageStore.SaveAsync(record);
    }
}