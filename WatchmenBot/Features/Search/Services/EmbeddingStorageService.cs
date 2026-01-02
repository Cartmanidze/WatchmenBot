using System.Text;
using System.Text.Json;
using Dapper;
using WatchmenBot.Features.Search.Models;
using WatchmenBot.Infrastructure.Database;
using WatchmenBot.Models;
using WatchmenBot.Infrastructure.Settings;
using WatchmenBot.Features.Messages.Services;
using WatchmenBot.Features.Admin.Services;
using WatchmenBot.Features.Webhook.Services;

namespace WatchmenBot.Features.Search.Services;

/// <summary>
/// Service for storing and managing embeddings in the database.
/// Handles all CRUD operations for message_embeddings table.
/// </summary>
public class EmbeddingStorageService(
    EmbeddingClient embeddingClient,
    IDbConnectionFactory connectionFactory,
    ILogger<EmbeddingStorageService> logger)
{
    /// <summary>
    /// Stores embedding for a single message
    /// </summary>
    public async Task StoreMessageEmbeddingAsync(MessageRecord message, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(message.Text))
            return;

        try
        {
            var text = FormatMessageForEmbedding(message);
            var embedding = await embeddingClient.GetEmbeddingAsync(text, ct);

            if (embedding.Length == 0)
            {
                logger.LogWarning("Empty embedding returned for message {MessageId}", message.Id);
                return;
            }

            await StoreEmbeddingAsync(
                message.ChatId,
                message.Id,
                0,
                text,
                embedding,
                new { message.FromUserId, message.Username, message.DisplayName, message.DateUtc },
                ct);

            logger.LogDebug("Stored embedding for message {MessageId} in chat {ChatId}", message.Id, message.ChatId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to store embedding for message {MessageId}", message.Id);
        }
    }

    /// <summary>
    /// Batch store embeddings for multiple messages.
    /// Groups consecutive messages from the same author (within 5 min) into single embeddings.
    /// </summary>
    public async Task StoreMessageEmbeddingsBatchAsync(IEnumerable<MessageRecord> messages, CancellationToken ct = default)
    {
        var messageList = messages
            .Where(m => !string.IsNullOrWhiteSpace(m.Text) && m.Text.Length > 5)
            .OrderBy(m => m.DateUtc)
            .ToList();

        if (messageList.Count == 0)
            return;

        try
        {
            // Group consecutive messages from the same author
            var groups = GroupConsecutiveMessages(messageList);

            logger.LogDebug("[Embeddings] Grouped {Original} messages into {Grouped} chunks",
                messageList.Count, groups.Count);

            var texts = groups.Select(g => FormatGroupForEmbedding(g)).ToList();
            var embeddings = await embeddingClient.GetEmbeddingsAsync(texts, ct);

            // Prepare batch data
            var batchData = new List<(long ChatId, long MessageId, int ChunkIndex, string ChunkText, float[] Embedding, string MetadataJson)>();

            for (var i = 0; i < groups.Count && i < embeddings.Count; i++)
            {
                var group = groups[i];
                var embedding = embeddings[i];

                if (embedding.Length == 0) continue;

                var firstMsg = group.First();
                var lastMsg = group.Last();
                var metadata = new
                {
                    firstMsg.FromUserId,
                    firstMsg.Username,
                    firstMsg.DisplayName,
                    firstMsg.DateUtc,
                    EndDateUtc = lastMsg.DateUtc,
                    MessageCount = group.Count,
                    MessageIds = group.Select(m => m.Id).ToArray()
                };
                var metadataJson = JsonSerializer.Serialize(metadata);

                // Use first message ID as the primary key
                batchData.Add((firstMsg.ChatId, firstMsg.Id, 0, texts[i], embedding, metadataJson));
            }

            // Batch insert to DB
            if (batchData.Count > 0)
            {
                await StoreBatchEmbeddingsAsync(batchData, ct);
            }

            logger.LogDebug("[Embeddings] Stored {Stored} embeddings from {Original} messages",
                batchData.Count, messageList.Count);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Embeddings] Failed to store batch of {Count} messages", messageList.Count);
            throw;
        }
    }

    /// <summary>
    /// Delete embeddings for a chat
    /// </summary>
    public async Task DeleteChatEmbeddingsAsync(long chatId, CancellationToken ct = default)
    {
        using var connection = await connectionFactory.CreateConnectionAsync();

        var deleted = await connection.ExecuteAsync(
            "DELETE FROM message_embeddings WHERE chat_id = @ChatId",
            new { ChatId = chatId });

        logger.LogInformation("Deleted {Count} embeddings for chat {ChatId}", deleted, chatId);
    }

    /// <summary>
    /// Delete ALL embeddings (for full reindex)
    /// </summary>
    public async Task DeleteAllEmbeddingsAsync(CancellationToken ct = default)
    {
        using var connection = await connectionFactory.CreateConnectionAsync();

        await connection.ExecuteAsync("TRUNCATE message_embeddings");

        logger.LogInformation("Deleted ALL embeddings (TRUNCATE)");
    }

    /// <summary>
    /// Replace display name in chunk_text for existing embeddings
    /// Format: "OldName: message text" (new) or "[date] OldName: " (legacy)
    /// </summary>
    public async Task<int> RenameInEmbeddingsAsync(long? chatId, string oldName, string newName, CancellationToken ct = default)
    {
        using var connection = await connectionFactory.CreateConnectionAsync();

        // Handle both new format "Name: " and legacy format "] Name: "
        var sql = chatId.HasValue
            ? """
              UPDATE message_embeddings
              SET chunk_text = REPLACE(REPLACE(chunk_text, @OldPatternNew, @NewPatternNew), @OldPatternLegacy, @NewPatternLegacy),
                  metadata = CASE
                      WHEN metadata->>'DisplayName' = @OldName
                      THEN jsonb_set(metadata, '{DisplayName}', to_jsonb(@NewName::text))
                      ELSE metadata
                  END
              WHERE chat_id = @ChatId
                AND (chunk_text LIKE @LikePatternNew OR chunk_text LIKE @LikePatternLegacy OR metadata->>'DisplayName' = @OldName)
              """
            : """
              UPDATE message_embeddings
              SET chunk_text = REPLACE(REPLACE(chunk_text, @OldPatternNew, @NewPatternNew), @OldPatternLegacy, @NewPatternLegacy),
                  metadata = CASE
                      WHEN metadata->>'DisplayName' = @OldName
                      THEN jsonb_set(metadata, '{DisplayName}', to_jsonb(@NewName::text))
                      ELSE metadata
                  END
              WHERE chunk_text LIKE @LikePatternNew OR chunk_text LIKE @LikePatternLegacy OR metadata->>'DisplayName' = @OldName
              """;

        var affected = await connection.ExecuteAsync(sql, new
        {
            ChatId = chatId,
            OldName = oldName,
            NewName = newName,
            // New format: starts with "Name: "
            OldPatternNew = $"{oldName}: ",
            NewPatternNew = $"{newName}: ",
            LikePatternNew = $"{oldName}: %",
            // Legacy format: "] Name: "
            OldPatternLegacy = $"] {oldName}: ",
            NewPatternLegacy = $"] {newName}: ",
            LikePatternLegacy = $"%] {oldName}: %"
        });

        logger.LogInformation("Renamed '{OldName}' to '{NewName}' in {Count} embeddings (chatId: {ChatId})",
            oldName, newName, affected, chatId?.ToString() ?? "all");

        return affected;
    }

    /// <summary>
    /// Get embedding statistics for a chat
    /// </summary>
    public async Task<EmbeddingStats> GetStatsAsync(long chatId, CancellationToken ct = default)
    {
        using var connection = await connectionFactory.CreateConnectionAsync();

        var stats = await connection.QuerySingleOrDefaultAsync<EmbeddingStats>(
            """
            SELECT
                COUNT(*) as TotalEmbeddings,
                MIN(created_at) as OldestEmbedding,
                MAX(created_at) as NewestEmbedding
            FROM message_embeddings
            WHERE chat_id = @ChatId
            """,
            new { ChatId = chatId });

        return stats ?? new EmbeddingStats();
    }

    #region Private Methods

    /// <summary>
    /// Store a single embedding to the database
    /// </summary>
    private async Task StoreEmbeddingAsync(
        long chatId,
        long messageId,
        int chunkIndex,
        string chunkText,
        float[] embedding,
        object metadata,
        CancellationToken ct)
    {
        using var connection = await connectionFactory.CreateConnectionAsync();

        var embeddingString = "[" + string.Join(",", embedding) + "]";
        var metadataJson = JsonSerializer.Serialize(metadata);

        await connection.ExecuteAsync(
            """
            INSERT INTO message_embeddings (chat_id, message_id, chunk_index, chunk_text, embedding, metadata)
            VALUES (@ChatId, @MessageId, @ChunkIndex, @ChunkText, @Embedding::vector, @Metadata::jsonb)
            ON CONFLICT (chat_id, message_id, chunk_index)
            DO UPDATE SET
                chunk_text = EXCLUDED.chunk_text,
                embedding = EXCLUDED.embedding,
                metadata = EXCLUDED.metadata,
                created_at = NOW()
            """,
            new
            {
                ChatId = chatId,
                MessageId = messageId,
                ChunkIndex = chunkIndex,
                ChunkText = chunkText,
                Embedding = embeddingString,
                Metadata = metadataJson
            });
    }

    /// <summary>
    /// Batch store embeddings using a single SQL INSERT with multiple VALUES
    /// </summary>
    private async Task StoreBatchEmbeddingsAsync(
        List<(long ChatId, long MessageId, int ChunkIndex, string ChunkText, float[] Embedding, string MetadataJson)> batch,
        CancellationToken ct)
    {
        using var connection = await connectionFactory.CreateConnectionAsync();

        // Build batch insert SQL with VALUES
        var sb = new StringBuilder();
        sb.AppendLine("""
            INSERT INTO message_embeddings (chat_id, message_id, chunk_index, chunk_text, embedding, metadata)
            VALUES
            """);

        var parameters = new DynamicParameters();
        for (var i = 0; i < batch.Count; i++)
        {
            var (chatId, messageId, chunkIndex, chunkText, embedding, metadataJson) = batch[i];

            if (i > 0) sb.Append(',');
            sb.AppendLine($"(@ChatId{i}, @MessageId{i}, @ChunkIndex{i}, @ChunkText{i}, @Embedding{i}::vector, @Metadata{i}::jsonb)");

            parameters.Add($"ChatId{i}", chatId);
            parameters.Add($"MessageId{i}", messageId);
            parameters.Add($"ChunkIndex{i}", chunkIndex);
            parameters.Add($"ChunkText{i}", chunkText);
            parameters.Add($"Embedding{i}", "[" + string.Join(",", embedding) + "]");
            parameters.Add($"Metadata{i}", metadataJson);
        }

        sb.AppendLine("""
            ON CONFLICT (chat_id, message_id, chunk_index)
            DO UPDATE SET
                chunk_text = EXCLUDED.chunk_text,
                embedding = EXCLUDED.embedding,
                metadata = EXCLUDED.metadata,
                created_at = NOW()
            """);

        try
        {
            var affected = await connection.ExecuteAsync(sb.ToString(), parameters);
            logger.LogDebug("[Embeddings] Batch INSERT: {Affected} rows affected", affected);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Embeddings] Batch INSERT failed for {Count} items", batch.Count);
            throw;
        }
    }

    /// <summary>
    /// Groups consecutive messages from the same author within a time window
    /// </summary>
    private static List<List<MessageRecord>> GroupConsecutiveMessages(List<MessageRecord> messages, int maxGapMinutes = 5, int maxGroupSize = 10)
    {
        var groups = new List<List<MessageRecord>>();
        if (messages.Count == 0) return groups;

        var currentGroup = new List<MessageRecord> { messages[0] };

        for (var i = 1; i < messages.Count; i++)
        {
            var prev = messages[i - 1];
            var curr = messages[i];

            var sameAuthor = prev.FromUserId == curr.FromUserId && prev.FromUserId != 0;
            var sameChat = prev.ChatId == curr.ChatId;
            var withinTimeWindow = (curr.DateUtc - prev.DateUtc).TotalMinutes <= maxGapMinutes;
            var groupNotFull = currentGroup.Count < maxGroupSize;

            if (sameAuthor && sameChat && withinTimeWindow && groupNotFull)
            {
                currentGroup.Add(curr);
            }
            else
            {
                groups.Add(currentGroup);
                currentGroup = [curr];
            }
        }

        groups.Add(currentGroup);
        return groups;
    }

    /// <summary>
    /// Format a group of messages for embedding
    /// </summary>
    private static string FormatGroupForEmbedding(List<MessageRecord> group)
    {
        var first = group.First();
        var name = !string.IsNullOrWhiteSpace(first.DisplayName)
            ? first.DisplayName
            : !string.IsNullOrWhiteSpace(first.Username)
                ? first.Username
                : first.FromUserId.ToString();

        if (group.Count == 1)
        {
            return $"{name}: {first.Text}";
        }

        // Multiple messages - combine with newlines
        var combinedText = string.Join("\n", group.Select(m => m.Text));
        return $"{name}: {combinedText}";
    }

    /// <summary>
    /// Format a single message for embedding
    /// </summary>
    private static string FormatMessageForEmbedding(MessageRecord message)
    {
        // Format: "Name: message text" (без даты — она в metadata)
        var name = !string.IsNullOrWhiteSpace(message.DisplayName)
            ? message.DisplayName
            : !string.IsNullOrWhiteSpace(message.Username)
                ? message.Username
                : message.FromUserId.ToString();

        return $"{name}: {message.Text}";
    }

    #endregion
}
