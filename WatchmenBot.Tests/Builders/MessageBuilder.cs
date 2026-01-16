using System.Text.Json;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace WatchmenBot.Tests.Builders;

/// <summary>
/// Fluent builder for creating Telegram Message objects in tests.
/// Provides readable, chainable API for constructing test messages.
/// </summary>
public class MessageBuilder
{
    private long _chatId = 12345L;
    private ChatType _chatType = ChatType.Supergroup;
    private string? _chatTitle = "Test Chat";
    private long _userId = 67890L;
    private string? _username = "testuser";
    private string _firstName = "Test";
    private string? _lastName;
    private string _text = "test message";
    private int _messageId = 1001;
    private DateTime _date = DateTime.UtcNow;
    private int? _replyToMessageId;
    private string? _replyToText;

    /// <summary>
    /// Set the chat where this message is sent.
    /// </summary>
    public MessageBuilder InChat(long chatId, ChatType type = ChatType.Supergroup)
    {
        _chatId = chatId;
        _chatType = type;
        return this;
    }

    /// <summary>
    /// Set as private chat (direct message to bot).
    /// </summary>
    public MessageBuilder InPrivateChat(long chatId)
    {
        _chatId = chatId;
        _chatType = ChatType.Private;
        _chatTitle = null; // Private chats don't have titles
        return this;
    }

    /// <summary>
    /// Set as group chat.
    /// </summary>
    public MessageBuilder InGroupChat(long chatId)
    {
        return InChat(chatId, ChatType.Group);
    }

    /// <summary>
    /// Set as supergroup chat.
    /// </summary>
    public MessageBuilder InSupergroup(long chatId)
    {
        return InChat(chatId, ChatType.Supergroup);
    }

    /// <summary>
    /// Set the chat title (for groups/supergroups).
    /// </summary>
    public MessageBuilder WithChatTitle(string title)
    {
        _chatTitle = title;
        return this;
    }

    /// <summary>
    /// Set the sender of this message.
    /// </summary>
    public MessageBuilder From(long userId, string? username = null, string? firstName = null, string? lastName = null)
    {
        _userId = userId;
        if (username != null) _username = username;
        if (firstName != null) _firstName = firstName;
        if (lastName != null) _lastName = lastName;
        return this;
    }

    /// <summary>
    /// Set the message text.
    /// </summary>
    public MessageBuilder WithText(string text)
    {
        _text = text;
        return this;
    }

    /// <summary>
    /// Set the message ID.
    /// </summary>
    public MessageBuilder WithMessageId(int id)
    {
        _messageId = id;
        return this;
    }

    /// <summary>
    /// Set the message date.
    /// </summary>
    public MessageBuilder WithDate(DateTime date)
    {
        _date = date;
        return this;
    }

    /// <summary>
    /// Set this message as a reply to another message.
    /// </summary>
    public MessageBuilder ReplyTo(int messageId, string? text = null)
    {
        _replyToMessageId = messageId;
        _replyToText = text;
        return this;
    }

    /// <summary>
    /// Build the Message object.
    /// Uses JSON serialization to properly set init-only properties in Telegram.Bot.
    /// </summary>
    public Message Build()
    {
        // Create JSON object with all properties (including read-only MessageId)
        // This approach works because Telegram.Bot uses System.Text.Json with init setters
        var messageJson = new
        {
            message_id = _messageId,
            date = new DateTimeOffset(_date).ToUnixTimeSeconds(),
            chat = new
            {
                id = _chatId,
                type = GetChatTypeString(_chatType),
                title = _chatTitle
            },
            from = new
            {
                id = _userId,
                is_bot = false,
                first_name = _firstName,
                last_name = _lastName,
                username = _username
            },
            text = _text,
            reply_to_message = _replyToMessageId.HasValue ? new
            {
                message_id = _replyToMessageId.Value,
                date = new DateTimeOffset(_date).ToUnixTimeSeconds(),
                chat = new
                {
                    id = _chatId,
                    type = GetChatTypeString(_chatType),
                    title = _chatTitle
                },
                text = _replyToText
            } : null
        };

        var json = JsonSerializer.Serialize(messageJson, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });

        return JsonSerializer.Deserialize<Message>(json, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        })!;
    }

    private static string GetChatTypeString(ChatType type) => type switch
    {
        ChatType.Private => "private",
        ChatType.Group => "group",
        ChatType.Supergroup => "supergroup",
        ChatType.Channel => "channel",
        _ => "private"
    };

    /// <summary>
    /// Create a new builder with default values.
    /// </summary>
    public static MessageBuilder Create() => new();

    /// <summary>
    /// Create a command message (e.g., "/ask question").
    /// </summary>
    public static Message Command(string command, string? args = null, long chatId = -100123456L, long userId = 67890L)
    {
        var text = string.IsNullOrEmpty(args) ? command : $"{command} {args}";
        return new MessageBuilder()
            .InSupergroup(chatId)
            .From(userId)
            .WithText(text)
            .Build();
    }
}
