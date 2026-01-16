using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace WatchmenBot.Tests.Builders;

/// <summary>
/// Fluent builder for creating Telegram Update objects in tests.
/// Update is the wrapper object that Telegram sends for all events.
/// </summary>
public class UpdateBuilder
{
    private int _updateId = 1;
    private Message? _message;
    private Message? _editedMessage;
    private CallbackQuery? _callbackQuery;

    /// <summary>
    /// Set the update ID.
    /// </summary>
    public UpdateBuilder WithUpdateId(int id)
    {
        _updateId = id;
        return this;
    }

    /// <summary>
    /// Set a new message in this update.
    /// </summary>
    public UpdateBuilder WithMessage(Message message)
    {
        _message = message;
        _editedMessage = null;
        return this;
    }

    /// <summary>
    /// Set a new message using a builder.
    /// </summary>
    public UpdateBuilder WithMessage(Action<MessageBuilder> configure)
    {
        var builder = new MessageBuilder();
        configure(builder);
        _message = builder.Build();
        _editedMessage = null;
        return this;
    }

    /// <summary>
    /// Set an edited message in this update.
    /// </summary>
    public UpdateBuilder WithEditedMessage(Message message)
    {
        _editedMessage = message;
        _message = null;
        return this;
    }

    /// <summary>
    /// Set an edited message using a builder.
    /// </summary>
    public UpdateBuilder WithEditedMessage(Action<MessageBuilder> configure)
    {
        var builder = new MessageBuilder();
        configure(builder);
        _editedMessage = builder.Build();
        _message = null;
        return this;
    }

    /// <summary>
    /// Set a callback query (inline button press) in this update.
    /// </summary>
    public UpdateBuilder WithCallbackQuery(CallbackQuery query)
    {
        _callbackQuery = query;
        return this;
    }

    /// <summary>
    /// Set a callback query with specified data.
    /// </summary>
    public UpdateBuilder WithCallbackQuery(string data, long userId = 67890L, string username = "testuser")
    {
        _callbackQuery = new CallbackQuery
        {
            Id = "callback_" + _updateId,
            From = new User
            {
                Id = userId,
                Username = username,
                FirstName = "Test"
            },
            Data = data,
            ChatInstance = "test_instance"
        };
        return this;
    }

    /// <summary>
    /// Build the Update object.
    /// </summary>
    public Update Build()
    {
        return new Update
        {
            Id = _updateId,
            Message = _message,
            EditedMessage = _editedMessage,
            CallbackQuery = _callbackQuery
        };
    }

    /// <summary>
    /// Create a new builder with default values.
    /// </summary>
    public static UpdateBuilder Create() => new();

    /// <summary>
    /// Create an update with a text message.
    /// </summary>
    public static Update TextMessage(string text, long chatId = -100123456L, long userId = 67890L)
    {
        return new UpdateBuilder()
            .WithMessage(new MessageBuilder()
                .InSupergroup(chatId)
                .From(userId)
                .WithText(text)
                .Build())
            .Build();
    }

    /// <summary>
    /// Create an update with a command message.
    /// </summary>
    public static Update Command(string command, string? args = null, long chatId = -100123456L, long userId = 67890L)
    {
        return new UpdateBuilder()
            .WithMessage(MessageBuilder.Command(command, args, chatId, userId))
            .Build();
    }

    /// <summary>
    /// Create an update with a private message (DM to bot).
    /// </summary>
    public static Update PrivateMessage(string text, long userId = 67890L)
    {
        return new UpdateBuilder()
            .WithMessage(new MessageBuilder()
                .InPrivateChat(userId)
                .From(userId)
                .WithText(text)
                .Build())
            .Build();
    }
}
