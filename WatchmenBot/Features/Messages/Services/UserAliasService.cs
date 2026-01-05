using Dapper;
using WatchmenBot.Infrastructure.Database;

namespace WatchmenBot.Features.Messages.Services;

/// <summary>
/// Service for managing user aliases (different names for the same user).
/// Enables resolving "Бексултан" → user_id even if user changed name to "Beksultan Valiev".
/// </summary>
public class UserAliasService(
    IDbConnectionFactory connectionFactory,
    ILogger<UserAliasService> logger)
{
    /// <summary>
    /// Record an alias for a user (called when saving messages).
    /// Uses UPSERT to increment usage_count if alias already exists.
    /// </summary>
    public async Task RecordAliasAsync(
        long chatId,
        long userId,
        string alias,
        string aliasType = "display_name",
        CancellationToken ct = default)
    {
        if (userId <= 0 || string.IsNullOrWhiteSpace(alias))
            return;

        try
        {
            using var connection = await connectionFactory.CreateConnectionAsync();

            const string sql = """
                INSERT INTO user_aliases (chat_id, user_id, alias, alias_type, usage_count, first_seen, last_seen)
                VALUES (@ChatId, @UserId, @Alias, @AliasType, 1, NOW(), NOW())
                ON CONFLICT (chat_id, user_id, alias) DO UPDATE SET
                    usage_count = user_aliases.usage_count + 1,
                    last_seen = NOW();
                """;

            await connection.ExecuteAsync(new CommandDefinition(sql, new
            {
                ChatId = chatId,
                UserId = userId,
                Alias = alias.Trim(),
                AliasType = aliasType
            }, cancellationToken: ct));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[UserAlias] Failed to record alias '{Alias}' for user {UserId}", alias, userId);
        }
    }

    /// <summary>
    /// Resolve an alias to user_id(s) in a chat.
    /// Returns list of possible user_ids sorted by usage_count (most likely first).
    /// </summary>
    public async Task<List<long>> ResolveAliasAsync(
        long chatId,
        string alias,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(alias))
            return [];

        try
        {
            using var connection = await connectionFactory.CreateConnectionAsync();

            // Case-insensitive search with GROUP BY for ORDER BY MAX(usage_count)
            const string sql = """
                SELECT user_id
                FROM user_aliases
                WHERE chat_id = @ChatId
                  AND LOWER(alias) = LOWER(@Alias)
                GROUP BY user_id
                ORDER BY MAX(usage_count) DESC
                LIMIT 5;
                """;

            var userIds = await connection.QueryAsync<long>(new CommandDefinition(
                sql,
                new { ChatId = chatId, Alias = alias.Trim().TrimStart('@') },
                cancellationToken: ct));

            return userIds.ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[UserAlias] Failed to resolve alias '{Alias}' in chat {ChatId}", alias, chatId);
            return [];
        }
    }

    /// <summary>
    /// Get all known aliases for a user in a chat.
    /// </summary>
    public async Task<List<UserAliasInfo>> GetUserAliasesAsync(
        long chatId,
        long userId,
        CancellationToken ct = default)
    {
        try
        {
            using var connection = await connectionFactory.CreateConnectionAsync();

            const string sql = """
                SELECT alias, alias_type AS AliasType, usage_count AS UsageCount, last_seen AS LastSeen
                FROM user_aliases
                WHERE chat_id = @ChatId AND user_id = @UserId
                ORDER BY usage_count DESC;
                """;

            var aliases = await connection.QueryAsync<UserAliasInfo>(new CommandDefinition(
                sql,
                new { ChatId = chatId, UserId = userId },
                cancellationToken: ct));

            return aliases.ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[UserAlias] Failed to get aliases for user {UserId}", userId);
            return [];
        }
    }

    /// <summary>
    /// Get the most common name for a user (for display purposes).
    /// </summary>
    public async Task<string?> GetPrimaryNameAsync(
        long chatId,
        long userId,
        CancellationToken ct = default)
    {
        try
        {
            using var connection = await connectionFactory.CreateConnectionAsync();

            const string sql = """
                SELECT alias
                FROM user_aliases
                WHERE chat_id = @ChatId
                  AND user_id = @UserId
                  AND alias_type = 'display_name'
                ORDER BY last_seen DESC, usage_count DESC
                LIMIT 1;
                """;

            return await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
                sql,
                new { ChatId = chatId, UserId = userId },
                cancellationToken: ct));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[UserAlias] Failed to get primary name for user {UserId}", userId);
            return null;
        }
    }

    /// <summary>
    /// Search for users by partial alias match (for autocomplete/suggestions).
    /// </summary>
    public async Task<List<UserAliasSuggestion>> SearchAliasesAsync(
        long chatId,
        string partialAlias,
        int limit = 10,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(partialAlias) || partialAlias.Length < 2)
            return [];

        try
        {
            using var connection = await connectionFactory.CreateConnectionAsync();

            const string sql = """
                SELECT user_id AS UserId, alias AS Alias, usage_count AS UsageCount
                FROM user_aliases
                WHERE chat_id = @ChatId
                  AND LOWER(alias) LIKE LOWER(@Pattern)
                ORDER BY usage_count DESC
                LIMIT @Limit;
                """;

            var results = await connection.QueryAsync<UserAliasSuggestion>(new CommandDefinition(
                sql,
                new { ChatId = chatId, Pattern = $"%{partialAlias}%", Limit = limit },
                cancellationToken: ct));

            return results.ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[UserAlias] Failed to search aliases for '{Partial}'", partialAlias);
            return [];
        }
    }
}

public record UserAliasInfo(string Alias, string AliasType, int UsageCount, DateTime LastSeen);
public record UserAliasSuggestion(long UserId, string Alias, int UsageCount);
