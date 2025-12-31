using Dapper;
using WatchmenBot.Infrastructure.Database;

namespace WatchmenBot.Services.Embeddings;

/// <summary>
/// Service for retrieving and merging context windows around messages.
/// Provides surrounding conversation context for search results.
/// </summary>
public class ContextWindowService
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<ContextWindowService> _logger;

    public ContextWindowService(
        IDbConnectionFactory connectionFactory,
        ILogger<ContextWindowService> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    /// <summary>
    /// Get context window around a specific message (N messages before + N after)
    /// </summary>
    public async Task<List<ContextMessage>> GetContextWindowAsync(
        long chatId,
        long messageId,
        int windowSize = 2,
        CancellationToken ct = default)
    {
        try
        {
            using var connection = await _connectionFactory.CreateConnectionAsync();

            // Get the target message's timestamp first
            var targetTime = await connection.ExecuteScalarAsync<DateTime?>(
                "SELECT date_utc FROM messages WHERE chat_id = @ChatId AND id = @MessageId",
                new { ChatId = chatId, MessageId = messageId });

            if (targetTime == null)
                return new List<ContextMessage>();

            // Get messages before (including target)
            var before = await connection.QueryAsync<ContextMessage>(
                """
                SELECT
                    id as MessageId,
                    chat_id as ChatId,
                    from_user_id as FromUserId,
                    COALESCE(display_name, username, from_user_id::text) as Author,
                    text as Text,
                    date_utc as DateUtc
                FROM messages
                WHERE chat_id = @ChatId
                  AND date_utc <= @TargetTime
                  AND text IS NOT NULL AND text != ''
                ORDER BY date_utc DESC
                LIMIT @Limit
                """,
                new { ChatId = chatId, TargetTime = targetTime, Limit = windowSize + 1 });

            // Get messages after
            var after = await connection.QueryAsync<ContextMessage>(
                """
                SELECT
                    id as MessageId,
                    chat_id as ChatId,
                    from_user_id as FromUserId,
                    COALESCE(display_name, username, from_user_id::text) as Author,
                    text as Text,
                    date_utc as DateUtc
                FROM messages
                WHERE chat_id = @ChatId
                  AND date_utc > @TargetTime
                  AND text IS NOT NULL AND text != ''
                ORDER BY date_utc ASC
                LIMIT @Limit
                """,
                new { ChatId = chatId, TargetTime = targetTime, Limit = windowSize });

            // Combine and sort chronologically
            var window = before.Reverse().Concat(after).ToList();

            _logger.LogDebug("[ContextWindow] MsgId={Id} → {Count} messages in window", messageId, window.Count);

            return window;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get context window for message {MessageId}", messageId);
            return new List<ContextMessage>();
        }
    }

    /// <summary>
    /// Get context windows for multiple messages, merging overlapping windows.
    /// This creates cohesive conversation threads from scattered search results.
    /// </summary>
    public async Task<List<ContextWindow>> GetMergedContextWindowsAsync(
        long chatId,
        List<long> messageIds,
        int windowSize = 2,
        CancellationToken ct = default)
    {
        if (messageIds.Count == 0)
            return new List<ContextWindow>();

        var allWindows = new List<ContextWindow>();

        foreach (var msgId in messageIds.Distinct().Take(10)) // Limit to top 10
        {
            var messages = await GetContextWindowAsync(chatId, msgId, windowSize, ct);
            if (messages.Count > 0)
            {
                allWindows.Add(new ContextWindow
                {
                    CenterMessageId = msgId,
                    Messages = messages
                });
            }
        }

        // Merge overlapping windows
        var merged = MergeOverlappingWindows(allWindows);

        _logger.LogInformation("[ContextWindows] {Input} messages → {Windows} windows → {Merged} merged",
            messageIds.Count, allWindows.Count, merged.Count);

        return merged;
    }

    /// <summary>
    /// Merge windows that share messages (by MessageId).
    /// This creates longer coherent conversation threads.
    /// </summary>
    private static List<ContextWindow> MergeOverlappingWindows(List<ContextWindow> windows)
    {
        if (windows.Count <= 1)
            return windows;

        var merged = new List<ContextWindow>();
        var used = new HashSet<int>();

        for (var i = 0; i < windows.Count; i++)
        {
            if (used.Contains(i))
                continue;

            var current = windows[i];
            var currentIds = current.Messages.Select(m => m.MessageId).ToHashSet();

            // Find all overlapping windows
            for (var j = i + 1; j < windows.Count; j++)
            {
                if (used.Contains(j))
                    continue;

                var other = windows[j];
                var otherIds = other.Messages.Select(m => m.MessageId).ToHashSet();

                // Check for overlap
                if (currentIds.Overlaps(otherIds))
                {
                    // Merge: add all messages from other, dedupe, re-sort
                    var allMessages = current.Messages
                        .Concat(other.Messages)
                        .DistinctBy(m => m.MessageId)
                        .OrderBy(m => m.DateUtc)
                        .ToList();

                    current = new ContextWindow
                    {
                        CenterMessageId = current.CenterMessageId,
                        Messages = allMessages
                    };
                    currentIds = allMessages.Select(m => m.MessageId).ToHashSet();
                    used.Add(j);
                }
            }

            merged.Add(current);
            used.Add(i);
        }

        return merged;
    }
}

