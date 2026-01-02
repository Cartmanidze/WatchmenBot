using Dapper;
using WatchmenBot.Infrastructure.Database;

namespace WatchmenBot.Features.Profile.Services;

/// <summary>
/// Лёгкий сервис для добавления сообщений в очередь на анализ профиля.
/// Вызывается из SaveMessage fire-and-forget.
/// </summary>
public class ProfileQueueService(
    IDbConnectionFactory connectionFactory,
    ILogger<ProfileQueueService> logger)
{
    private const int MinTextLength = 20; // Минимальная длина текста для анализа

    /// <summary>
    /// Добавить сообщение в очередь на анализ профиля
    /// </summary>
    public async Task EnqueueMessageAsync(long chatId, long messageId, long userId, string? displayName, string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < MinTextLength)
            return;

        try
        {
            using var connection = await connectionFactory.CreateConnectionAsync();

            await connection.ExecuteAsync("""
                INSERT INTO message_queue (chat_id, message_id, user_id, display_name, text)
                VALUES (@ChatId, @MessageId, @UserId, @DisplayName, @Text)
                ON CONFLICT (chat_id, message_id) DO NOTHING
                """,
                new { ChatId = chatId, MessageId = messageId, UserId = userId, DisplayName = displayName, Text = text });

            // Также обновляем счётчики активности
            await UpdateActivityAsync(chatId, userId, displayName);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to enqueue message {ChatId}/{MessageId}", chatId, messageId);
        }
    }

    /// <summary>
    /// Обновить счётчики активности пользователя (realtime)
    /// </summary>
    public async Task UpdateActivityAsync(long chatId, long userId, string? displayName)
    {
        try
        {
            using var connection = await connectionFactory.CreateConnectionAsync();

            var hour = DateTime.UtcNow.Hour;

            await connection.ExecuteAsync("""
                INSERT INTO user_profiles (chat_id, user_id, display_name, message_count, last_message_at, active_hours)
                VALUES (@ChatId, @UserId, @DisplayName, 1, NOW(), jsonb_build_object(@Hour::text, 1))
                ON CONFLICT (chat_id, user_id) DO UPDATE SET
                    display_name = COALESCE(EXCLUDED.display_name, user_profiles.display_name),
                    message_count = user_profiles.message_count + 1,
                    last_message_at = NOW(),
                    active_hours = CASE
                        WHEN user_profiles.active_hours ? @Hour::text
                        THEN jsonb_set(
                            user_profiles.active_hours,
                            ARRAY[@Hour::text],
                            to_jsonb(COALESCE((user_profiles.active_hours->>@Hour::text)::int, 0) + 1)
                        )
                        ELSE user_profiles.active_hours || jsonb_build_object(@Hour::text, 1)
                    END,
                    updated_at = NOW()
                """,
                new { ChatId = chatId, UserId = userId, DisplayName = displayName, Hour = hour.ToString() });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to update activity for user {UserId} in chat {ChatId}", userId, chatId);
        }
    }

    /// <summary>
    /// Получить необработанные сообщения из очереди (для воркера)
    /// </summary>
    public async Task<List<QueuedMessage>> GetPendingMessagesAsync(int limit = 200)
    {
        try
        {
            using var connection = await connectionFactory.CreateConnectionAsync();

            var messages = await connection.QueryAsync<QueuedMessage>("""
                SELECT id, chat_id AS ChatId, message_id AS MessageId, user_id AS UserId,
                       display_name AS DisplayName, text AS Text, created_at AS CreatedAt
                FROM message_queue
                WHERE processed = FALSE
                ORDER BY created_at
                LIMIT @Limit
                """,
                new { Limit = limit });

            return messages.ToList();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get pending messages");
            return [];
        }
    }

    /// <summary>
    /// Пометить сообщения как обработанные
    /// </summary>
    public async Task MarkAsProcessedAsync(List<int> ids)
    {
        if (!ids.Any()) return;

        try
        {
            using var connection = await connectionFactory.CreateConnectionAsync();

            await connection.ExecuteAsync("""
                UPDATE message_queue SET processed = TRUE WHERE id = ANY(@Ids)
                """,
                new { Ids = ids.ToArray() });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to mark messages as processed");
        }
    }

    /// <summary>
    /// Очистить старые обработанные сообщения (вызывается периодически)
    /// </summary>
    public async Task CleanupOldMessagesAsync(int daysToKeep = 7)
    {
        try
        {
            using var connection = await connectionFactory.CreateConnectionAsync();

            var deleted = await connection.ExecuteAsync("""
                DELETE FROM message_queue
                WHERE processed = TRUE AND created_at < NOW() - @Days * INTERVAL '1 day'
                """,
                new { Days = daysToKeep });

            if (deleted > 0)
            {
                logger.LogInformation("Cleaned up {Count} old processed messages from queue", deleted);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to cleanup old messages");
        }
    }
}

public class QueuedMessage
{
    public int Id { get; set; }
    public long ChatId { get; set; }
    public long MessageId { get; set; }
    public long UserId { get; set; }
    public string? DisplayName { get; set; }
    public string Text { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}
