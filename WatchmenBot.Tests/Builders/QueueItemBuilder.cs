using WatchmenBot.Features.Search.Services;
using WatchmenBot.Features.Summary.Services;

namespace WatchmenBot.Tests.Builders;

/// <summary>
/// Fluent builder for creating AskQueueItem objects in tests.
/// Used for testing /ask and /smart command processing pipeline.
/// </summary>
public class AskQueueItemBuilder
{
    private long _chatId = -100123456L;
    private int _replyToMessageId = 1001;
    private string _question = "тестовый вопрос";
    private string _command = "ask";
    private long _askerId = 67890L;
    private string _askerName = "Test User";
    private string? _askerUsername = "testuser";
    private int _attemptCount;

    public AskQueueItemBuilder ForChat(long chatId)
    {
        _chatId = chatId;
        return this;
    }

    public AskQueueItemBuilder ReplyTo(int messageId)
    {
        _replyToMessageId = messageId;
        return this;
    }

    public AskQueueItemBuilder WithQuestion(string question)
    {
        _question = question;
        return this;
    }

    /// <summary>
    /// Set command to "ask" (default).
    /// </summary>
    public AskQueueItemBuilder AsAskCommand()
    {
        _command = "ask";
        return this;
    }

    /// <summary>
    /// Set command to "smart" (Perplexity search).
    /// </summary>
    public AskQueueItemBuilder AsSmartCommand()
    {
        _command = "smart";
        return this;
    }

    public AskQueueItemBuilder FromUser(long userId, string name, string? username = null)
    {
        _askerId = userId;
        _askerName = name;
        _askerUsername = username;
        return this;
    }

    public AskQueueItemBuilder WithAttemptCount(int count)
    {
        _attemptCount = count;
        return this;
    }

    public AskQueueItem Build()
    {
        return new AskQueueItem
        {
            ChatId = _chatId,
            ReplyToMessageId = _replyToMessageId,
            Question = _question,
            Command = _command,
            AskerId = _askerId,
            AskerName = _askerName,
            AskerUsername = _askerUsername,
            AttemptCount = _attemptCount
        };
    }

    public static AskQueueItemBuilder Create() => new();

    /// <summary>
    /// Create a simple /ask queue item with question.
    /// </summary>
    public static AskQueueItem Ask(string question, long chatId = -100123456L)
    {
        return new AskQueueItemBuilder()
            .ForChat(chatId)
            .WithQuestion(question)
            .Build();
    }

    /// <summary>
    /// Create a /smart queue item with question.
    /// </summary>
    public static AskQueueItem Smart(string question, long chatId = -100123456L)
    {
        return new AskQueueItemBuilder()
            .ForChat(chatId)
            .WithQuestion(question)
            .AsSmartCommand()
            .Build();
    }
}

/// <summary>
/// Fluent builder for creating TruthQueueItem objects in tests.
/// Used for testing /truth fact-checking pipeline.
/// </summary>
public class TruthQueueItemBuilder
{
    private long _chatId = -100123456L;
    private int _replyToMessageId = 1001;
    private int _messageCount = 5;
    private string _requestedBy = "testuser";
    private int _attemptCount;

    public TruthQueueItemBuilder ForChat(long chatId)
    {
        _chatId = chatId;
        return this;
    }

    public TruthQueueItemBuilder ReplyTo(int messageId)
    {
        _replyToMessageId = messageId;
        return this;
    }

    public TruthQueueItemBuilder WithMessageCount(int count)
    {
        _messageCount = count;
        return this;
    }

    public TruthQueueItemBuilder RequestedBy(string username)
    {
        _requestedBy = username;
        return this;
    }

    public TruthQueueItemBuilder WithAttemptCount(int count)
    {
        _attemptCount = count;
        return this;
    }

    public TruthQueueItem Build()
    {
        return new TruthQueueItem
        {
            ChatId = _chatId,
            ReplyToMessageId = _replyToMessageId,
            MessageCount = _messageCount,
            RequestedBy = _requestedBy,
            AttemptCount = _attemptCount
        };
    }

    public static TruthQueueItemBuilder Create() => new();

    /// <summary>
    /// Create a simple /truth queue item.
    /// </summary>
    public static TruthQueueItem Default(long chatId = -100123456L, int messageCount = 5)
    {
        return new TruthQueueItemBuilder()
            .ForChat(chatId)
            .WithMessageCount(messageCount)
            .Build();
    }
}

/// <summary>
/// Fluent builder for creating SummaryQueueItem objects in tests.
/// Used for testing /summary generation pipeline.
/// </summary>
public class SummaryQueueItemBuilder
{
    private long _chatId = -100123456L;
    private int _replyToMessageId = 1001;
    private int _hours = 24;
    private string _requestedBy = "testuser";
    private int _attemptCount;

    public SummaryQueueItemBuilder ForChat(long chatId)
    {
        _chatId = chatId;
        return this;
    }

    public SummaryQueueItemBuilder ReplyTo(int messageId)
    {
        _replyToMessageId = messageId;
        return this;
    }

    public SummaryQueueItemBuilder ForHours(int hours)
    {
        _hours = hours;
        return this;
    }

    public SummaryQueueItemBuilder RequestedBy(string username)
    {
        _requestedBy = username;
        return this;
    }

    public SummaryQueueItemBuilder WithAttemptCount(int count)
    {
        _attemptCount = count;
        return this;
    }

    public SummaryQueueItem Build()
    {
        return new SummaryQueueItem
        {
            ChatId = _chatId,
            ReplyToMessageId = _replyToMessageId,
            Hours = _hours,
            RequestedBy = _requestedBy,
            AttemptCount = _attemptCount
        };
    }

    public static SummaryQueueItemBuilder Create() => new();

    /// <summary>
    /// Create a simple /summary queue item for last N hours.
    /// </summary>
    public static SummaryQueueItem ForLastHours(int hours, long chatId = -100123456L)
    {
        return new SummaryQueueItemBuilder()
            .ForChat(chatId)
            .ForHours(hours)
            .Build();
    }
}
