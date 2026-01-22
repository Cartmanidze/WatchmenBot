using System.Collections.Concurrent;
using Dapper;
using WatchmenBot.Infrastructure.Database;

namespace WatchmenBot.Features.Admin.Services;

/// <summary>
/// Service for managing user bans (global, per-user).
/// Includes in-memory cache for hot path optimization.
/// </summary>
public class BannedUserService(
    IDbConnectionFactory connectionFactory,
    ILogger<BannedUserService> logger)
{
    // Static cache shared across all scoped instances (TTL-based)
    private static readonly ConcurrentDictionary<long, (bool IsBanned, DateTime CachedAt)> BanCache = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Ban a user globally (across all chats).
    /// Uses ON CONFLICT to handle race conditions gracefully.
    /// </summary>
    public async Task<BanResult> BanUserAsync(
        long userId,
        long bannedByUserId,
        string? reason = null,
        TimeSpan? duration = null,
        CancellationToken ct = default)
    {
        try
        {
            using var connection = await connectionFactory.CreateConnectionAsync();

            // Deactivate any existing bans first (expired or active)
            await connection.ExecuteAsync(
                new CommandDefinition(
                    """
                    UPDATE banned_users
                    SET is_active = FALSE
                    WHERE user_id = @UserId AND is_active = TRUE
                    """,
                    new { UserId = userId },
                    cancellationToken: ct));

            // Create new ban with ON CONFLICT for race condition safety
            var expiresAt = duration.HasValue ? DateTime.UtcNow + duration.Value : (DateTime?)null;

            var newBan = await connection.QueryFirstOrDefaultAsync<BannedUser>(
                new CommandDefinition(
                    """
                    INSERT INTO banned_users (user_id, reason, banned_by_user_id, expires_at, is_active)
                    VALUES (@UserId, @Reason, @BannedByUserId, @ExpiresAt, TRUE)
                    ON CONFLICT (user_id) WHERE is_active = TRUE
                    DO UPDATE SET
                        reason = EXCLUDED.reason,
                        banned_by_user_id = EXCLUDED.banned_by_user_id,
                        expires_at = EXCLUDED.expires_at,
                        banned_at = NOW()
                    RETURNING *
                    """,
                    new
                    {
                        UserId = userId,
                        Reason = reason,
                        BannedByUserId = bannedByUserId,
                        ExpiresAt = expiresAt
                    },
                    cancellationToken: ct));

            // Invalidate cache
            InvalidateCache(userId);

            if (newBan == null)
            {
                logger.LogWarning("[Ban] Failed to create ban for user {UserId} - no result returned", userId);
                return BanResult.AlreadyBanned(null!);
            }

            logger.LogInformation(
                "[Ban] User {UserId} banned by {AdminId}. Reason: {Reason}. Expires: {ExpiresAt}",
                userId, bannedByUserId, reason ?? "none", expiresAt?.ToString("g") ?? "never");

            return BanResult.Success(newBan);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Ban] Failed to ban user {UserId}", userId);
            throw; // Re-throw for command to handle
        }
    }

    /// <summary>
    /// Unban a user
    /// </summary>
    public async Task<UnbanResult> UnbanUserAsync(
        long userId,
        long unbannedByUserId,
        CancellationToken ct = default)
    {
        try
        {
            using var connection = await connectionFactory.CreateConnectionAsync();

            var affected = await connection.ExecuteAsync(
                new CommandDefinition(
                    """
                    UPDATE banned_users
                    SET is_active = FALSE,
                        unbanned_at = NOW(),
                        unbanned_by_user_id = @UnbannedByUserId
                    WHERE user_id = @UserId
                      AND is_active = TRUE
                    """,
                    new { UserId = userId, UnbannedByUserId = unbannedByUserId },
                    cancellationToken: ct));

            // Invalidate cache
            InvalidateCache(userId);

            if (affected == 0)
            {
                return UnbanResult.NotBanned();
            }

            logger.LogInformation("[Ban] User {UserId} unbanned by {AdminId}", userId, unbannedByUserId);
            return UnbanResult.Success();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Ban] Failed to unban user {UserId}", userId);
            throw; // Re-throw for command to handle
        }
    }

    /// <summary>
    /// Check if a user is currently banned.
    /// Uses in-memory cache with TTL for hot path optimization.
    /// On DB error, returns cached value or false (fail-open with logging).
    /// </summary>
    public async Task<bool> IsUserBannedAsync(long userId, CancellationToken ct = default)
    {
        // Check cache first
        if (BanCache.TryGetValue(userId, out var cached))
        {
            if (DateTime.UtcNow - cached.CachedAt < CacheTtl)
            {
                return cached.IsBanned;
            }
        }

        try
        {
            using var connection = await connectionFactory.CreateConnectionAsync();

            var isBanned = await connection.ExecuteScalarAsync<bool>(
                new CommandDefinition(
                    """
                    SELECT EXISTS (
                        SELECT 1 FROM banned_users
                        WHERE user_id = @UserId
                          AND is_active = TRUE
                          AND (expires_at IS NULL OR expires_at > NOW())
                    )
                    """,
                    new { UserId = userId },
                    cancellationToken: ct));

            // Update cache
            BanCache[userId] = (isBanned, DateTime.UtcNow);

            return isBanned;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Ban] Failed to check ban status for user {UserId}. Using cached/default value.", userId);

            // Fail-open strategy: if we have a cached value, use it; otherwise allow (false)
            // This prevents blocking all users when DB is temporarily unavailable
            if (BanCache.TryGetValue(userId, out var fallback))
            {
                logger.LogWarning("[Ban] Using stale cache for user {UserId}: IsBanned={IsBanned}", userId, fallback.IsBanned);
                return fallback.IsBanned;
            }

            // No cache, fail-open (allow) but log warning
            logger.LogWarning("[Ban] No cache available for user {UserId}, allowing (fail-open)", userId);
            return false;
        }
    }

    /// <summary>
    /// Get all active bans (for banlist command)
    /// </summary>
    public async Task<IReadOnlyList<BannedUser>> GetBannedUsersAsync(CancellationToken ct = default)
    {
        try
        {
            using var connection = await connectionFactory.CreateConnectionAsync();

            var bannedUsers = await connection.QueryAsync<BannedUser>(
                new CommandDefinition(
                    """
                    SELECT
                        id AS Id,
                        user_id AS UserId,
                        reason AS Reason,
                        banned_by_user_id AS BannedByUserId,
                        banned_at AS BannedAt,
                        expires_at AS ExpiresAt,
                        is_active AS IsActive,
                        unbanned_at AS UnbannedAt,
                        unbanned_by_user_id AS UnbannedByUserId
                    FROM banned_users
                    WHERE is_active = TRUE
                      AND (expires_at IS NULL OR expires_at > NOW())
                    ORDER BY banned_at DESC
                    """,
                    cancellationToken: ct));

            return bannedUsers.ToList();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Ban] Failed to get banned users list");
            throw; // Re-throw for command to handle
        }
    }

    /// <summary>
    /// Get ban info for a specific user (for diagnostics)
    /// </summary>
    public async Task<BannedUser?> GetBanInfoAsync(long userId, CancellationToken ct = default)
    {
        try
        {
            using var connection = await connectionFactory.CreateConnectionAsync();

            return await connection.QueryFirstOrDefaultAsync<BannedUser>(
                new CommandDefinition(
                    """
                    SELECT
                        id AS Id,
                        user_id AS UserId,
                        reason AS Reason,
                        banned_by_user_id AS BannedByUserId,
                        banned_at AS BannedAt,
                        expires_at AS ExpiresAt,
                        is_active AS IsActive,
                        unbanned_at AS UnbannedAt,
                        unbanned_by_user_id AS UnbannedByUserId
                    FROM banned_users
                    WHERE user_id = @UserId
                      AND is_active = TRUE
                      AND (expires_at IS NULL OR expires_at > NOW())
                    """,
                    new { UserId = userId },
                    cancellationToken: ct));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Ban] Failed to get ban info for user {UserId}", userId);
            throw; // Re-throw for command to handle
        }
    }

    /// <summary>
    /// Invalidate cache for a specific user (called after ban/unban)
    /// </summary>
    private static void InvalidateCache(long userId)
    {
        BanCache.TryRemove(userId, out _);
    }
}

/// <summary>
/// Banned user record from database
/// </summary>
public record BannedUser
{
    public int Id { get; init; }
    public long UserId { get; init; }
    public string? Reason { get; init; }
    public long BannedByUserId { get; init; }
    public DateTime BannedAt { get; init; }
    public DateTime? ExpiresAt { get; init; }
    public bool IsActive { get; init; }
    public DateTime? UnbannedAt { get; init; }
    public long? UnbannedByUserId { get; init; }

    /// <summary>
    /// True if ban has expired (but wasn't manually lifted)
    /// </summary>
    public bool IsExpired => ExpiresAt.HasValue && ExpiresAt.Value <= DateTime.UtcNow;

    /// <summary>
    /// True if ban is currently in effect
    /// </summary>
    public bool IsEffective => IsActive && !IsExpired;
}

/// <summary>
/// Result of ban operation
/// </summary>
public record BanResult
{
    private BanResult() { }

    public bool IsSuccess { get; private init; }
    public bool WasAlreadyBanned { get; private init; }
    public BannedUser? Ban { get; private init; }

    public static BanResult Success(BannedUser ban) => new() { IsSuccess = true, Ban = ban };
    public static BanResult AlreadyBanned(BannedUser existingBan) => new() { IsSuccess = false, WasAlreadyBanned = true, Ban = existingBan };
}

/// <summary>
/// Result of unban operation
/// </summary>
public record UnbanResult
{
    private UnbanResult() { }

    public bool IsSuccess { get; private init; }
    public bool WasNotBanned { get; private init; }

    public static UnbanResult Success() => new() { IsSuccess = true };
    public static UnbanResult NotBanned() => new() { IsSuccess = false, WasNotBanned = true };
}
