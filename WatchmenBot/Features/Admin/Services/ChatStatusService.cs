using System.Collections.Concurrent;
using Dapper;
using WatchmenBot.Infrastructure.Database;

namespace WatchmenBot.Features.Admin.Services;

/// <summary>
/// Service for managing chat active/inactive status.
/// Automatically deactivates chats when bot is kicked (403 error).
/// Includes in-memory cache for hot path optimization with automatic pruning.
/// </summary>
public class ChatStatusService(
    IDbConnectionFactory connectionFactory,
    ILogger<ChatStatusService> logger)
{
    // Static cache shared across all scoped instances (TTL-based)
    private static readonly ConcurrentDictionary<long, (bool IsActive, DateTime CachedAt)> StatusCache = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    // Cache pruning settings
    private static readonly TimeSpan PruneThreshold = TimeSpan.FromHours(1); // Remove entries older than this
    private const int MaxCacheSize = 10_000; // Trigger pruning if exceeded
    private const int PruneInterval = 100; // Prune every N cache updates
    private static int _updateCounter;

    /// <summary>
    /// Deactivate a chat (e.g., when bot is kicked or chat is deleted).
    /// </summary>
    public async Task<bool> DeactivateChatAsync(
        long chatId,
        string reason,
        CancellationToken ct = default)
    {
        try
        {
            using var connection = await connectionFactory.CreateConnectionAsync();

            var affected = await connection.ExecuteAsync(
                new CommandDefinition(
                    """
                    UPDATE chats
                    SET is_active = FALSE,
                        deactivated_at = NOW(),
                        deactivation_reason = @Reason
                    WHERE id = @ChatId
                      AND is_active = TRUE
                    """,
                    new { ChatId = chatId, Reason = reason },
                    cancellationToken: ct));

            // Invalidate cache
            InvalidateCache(chatId);

            if (affected > 0)
            {
                logger.LogWarning("[ChatStatus] Chat {ChatId} deactivated: {Reason}", chatId, reason);
                return true;
            }

            logger.LogDebug("[ChatStatus] Chat {ChatId} was already inactive", chatId);
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[ChatStatus] Failed to deactivate chat {ChatId}", chatId);
            // Still invalidate cache to be safe
            InvalidateCache(chatId);
            return false;
        }
    }

    /// <summary>
    /// Reactivate a previously deactivated chat (admin operation).
    /// </summary>
    public async Task<bool> ReactivateChatAsync(
        long chatId,
        CancellationToken ct = default)
    {
        try
        {
            using var connection = await connectionFactory.CreateConnectionAsync();

            var affected = await connection.ExecuteAsync(
                new CommandDefinition(
                    """
                    UPDATE chats
                    SET is_active = TRUE,
                        deactivated_at = NULL,
                        deactivation_reason = NULL
                    WHERE id = @ChatId
                      AND is_active = FALSE
                    """,
                    new { ChatId = chatId },
                    cancellationToken: ct));

            // Invalidate cache
            InvalidateCache(chatId);

            if (affected > 0)
            {
                logger.LogInformation("[ChatStatus] Chat {ChatId} reactivated", chatId);
                return true;
            }

            logger.LogDebug("[ChatStatus] Chat {ChatId} was already active", chatId);
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[ChatStatus] Failed to reactivate chat {ChatId}", chatId);
            return false;
        }
    }

    /// <summary>
    /// Check if a chat is currently active.
    /// Uses in-memory cache with TTL for hot path optimization.
    /// On DB error, returns cached value or true (fail-open with logging).
    /// </summary>
    public async Task<bool> IsChatActiveAsync(long chatId, CancellationToken ct = default)
    {
        // Check cache first
        if (StatusCache.TryGetValue(chatId, out var cached))
        {
            if (DateTime.UtcNow - cached.CachedAt < CacheTtl)
            {
                return cached.IsActive;
            }
        }

        try
        {
            using var connection = await connectionFactory.CreateConnectionAsync();

            // Also check if chat exists (if not, treat as inactive)
            var isActive = await connection.ExecuteScalarAsync<bool?>(
                new CommandDefinition(
                    """
                    SELECT is_active FROM chats WHERE id = @ChatId
                    """,
                    new { ChatId = chatId },
                    cancellationToken: ct));

            // If chat doesn't exist in DB, treat as active (it will be created on first message)
            var result = isActive ?? true;

            // Update cache (with automatic pruning)
            UpdateCache(chatId, result);

            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[ChatStatus] Failed to check status for chat {ChatId}. Using cached/default value.", chatId);

            // Fail-open strategy: if we have a cached value, use it; otherwise allow (true)
            if (StatusCache.TryGetValue(chatId, out var fallback))
            {
                logger.LogWarning("[ChatStatus] Using stale cache for chat {ChatId}: IsActive={IsActive}", chatId, fallback.IsActive);
                return fallback.IsActive;
            }

            // No cache, fail-open (allow) but log warning
            logger.LogWarning("[ChatStatus] No cache available for chat {ChatId}, allowing (fail-open)", chatId);
            return true;
        }
    }

    /// <summary>
    /// Get all active chat IDs for background processing (e.g., daily summaries).
    /// Includes chats from messages table that may not exist in chats table yet.
    /// Excludes explicitly deactivated chats.
    /// </summary>
    public async Task<IReadOnlyList<long>> GetActiveChatIdsAsync(CancellationToken ct = default)
    {
        try
        {
            using var connection = await connectionFactory.CreateConnectionAsync();

            // Get all chat IDs from messages, excluding those explicitly deactivated in chats table
            // This ensures we don't miss chats that have messages but aren't in chats table yet
            var chatIds = await connection.QueryAsync<long>(
                new CommandDefinition(
                    """
                    SELECT DISTINCT m.chat_id
                    FROM messages m
                    LEFT JOIN chats c ON m.chat_id = c.id
                    WHERE c.is_active IS NULL OR c.is_active = TRUE
                    """,
                    cancellationToken: ct));

            var result = chatIds.ToList();

            // Update cache for all retrieved chats (with automatic pruning on last update)
            foreach (var chatId in result)
            {
                UpdateCache(chatId, true);
            }

            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[ChatStatus] Failed to get active chat IDs");
            throw; // Re-throw for caller to handle
        }
    }

    /// <summary>
    /// Get all deactivated chats (for admin diagnostics).
    /// </summary>
    public async Task<IReadOnlyList<DeactivatedChat>> GetDeactivatedChatsAsync(CancellationToken ct = default)
    {
        try
        {
            using var connection = await connectionFactory.CreateConnectionAsync();

            var chats = await connection.QueryAsync<DeactivatedChat>(
                new CommandDefinition(
                    """
                    SELECT
                        id AS ChatId,
                        title AS Title,
                        deactivated_at AS DeactivatedAt,
                        deactivation_reason AS Reason
                    FROM chats
                    WHERE is_active = FALSE
                    ORDER BY deactivated_at DESC
                    """,
                    cancellationToken: ct));

            return chats.ToList();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[ChatStatus] Failed to get deactivated chats");
            throw;
        }
    }

    /// <summary>
    /// Invalidate cache for a specific chat (called after status change)
    /// </summary>
    private static void InvalidateCache(long chatId)
    {
        StatusCache.TryRemove(chatId, out _);
    }

    /// <summary>
    /// Update cache entry and trigger pruning if needed.
    /// </summary>
    private void UpdateCache(long chatId, bool isActive)
    {
        StatusCache[chatId] = (isActive, DateTime.UtcNow);

        // Lazy pruning: check periodically or when cache is too large
        var counter = Interlocked.Increment(ref _updateCounter);
        if (counter % PruneInterval == 0 || StatusCache.Count > MaxCacheSize)
        {
            PruneStaleEntries();
        }
    }

    /// <summary>
    /// Remove stale cache entries older than PruneThreshold.
    /// Called automatically during cache updates.
    /// </summary>
    private void PruneStaleEntries()
    {
        var threshold = DateTime.UtcNow - PruneThreshold;
        var staleKeys = StatusCache
            .Where(kv => kv.Value.CachedAt < threshold)
            .Select(kv => kv.Key)
            .ToList();

        if (staleKeys.Count == 0) return;

        foreach (var key in staleKeys)
        {
            StatusCache.TryRemove(key, out _);
        }

        logger.LogDebug("[ChatStatus] Pruned {Count} stale cache entries. Remaining: {Remaining}",
            staleKeys.Count, StatusCache.Count);
    }

    /// <summary>
    /// Get current cache statistics (for diagnostics).
    /// </summary>
    public static (int Count, int StaleCount) GetCacheStats()
    {
        var threshold = DateTime.UtcNow - PruneThreshold;
        var staleCount = StatusCache.Count(kv => kv.Value.CachedAt < threshold);
        return (StatusCache.Count, staleCount);
    }
}

/// <summary>
/// Deactivated chat record from database
/// </summary>
public record DeactivatedChat
{
    public long ChatId { get; init; }
    public string? Title { get; init; }
    public DateTime? DeactivatedAt { get; init; }
    public string? Reason { get; init; }
}
