using Dapper;
using WatchmenBot.Infrastructure.Database;

namespace WatchmenBot.Infrastructure.Settings;

/// <summary>
/// Manages per-chat settings (mode, language) with caching.
/// Settings are stored in chat_settings table.
/// </summary>
public class ChatSettingsStore(
    IDbConnectionFactory connectionFactory,
    ILogger<ChatSettingsStore> logger)
{
    // In-memory cache for frequently accessed settings
    // Key: chatId, Value: (settings, cachedAt)
    private readonly Dictionary<long, (ChatSettings Settings, DateTimeOffset CachedAt)> _cache = new();
    private readonly TimeSpan _cacheTtl = TimeSpan.FromMinutes(5);
    private readonly object _cacheLock = new();

    /// <summary>
    /// Get settings for a chat. Returns default if not found.
    /// </summary>
    public async Task<ChatSettings> GetSettingsAsync(long chatId)
    {
        // Check cache first
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(chatId, out var cached) &&
                DateTimeOffset.UtcNow - cached.CachedAt < _cacheTtl)
            {
                return cached.Settings;
            }
        }

        try
        {
            using var connection = await connectionFactory.CreateConnectionAsync();

            var result = await connection.QuerySingleOrDefaultAsync<(int Mode, int Language, DateTimeOffset UpdatedAt)?>(
                """
                SELECT mode, language, updated_at
                FROM chat_settings
                WHERE chat_id = @ChatId
                """,
                new { ChatId = chatId });

            if (result.HasValue)
            {
                var settings = new ChatSettings
                {
                    ChatId = chatId,
                    Mode = (ChatMode)result.Value.Mode,
                    Language = (ChatLanguage)result.Value.Language,
                    UpdatedAt = result.Value.UpdatedAt
                };

                UpdateCache(chatId, settings);
                return settings;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to get settings for chat {ChatId}, using default", chatId);
        }

        // Return default (Business mode, Russian language)
        return ChatSettings.Default(chatId);
    }

    /// <summary>
    /// Set mode for a chat
    /// </summary>
    public async Task SetModeAsync(long chatId, ChatMode mode)
    {
        try
        {
            using var connection = await connectionFactory.CreateConnectionAsync();

            await connection.ExecuteAsync(
                """
                INSERT INTO chat_settings (chat_id, mode, language, updated_at)
                VALUES (@ChatId, @Mode, @Language, NOW())
                ON CONFLICT (chat_id) DO UPDATE SET
                    mode = EXCLUDED.mode,
                    updated_at = NOW()
                """,
                new { ChatId = chatId, Mode = (int)mode, Language = (int)ChatLanguage.Ru });

            // Invalidate cache
            InvalidateCache(chatId);

            logger.LogInformation("Set mode for chat {ChatId}: {Mode}", chatId, mode);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to set mode for chat {ChatId}", chatId);
            throw;
        }
    }

    /// <summary>
    /// Set language for a chat
    /// </summary>
    public async Task SetLanguageAsync(long chatId, ChatLanguage language)
    {
        try
        {
            using var connection = await connectionFactory.CreateConnectionAsync();

            await connection.ExecuteAsync(
                """
                INSERT INTO chat_settings (chat_id, mode, language, updated_at)
                VALUES (@ChatId, @Mode, @Language, NOW())
                ON CONFLICT (chat_id) DO UPDATE SET
                    language = EXCLUDED.language,
                    updated_at = NOW()
                """,
                new { ChatId = chatId, Mode = (int)ChatMode.Business, Language = (int)language });

            // Invalidate cache
            InvalidateCache(chatId);

            logger.LogInformation("Set language for chat {ChatId}: {Language}", chatId, language);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to set language for chat {ChatId}", chatId);
            throw;
        }
    }

    /// <summary>
    /// Get mode for a chat (convenience method)
    /// </summary>
    public async Task<ChatMode> GetModeAsync(long chatId)
    {
        var settings = await GetSettingsAsync(chatId);
        return settings.Mode;
    }

    /// <summary>
    /// Get language for a chat (convenience method)
    /// </summary>
    public async Task<ChatLanguage> GetLanguageAsync(long chatId)
    {
        var settings = await GetSettingsAsync(chatId);
        return settings.Language;
    }

    private void UpdateCache(long chatId, ChatSettings settings)
    {
        lock (_cacheLock)
        {
            _cache[chatId] = (settings, DateTimeOffset.UtcNow);
        }
    }

    private void InvalidateCache(long chatId)
    {
        lock (_cacheLock)
        {
            _cache.Remove(chatId);
        }
    }
}
