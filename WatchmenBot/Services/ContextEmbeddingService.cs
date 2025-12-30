using System.Text;
using Dapper;
using WatchmenBot.Infrastructure.Database;

namespace WatchmenBot.Services;

/// <summary>
/// Service for context-aware embeddings using sliding windows.
/// Instead of embedding isolated messages, embeds conversation windows (10 messages).
/// This preserves conversational context like "Да, согласен" → meaningful in context.
/// </summary>
public class ContextEmbeddingService
{
    private readonly EmbeddingClient _embeddingClient;
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<ContextEmbeddingService> _logger;

    // Window configuration
    private const int MinWindowSize = 5;   // Minimum messages per window
    private const int MaxWindowSize = 15;  // Maximum messages per window
    private const int WindowStep = 3;      // Step between windows within large dialogs
    private const int DialogGapMinutes = 30; // Time gap to consider new dialog

    public ContextEmbeddingService(
        EmbeddingClient embeddingClient,
        IDbConnectionFactory connectionFactory,
        ILogger<ContextEmbeddingService> logger)
    {
        _embeddingClient = embeddingClient;
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    /// <summary>
    /// Build and store context embeddings for a chat.
    /// Uses sliding window approach: 10 messages per window, step 3.
    /// </summary>
    public async Task BuildContextEmbeddingsAsync(
        long chatId,
        int batchSize = 50,
        CancellationToken ct = default)
    {
        try
        {
            using var connection = await _connectionFactory.CreateConnectionAsync();

            // Get the last processed center message ID to continue from there
            var lastProcessedId = await connection.ExecuteScalarAsync<long?>(
                "SELECT MAX(center_message_id) FROM context_embeddings WHERE chat_id = @ChatId",
                new { ChatId = chatId });

            _logger.LogInformation("[ContextEmb] Building for chat {ChatId}, starting after message {LastId}",
                chatId, lastProcessedId ?? 0);

            // Get messages that haven't been processed as centers yet
            var messages = await GetMessagesForWindowsAsync(connection, chatId, lastProcessedId, batchSize * WindowStep, ct);

            if (messages.Count < MinWindowSize)
            {
                _logger.LogInformation("[ContextEmb] Not enough messages ({Count}) for a window in chat {ChatId}",
                    messages.Count, chatId);
                return;
            }

            var windows = BuildSlidingWindows(messages);
            _logger.LogInformation("[ContextEmb] Built {Count} windows from {Messages} messages",
                windows.Count, messages.Count);

            var processedCount = 0;
            foreach (var window in windows)
            {
                ct.ThrowIfCancellationRequested();

                // Check if already exists
                var exists = await connection.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS(SELECT 1 FROM context_embeddings WHERE chat_id = @ChatId AND center_message_id = @CenterId)",
                    new { ChatId = chatId, CenterId = window.CenterMessageId });

                if (exists)
                    continue;

                // Build context text
                var contextText = FormatWindowForEmbedding(window);

                // Get embedding
                var embedding = await _embeddingClient.GetEmbeddingAsync(contextText, ct);
                if (embedding.Length == 0)
                {
                    _logger.LogWarning("[ContextEmb] Empty embedding for window centered at {CenterId}", window.CenterMessageId);
                    continue;
                }

                // Store
                await StoreContextEmbeddingAsync(connection, chatId, window, contextText, embedding, ct);
                processedCount++;

                if (processedCount % 10 == 0)
                {
                    _logger.LogDebug("[ContextEmb] Processed {Count} windows", processedCount);
                }
            }

            _logger.LogInformation("[ContextEmb] Completed: stored {Count} new context embeddings for chat {ChatId}",
                processedCount, chatId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ContextEmb] Failed to build context embeddings for chat {ChatId}", chatId);
            throw;
        }
    }

    /// <summary>
    /// Search in context embeddings for a query.
    /// Returns windows that match the query semantically.
    /// </summary>
    public async Task<List<ContextSearchResult>> SearchContextAsync(
        long chatId,
        string query,
        int limit = 10,
        CancellationToken ct = default)
    {
        try
        {
            using var connection = await _connectionFactory.CreateConnectionAsync();

            // Check if we have any context embeddings
            var count = await connection.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM context_embeddings WHERE chat_id = @ChatId",
                new { ChatId = chatId });

            if (count == 0)
            {
                _logger.LogWarning("[ContextEmb] No context embeddings for chat {ChatId}", chatId);
                return new List<ContextSearchResult>();
            }

            // Get query embedding
            var queryEmbedding = await _embeddingClient.GetEmbeddingAsync(query, ct);
            if (queryEmbedding.Length == 0)
            {
                _logger.LogWarning("[ContextEmb] Failed to get embedding for query: {Query}", query);
                return new List<ContextSearchResult>();
            }

            var embeddingString = "[" + string.Join(",", queryEmbedding) + "]";

            var results = await connection.QueryAsync<ContextSearchResult>(
                """
                SELECT
                    id as Id,
                    chat_id as ChatId,
                    center_message_id as CenterMessageId,
                    window_start_id as WindowStartId,
                    window_end_id as WindowEndId,
                    message_ids as MessageIds,
                    context_text as ContextText,
                    embedding <=> @Embedding::vector as Distance,
                    1 - (embedding <=> @Embedding::vector) as Similarity
                FROM context_embeddings
                WHERE chat_id = @ChatId
                ORDER BY embedding <=> @Embedding::vector
                LIMIT @Limit
                """,
                new { ChatId = chatId, Embedding = embeddingString, Limit = limit });

            var resultList = results.ToList();

            _logger.LogInformation("[ContextEmb] Search '{Query}' in chat {ChatId}: {Count} results, best sim={Best:F3}",
                TruncateForLog(query, 30), chatId, resultList.Count,
                resultList.Count > 0 ? resultList[0].Similarity : 0);

            return resultList;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ContextEmb] Search failed for query: {Query}", query);
            return new List<ContextSearchResult>();
        }
    }

    /// <summary>
    /// Get messages for building windows, ordered by date
    /// </summary>
    private async Task<List<WindowMessage>> GetMessagesForWindowsAsync(
        System.Data.IDbConnection connection,
        long chatId,
        long? afterMessageId,
        int limit,
        CancellationToken ct)
    {
        var sql = afterMessageId.HasValue
            ? """
                SELECT
                    id as MessageId,
                    chat_id as ChatId,
                    from_user_id as FromUserId,
                    COALESCE(display_name, username, from_user_id::text) as Author,
                    text as Text,
                    date_utc as DateUtc
                FROM messages
                WHERE chat_id = @ChatId
                  AND id > @AfterId
                  AND text IS NOT NULL
                  AND LENGTH(text) > 5
                ORDER BY date_utc ASC
                LIMIT @Limit
                """
            : """
                SELECT
                    id as MessageId,
                    chat_id as ChatId,
                    from_user_id as FromUserId,
                    COALESCE(display_name, username, from_user_id::text) as Author,
                    text as Text,
                    date_utc as DateUtc
                FROM messages
                WHERE chat_id = @ChatId
                  AND text IS NOT NULL
                  AND LENGTH(text) > 5
                ORDER BY date_utc ASC
                LIMIT @Limit
                """;

        var messages = await connection.QueryAsync<WindowMessage>(sql, new
        {
            ChatId = chatId,
            AfterId = afterMessageId ?? 0,
            Limit = limit
        });

        return messages.ToList();
    }

    /// <summary>
    /// Build dialog-aware windows from messages.
    /// Detects dialog boundaries by time gaps (>30 min = new dialog).
    /// Small dialogs become single windows, large ones use sliding windows.
    /// </summary>
    private List<MessageWindow> BuildSlidingWindows(List<WindowMessage> messages)
    {
        var windows = new List<MessageWindow>();

        if (messages.Count < MinWindowSize)
            return windows;

        // Segment messages into dialogs by time gaps
        var dialogs = SegmentIntoDialogs(messages);

        _logger.LogDebug("[ContextEmb] Segmented {Messages} messages into {Dialogs} dialogs",
            messages.Count, dialogs.Count);

        foreach (var dialog in dialogs)
        {
            if (dialog.Count < MinWindowSize)
            {
                // Dialog too small, skip (will be included in adjacent windows)
                continue;
            }

            if (dialog.Count <= MaxWindowSize)
            {
                // Small dialog: create single window with all messages
                var centerIndex = dialog.Count / 2;
                var centerMessage = dialog[centerIndex];

                windows.Add(new MessageWindow
                {
                    CenterMessageId = centerMessage.MessageId,
                    WindowStartId = dialog.First().MessageId,
                    WindowEndId = dialog.Last().MessageId,
                    Messages = dialog
                });
            }
            else
            {
                // Large dialog: use sliding windows within it
                for (var i = 0; i + MaxWindowSize <= dialog.Count; i += WindowStep)
                {
                    var windowMessages = dialog.Skip(i).Take(MaxWindowSize).ToList();
                    var centerIndex = MaxWindowSize / 2;
                    var centerMessage = windowMessages[centerIndex];

                    windows.Add(new MessageWindow
                    {
                        CenterMessageId = centerMessage.MessageId,
                        WindowStartId = windowMessages.First().MessageId,
                        WindowEndId = windowMessages.Last().MessageId,
                        Messages = windowMessages
                    });
                }

                // Handle remaining messages at the end of large dialog
                var remaining = dialog.Count % WindowStep;
                if (remaining >= MinWindowSize)
                {
                    var windowMessages = dialog.TakeLast(MaxWindowSize).ToList();
                    var centerIndex = windowMessages.Count / 2;
                    var centerMessage = windowMessages[centerIndex];

                    windows.Add(new MessageWindow
                    {
                        CenterMessageId = centerMessage.MessageId,
                        WindowStartId = windowMessages.First().MessageId,
                        WindowEndId = windowMessages.Last().MessageId,
                        Messages = windowMessages
                    });
                }
            }
        }

        return windows;
    }

    /// <summary>
    /// Segment messages into dialogs by time gaps.
    /// A gap of >30 minutes indicates a new dialog.
    /// </summary>
    private List<List<WindowMessage>> SegmentIntoDialogs(List<WindowMessage> messages)
    {
        var dialogs = new List<List<WindowMessage>>();
        var currentDialog = new List<WindowMessage>();

        foreach (var msg in messages)
        {
            if (currentDialog.Count == 0)
            {
                currentDialog.Add(msg);
                continue;
            }

            var lastMsg = currentDialog.Last();
            var gap = msg.DateUtc - lastMsg.DateUtc;

            if (gap.TotalMinutes > DialogGapMinutes)
            {
                // Time gap detected, start new dialog
                if (currentDialog.Count > 0)
                    dialogs.Add(currentDialog);

                currentDialog = new List<WindowMessage> { msg };
            }
            else
            {
                currentDialog.Add(msg);
            }
        }

        // Don't forget the last dialog
        if (currentDialog.Count > 0)
            dialogs.Add(currentDialog);

        return dialogs;
    }

    /// <summary>
    /// Format a window for embedding - preserves conversation flow
    /// </summary>
    private static string FormatWindowForEmbedding(MessageWindow window)
    {
        var sb = new StringBuilder();

        foreach (var msg in window.Messages)
        {
            // Format: "Author: message text"
            // No timestamps - they add noise to semantic meaning
            sb.AppendLine($"{msg.Author}: {msg.Text}");
        }

        return sb.ToString().Trim();
    }

    /// <summary>
    /// Store a context embedding
    /// </summary>
    private async Task StoreContextEmbeddingAsync(
        System.Data.IDbConnection connection,
        long chatId,
        MessageWindow window,
        string contextText,
        float[] embedding,
        CancellationToken ct)
    {
        var embeddingString = "[" + string.Join(",", embedding) + "]";
        var messageIds = window.Messages.Select(m => m.MessageId).ToArray();

        await connection.ExecuteAsync(
            """
            INSERT INTO context_embeddings
                (chat_id, center_message_id, window_start_id, window_end_id, message_ids, context_text, embedding, window_size)
            VALUES
                (@ChatId, @CenterId, @StartId, @EndId, @MessageIds, @ContextText, @Embedding::vector, @WindowSize)
            ON CONFLICT (chat_id, center_message_id) DO UPDATE SET
                window_start_id = EXCLUDED.window_start_id,
                window_end_id = EXCLUDED.window_end_id,
                message_ids = EXCLUDED.message_ids,
                context_text = EXCLUDED.context_text,
                embedding = EXCLUDED.embedding,
                created_at = NOW()
            """,
            new
            {
                ChatId = chatId,
                CenterId = window.CenterMessageId,
                StartId = window.WindowStartId,
                EndId = window.WindowEndId,
                MessageIds = messageIds,
                ContextText = contextText,
                Embedding = embeddingString,
                WindowSize = window.Messages.Count // Dynamic window size
            });
    }

    /// <summary>
    /// Get statistics about context embeddings
    /// </summary>
    public async Task<ContextEmbeddingStats> GetStatsAsync(long chatId, CancellationToken ct = default)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync();

        var stats = await connection.QuerySingleOrDefaultAsync<ContextEmbeddingStats>(
            """
            SELECT
                COUNT(*) as TotalWindows,
                MIN(created_at) as OldestWindow,
                MAX(created_at) as NewestWindow,
                AVG(window_size) as AvgWindowSize
            FROM context_embeddings
            WHERE chat_id = @ChatId
            """,
            new { ChatId = chatId });

        return stats ?? new ContextEmbeddingStats();
    }

    /// <summary>
    /// Delete all context embeddings for a chat (for reindexing)
    /// </summary>
    public async Task DeleteChatContextEmbeddingsAsync(long chatId, CancellationToken ct = default)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync();

        var deleted = await connection.ExecuteAsync(
            "DELETE FROM context_embeddings WHERE chat_id = @ChatId",
            new { ChatId = chatId });

        _logger.LogInformation("[ContextEmb] Deleted {Count} context embeddings for chat {ChatId}", deleted, chatId);
    }

    /// <summary>
    /// Delete ALL context embeddings (for full reindexing)
    /// </summary>
    public async Task DeleteAllContextEmbeddingsAsync(CancellationToken ct = default)
    {
        using var connection = await _connectionFactory.CreateConnectionAsync();

        var deleted = await connection.ExecuteAsync("DELETE FROM context_embeddings");

        _logger.LogInformation("[ContextEmb] Deleted ALL {Count} context embeddings", deleted);
    }

    /// <summary>
    /// Get context windows that contain specific message IDs
    /// </summary>
    public async Task<List<ContextSearchResult>> GetContextWindowsByMessageIdsAsync(
        long chatId,
        List<long> messageIds,
        int limit = 10,
        CancellationToken ct = default)
    {
        if (messageIds.Count == 0)
            return new List<ContextSearchResult>();

        try
        {
            using var connection = await _connectionFactory.CreateConnectionAsync();

            // Find windows that contain any of the target message IDs
            var results = await connection.QueryAsync<ContextSearchResult>(
                """
                SELECT
                    id as Id,
                    chat_id as ChatId,
                    center_message_id as CenterMessageId,
                    window_start_id as WindowStartId,
                    window_end_id as WindowEndId,
                    message_ids as MessageIds,
                    context_text as ContextText,
                    1.0 as Similarity,
                    0.0 as Distance
                FROM context_embeddings
                WHERE chat_id = @ChatId
                  AND message_ids && @MessageIds
                ORDER BY center_message_id DESC
                LIMIT @Limit
                """,
                new { ChatId = chatId, MessageIds = messageIds.ToArray(), Limit = limit });

            var resultsList = results.ToList();

            _logger.LogInformation(
                "[ContextEmb] Found {Count} context windows containing {MessageCount} target messages in chat {ChatId}",
                resultsList.Count, messageIds.Count, chatId);

            return resultsList;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ContextEmb] Failed to get context windows for message IDs in chat {ChatId}", chatId);
            return new List<ContextSearchResult>();
        }
    }

    private static string TruncateForLog(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            return text;
        return text[..(maxLength - 3)] + "...";
    }
}

/// <summary>
/// Internal model for building windows
/// </summary>
public class WindowMessage
{
    public long MessageId { get; set; }
    public long ChatId { get; set; }
    public long FromUserId { get; set; }
    public string Author { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public DateTime DateUtc { get; set; }
}

/// <summary>
/// A sliding window of messages
/// </summary>
public class MessageWindow
{
    public long CenterMessageId { get; set; }
    public long WindowStartId { get; set; }
    public long WindowEndId { get; set; }
    public List<WindowMessage> Messages { get; set; } = new();
}

/// <summary>
/// Search result from context embeddings
/// </summary>
public class ContextSearchResult
{
    public long Id { get; set; }
    public long ChatId { get; set; }
    public long CenterMessageId { get; set; }
    public long WindowStartId { get; set; }
    public long WindowEndId { get; set; }
    public long[] MessageIds { get; set; } = Array.Empty<long>();
    public string ContextText { get; set; } = string.Empty;
    public double Similarity { get; set; }
    public double Distance { get; set; }
}

/// <summary>
/// Statistics about context embeddings
/// </summary>
public class ContextEmbeddingStats
{
    public int TotalWindows { get; set; }
    public DateTimeOffset? OldestWindow { get; set; }
    public DateTimeOffset? NewestWindow { get; set; }
    public double AvgWindowSize { get; set; }
}
