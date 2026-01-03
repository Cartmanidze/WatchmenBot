using System.Text;
using Dapper;
using WatchmenBot.Features.Search.Models;
using WatchmenBot.Infrastructure.Database;

namespace WatchmenBot.Features.Search.Services;

/// <summary>
/// Service for context-aware embeddings using sliding windows.
/// Instead of embedding isolated messages, embeds conversation windows (10 messages).
/// This preserves conversational context like "Да, согласен" → meaningful in context.
/// </summary>
public class ContextEmbeddingService(
    EmbeddingClient embeddingClient,
    IDbConnectionFactory connectionFactory,
    ILogger<ContextEmbeddingService> logger)
{
    // Window configuration
    private const int MinWindowSize = 5;
    private const int MaxWindowSize = 15;
    private const int WindowStep = 3;
    private const int DialogGapMinutes = 30;

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
            using var connection = await connectionFactory.CreateConnectionAsync();

            var lastProcessedId = await connection.ExecuteScalarAsync<long?>(
                "SELECT MAX(center_message_id) FROM context_embeddings WHERE chat_id = @ChatId",
                new { ChatId = chatId });

            logger.LogInformation("[ContextEmb] Building for chat {ChatId}, starting after message {LastId}",
                chatId, lastProcessedId ?? 0);

            var messages = await GetMessagesForWindowsAsync(connection, chatId, lastProcessedId, batchSize * WindowStep, ct);

            if (messages.Count < MinWindowSize)
            {
                logger.LogInformation("[ContextEmb] Not enough messages ({Count}) for a window in chat {ChatId}",
                    messages.Count, chatId);
                return;
            }

            var windows = BuildSlidingWindows(messages);
            logger.LogInformation("[ContextEmb] Built {Count} windows from {Messages} messages",
                windows.Count, messages.Count);

            // Filter out windows that already exist
            var existingCenterIds = await connection.QueryAsync<long>(
                "SELECT center_message_id FROM context_embeddings WHERE chat_id = @ChatId",
                new { ChatId = chatId });

            var existingSet = existingCenterIds.ToHashSet();
            var newWindows = windows.Where(w => !existingSet.Contains(w.CenterMessageId)).ToList();

            if (newWindows.Count == 0)
            {
                logger.LogInformation("[ContextEmb] No new windows to process for chat {ChatId}", chatId);
                return;
            }

            logger.LogInformation("[ContextEmb] Processing {NewCount} new windows (skipping {ExistingCount} existing)",
                newWindows.Count, windows.Count - newWindows.Count);

            // Batch processing with late chunking for better context preservation
            var windowTexts = newWindows.Select(FormatWindowForEmbedding).ToList();

            // Use late chunking to preserve cross-window context (Jina AI feature)
            var embeddings = await embeddingClient.GetEmbeddingsAsync(
                windowTexts,
                EmbeddingTask.RetrievalPassage,
                lateChunking: true,
                ct);

            // Store all embeddings
            var processedCount = 0;
            for (var i = 0; i < newWindows.Count && i < embeddings.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                var window = newWindows[i];
                var embedding = embeddings[i];
                var contextText = windowTexts[i];

                if (embedding.Length == 0)
                {
                    logger.LogWarning("[ContextEmb] Empty embedding for window centered at {CenterId}", window.CenterMessageId);
                    continue;
                }

                await StoreContextEmbeddingAsync(connection, chatId, window, contextText, embedding, ct);
                processedCount++;
            }

            logger.LogInformation("[ContextEmb] Completed: stored {Count} new context embeddings for chat {ChatId}",
                processedCount, chatId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[ContextEmb] Failed to build context embeddings for chat {ChatId}", chatId);
            throw;
        }
    }

    // Hybrid search weights: 70% semantic, 30% keyword
    private const double DenseWeight = 0.7;
    private const double SparseWeight = 0.3;

    /// <summary>
    /// Search in context embeddings for a query using hybrid BM25 + vector search.
    /// Returns windows that match the query semantically and by keywords.
    /// </summary>
    public async Task<List<ContextSearchResult>> SearchContextAsync(
        long chatId,
        string query,
        int limit = 10,
        CancellationToken ct = default)
    {
        try
        {
            using var connection = await connectionFactory.CreateConnectionAsync();

            var count = await connection.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM context_embeddings WHERE chat_id = @ChatId",
                new { ChatId = chatId });

            if (count == 0)
            {
                logger.LogWarning("[ContextEmb] No context embeddings for chat {ChatId}", chatId);
                return [];
            }

            var queryEmbedding = await embeddingClient.GetEmbeddingAsync(query, EmbeddingTask.RetrievalQuery, ct);
            if (queryEmbedding.Length == 0)
            {
                logger.LogWarning("[ContextEmb] Failed to get embedding for query: {Query}", query);
                return [];
            }

            var embeddingString = "[" + string.Join(",", queryEmbedding) + "]";

            // Extract search terms for BM25 component
            var searchTerms = ExtractSearchTerms(query);
            var useHybrid = !string.IsNullOrWhiteSpace(searchTerms);

            // Hybrid search: combine vector similarity (70%) + BM25 text ranking (30%)
            var sql = useHybrid
                ? $"""
                    SELECT
                        id as Id,
                        chat_id as ChatId,
                        center_message_id as CenterMessageId,
                        window_start_id as WindowStartId,
                        window_end_id as WindowEndId,
                        message_ids as MessageIds,
                        context_text as ContextText,
                        embedding <=> @Embedding::vector as Distance,
                        {DenseWeight} * (1 - (embedding <=> @Embedding::vector))
                        + {SparseWeight} * COALESCE(
                            ts_rank_cd(
                                to_tsvector('russian', context_text),
                                websearch_to_tsquery('russian', @SearchTerms),
                                32
                            ),
                            0
                        ) as Similarity
                    FROM context_embeddings
                    WHERE chat_id = @ChatId
                    ORDER BY Similarity DESC
                    LIMIT @Limit
                    """
                : """
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
                    """;

            var results = await connection.QueryAsync<ContextSearchResult>(
                sql,
                new { ChatId = chatId, Embedding = embeddingString, SearchTerms = searchTerms, Limit = limit });

            logger.LogDebug("[ContextEmb] Hybrid={Hybrid}, Terms='{Terms}'", useHybrid, searchTerms ?? "none");

            var resultList = results.ToList();

            logger.LogInformation("[ContextEmb] Search '{Query}' in chat {ChatId}: {Count} results, best sim={Best:F3}",
                TruncateForLog(query, 30), chatId, resultList.Count,
                resultList.Count > 0 ? resultList[0].Similarity : 0);

            return resultList;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[ContextEmb] Search failed for query: {Query}", query);
            return [];
        }
    }

    /// <summary>
    /// Search in context embeddings within a specific time range using hybrid BM25 + vector.
    /// Used for /summary to ensure only messages from the target period are included.
    /// </summary>
    public async Task<List<ContextSearchResult>> SearchContextInRangeAsync(
        long chatId,
        string query,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        int limit = 10,
        CancellationToken ct = default)
    {
        try
        {
            using var connection = await connectionFactory.CreateConnectionAsync();

            var queryEmbedding = await embeddingClient.GetEmbeddingAsync(query, EmbeddingTask.RetrievalQuery, ct);
            if (queryEmbedding.Length == 0)
            {
                logger.LogWarning("[ContextEmb] Failed to get embedding for query: {Query}", query);
                return [];
            }

            var embeddingString = "[" + string.Join(",", queryEmbedding) + "]";

            // Extract search terms for BM25 component
            var searchTerms = ExtractSearchTerms(query);
            var useHybrid = !string.IsNullOrWhiteSpace(searchTerms);

            // Hybrid search: combine vector similarity (70%) + BM25 text ranking (30%)
            var sql = useHybrid
                ? $"""
                    SELECT
                        ce.id as Id,
                        ce.chat_id as ChatId,
                        ce.center_message_id as CenterMessageId,
                        ce.window_start_id as WindowStartId,
                        ce.window_end_id as WindowEndId,
                        ce.message_ids as MessageIds,
                        ce.context_text as ContextText,
                        ce.embedding <=> @Embedding::vector as Distance,
                        {DenseWeight} * (1 - (ce.embedding <=> @Embedding::vector))
                        + {SparseWeight} * COALESCE(
                            ts_rank_cd(
                                to_tsvector('russian', ce.context_text),
                                websearch_to_tsquery('russian', @SearchTerms),
                                32
                            ),
                            0
                        ) as Similarity
                    FROM context_embeddings ce
                    JOIN messages m ON ce.chat_id = m.chat_id AND ce.center_message_id = m.id
                    WHERE ce.chat_id = @ChatId
                      AND m.date_utc >= @StartUtc
                      AND m.date_utc < @EndUtc
                    ORDER BY Similarity DESC
                    LIMIT @Limit
                    """
                : """
                    SELECT
                        ce.id as Id,
                        ce.chat_id as ChatId,
                        ce.center_message_id as CenterMessageId,
                        ce.window_start_id as WindowStartId,
                        ce.window_end_id as WindowEndId,
                        ce.message_ids as MessageIds,
                        ce.context_text as ContextText,
                        ce.embedding <=> @Embedding::vector as Distance,
                        1 - (ce.embedding <=> @Embedding::vector) as Similarity
                    FROM context_embeddings ce
                    JOIN messages m ON ce.chat_id = m.chat_id AND ce.center_message_id = m.id
                    WHERE ce.chat_id = @ChatId
                      AND m.date_utc >= @StartUtc
                      AND m.date_utc < @EndUtc
                    ORDER BY ce.embedding <=> @Embedding::vector
                    LIMIT @Limit
                    """;

            var results = await connection.QueryAsync<ContextSearchResult>(
                sql,
                new { ChatId = chatId, Embedding = embeddingString, SearchTerms = searchTerms, StartUtc = startUtc.UtcDateTime, EndUtc = endUtc.UtcDateTime, Limit = limit });

            var resultList = results.ToList();

            logger.LogInformation("[ContextEmb] SearchInRange '{Query}' in chat {ChatId}: {Count} results, best sim={Best:F3}, hybrid={Hybrid}",
                TruncateForLog(query, 30), chatId, resultList.Count,
                resultList.Count > 0 ? resultList[0].Similarity : 0, useHybrid);

            return resultList;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[ContextEmb] SearchInRange failed for query: {Query}", query);
            return [];
        }
    }

    /// <summary>
    /// Get statistics about context embeddings
    /// </summary>
    public async Task<ContextEmbeddingStats> GetStatsAsync(long chatId, CancellationToken ct = default)
    {
        using var connection = await connectionFactory.CreateConnectionAsync();

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
        using var connection = await connectionFactory.CreateConnectionAsync();

        var deleted = await connection.ExecuteAsync(
            "DELETE FROM context_embeddings WHERE chat_id = @ChatId",
            new { ChatId = chatId });

        logger.LogInformation("[ContextEmb] Deleted {Count} context embeddings for chat {ChatId}", deleted, chatId);
    }

    /// <summary>
    /// Delete ALL context embeddings (for full reindexing)
    /// </summary>
    public async Task DeleteAllContextEmbeddingsAsync(CancellationToken ct = default)
    {
        using var connection = await connectionFactory.CreateConnectionAsync();

        var deleted = await connection.ExecuteAsync("DELETE FROM context_embeddings");

        logger.LogInformation("[ContextEmb] Deleted ALL {Count} context embeddings", deleted);
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
            return [];

        try
        {
            using var connection = await connectionFactory.CreateConnectionAsync();

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

            logger.LogInformation(
                "[ContextEmb] Found {Count} context windows containing {MessageCount} target messages in chat {ChatId}",
                resultsList.Count, messageIds.Count, chatId);

            return resultsList;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[ContextEmb] Failed to get context windows for message IDs in chat {ChatId}", chatId);
            return [];
        }
    }

    #region Private Helpers

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

    private List<MessageWindow> BuildSlidingWindows(List<WindowMessage> messages)
    {
        var windows = new List<MessageWindow>();

        if (messages.Count < MinWindowSize)
            return windows;

        var dialogs = SegmentIntoDialogs(messages);

        logger.LogDebug("[ContextEmb] Segmented {Messages} messages into {Dialogs} dialogs",
            messages.Count, dialogs.Count);

        foreach (var dialog in dialogs)
        {
            if (dialog.Count < MinWindowSize)
                continue;

            if (dialog.Count <= MaxWindowSize)
            {
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

    // Topic shift markers - words that often indicate a new topic
    private static readonly HashSet<string> TopicShiftMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        "кстати", "а вообще", "кста", "другая тема", "сменим тему", "кстате",
        "btw", "anyway", "by the way", "а ещё", "а еще", "ну и ещё",
        "совсем другое", "вопрос", "вопросик", "хотел спросить"
    };

    /// <summary>
    /// Segment messages into dialogs using topic-aware boundaries:
    /// 1. Time gaps > 30 min (strong boundary)
    /// 2. Topic shift markers in text (soft boundary, requires 5+ messages)
    /// 3. Participant pattern changes (monologue → group shift)
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

            // Strong boundary: time gap > 30 minutes
            if (gap.TotalMinutes > DialogGapMinutes)
            {
                if (currentDialog.Count > 0)
                    dialogs.Add(currentDialog);

                currentDialog = [msg];
                continue;
            }

            // Soft boundary: topic shift marker (only if dialog is substantial)
            if (currentDialog.Count >= 5 && HasTopicShiftMarker(msg.Text))
            {
                dialogs.Add(currentDialog);
                currentDialog = [msg];
                continue;
            }

            // Soft boundary: participant pattern shift (monologue → group discussion)
            if (currentDialog.Count >= 8 && IsParticipantPatternShift(currentDialog, msg))
            {
                dialogs.Add(currentDialog);
                currentDialog = [msg];
                continue;
            }

            currentDialog.Add(msg);
        }

        if (currentDialog.Count > 0)
            dialogs.Add(currentDialog);

        return dialogs;
    }

    /// <summary>
    /// Check if message starts with a topic shift marker
    /// </summary>
    private static bool HasTopicShiftMarker(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var lowerText = text.ToLowerInvariant().Trim();

        foreach (var marker in TopicShiftMarkers)
        {
            if (lowerText.StartsWith(marker) ||
                lowerText.Contains($" {marker}") ||
                lowerText.Contains($"{marker},"))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Detect participant pattern shift: from monologue to group discussion or vice versa.
    /// Returns true if the pattern shifts significantly.
    /// </summary>
    private static bool IsParticipantPatternShift(List<WindowMessage> dialog, WindowMessage newMsg)
    {
        // Look at last 5 messages
        var recentMessages = dialog.TakeLast(5).ToList();
        if (recentMessages.Count < 5)
            return false;

        var recentParticipants = recentMessages.Select(m => m.FromUserId).Distinct().Count();
        var newAuthorInRecent = recentMessages.Any(m => m.FromUserId == newMsg.FromUserId);

        // Monologue pattern: 1 person speaking last 5 messages
        var isMonologue = recentParticipants == 1;

        // If it was a monologue and a new person joins → potential topic shift
        if (isMonologue && !newAuthorInRecent)
        {
            return true;
        }

        return false;
    }

    private static string FormatWindowForEmbedding(MessageWindow window)
    {
        var sb = new StringBuilder();

        foreach (var msg in window.Messages)
        {
            sb.AppendLine($"{msg.Author}: {msg.Text}");
        }

        return sb.ToString().Trim();
    }

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
                WindowSize = window.Messages.Count
            });
    }

    private static string TruncateForLog(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            return text;
        return text[..(maxLength - 3)] + "...";
    }

    /// <summary>
    /// Extract meaningful search terms from a query for BM25 component
    /// </summary>
    private static string ExtractSearchTerms(string query)
        => TextSearchHelpers.ExtractSearchTerms(query);

    #endregion
}
