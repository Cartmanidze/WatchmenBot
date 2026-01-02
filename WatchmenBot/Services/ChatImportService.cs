using Dapper;
using WatchmenBot.Infrastructure.Database;

namespace WatchmenBot.Services;

/// <summary>
/// Service to import chat history from Telegram Desktop export
/// </summary>
public class ChatImportService(
    TelegramExportParser parser,
    IDbConnectionFactory connectionFactory,
    ILogger<ChatImportService> logger)
{
    /// <summary>
    /// Import messages from Telegram Desktop export directory
    /// </summary>
    public async Task<ImportResult> ImportFromDirectoryAsync(
        string directoryPath,
        long chatId,
        bool skipExisting = true,
        CancellationToken ct = default)
    {
        var result = new ImportResult { ChatId = chatId };

        try
        {
            // Parse HTML files
            logger.LogInformation("[Import] Starting import from {Path} for chat {ChatId}", directoryPath, chatId);
            var messages = await parser.ParseExportDirectoryAsync(directoryPath, chatId, ct);

            result.TotalParsed = messages.Count;

            if (messages.Count == 0)
            {
                result.ErrorMessage = "No messages found in export";
                return result;
            }

            // Get existing message IDs to skip duplicates
            var existingIds = skipExisting
                ? await GetExistingMessageIdsAsync(chatId, ct)
                : [];

            result.SkippedExisting = existingIds.Count;

            // Filter new messages
            var newMessages = messages
                .Where(m => !existingIds.Contains(m.Id))
                .ToList();

            if (newMessages.Count == 0)
            {
                logger.LogInformation("[Import] All messages already exist in database");
                result.IsSuccess = true;
                return result;
            }

            // Import in batches
            const int batchSize = 500;
            var imported = 0;

            for (var i = 0; i < newMessages.Count; i += batchSize)
            {
                ct.ThrowIfCancellationRequested();

                var batch = newMessages.Skip(i).Take(batchSize).ToList();
                await InsertMessagesBatchAsync(batch, ct);

                imported += batch.Count;
                logger.LogInformation("[Import] Progress: {Imported}/{Total} messages",
                    imported, newMessages.Count);
            }

            result.Imported = imported;
            result.IsSuccess = true;

            logger.LogInformation("[Import] Complete: {Imported} new messages imported, {Skipped} skipped",
                imported, result.SkippedExisting);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Import] Failed to import from {Path}", directoryPath);
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    private async Task<HashSet<long>> GetExistingMessageIdsAsync(long chatId, CancellationToken ct)
    {
        using var connection = await connectionFactory.CreateConnectionAsync();

        var ids = await connection.QueryAsync<long>(
            "SELECT id FROM messages WHERE chat_id = @ChatId",
            new { ChatId = chatId });

        return ids.ToHashSet();
    }

    private async Task InsertMessagesBatchAsync(List<ImportedMessage> messages, CancellationToken ct)
    {
        using var connection = await connectionFactory.CreateConnectionAsync();
        using var transaction = connection.BeginTransaction();

        try
        {
            foreach (var msg in messages)
            {
                await connection.ExecuteAsync(
                    """
                    INSERT INTO messages (id, chat_id, from_user_id, username, display_name, text, date_utc, reply_to_message_id, has_links, has_media, message_type)
                    VALUES (@Id, @ChatId, @FromUserId, @Username, @DisplayName, @Text, @DateUtc, @ReplyToMessageId, @HasLinks, @HasMedia, 'text')
                    ON CONFLICT (chat_id, id) DO NOTHING
                    """,
                    new
                    {
                        msg.Id,
                        msg.ChatId,
                        msg.FromUserId,
                        msg.Username,
                        msg.DisplayName,
                        msg.Text,
                        msg.DateUtc,
                        msg.ReplyToMessageId,
                        msg.HasLinks,
                        msg.HasMedia
                    },
                    transaction);
            }

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }
}

public class ImportResult
{
    public bool IsSuccess { get; set; }
    public long ChatId { get; set; }
    public int TotalParsed { get; set; }
    public int Imported { get; set; }
    public int SkippedExisting { get; set; }
    public string? ErrorMessage { get; set; }
}
