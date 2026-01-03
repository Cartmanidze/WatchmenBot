using System.Collections.Concurrent;
using System.Text.Json;
using Dapper;
using WatchmenBot.Features.Llm.Services;
using WatchmenBot.Infrastructure.Database;

namespace WatchmenBot.Features.Search.Services;

/// <summary>
/// Resolves nicknames/aliases to actual usernames using LLM.
/// </summary>
public class NicknameResolverService(
    IDbConnectionFactory connectionFactory,
    LlmRouter llmRouter,
    ILogger<NicknameResolverService> logger)
{
    // Cache user lists per chat (cleared periodically or on demand)
    private static readonly ConcurrentDictionary<long, (DateTime cachedAt, List<ChatUser> users)> _userCache = new();
    private static readonly TimeSpan CacheExpiry = TimeSpan.FromMinutes(30);

    private const string SystemPrompt = """
        Ты помогаешь определить, кого из пользователей чата имеют в виду по нику или сокращению.

        ПРАВИЛА:
        - Если ник/сокращение ОДНОЗНАЧНО соответствует кому-то из списка → верни его имя
        - Если НЕ можешь определить → верни "unknown"
        - Отвечай ТОЛЬКО JSON без markdown

        ФОРМАТ ОТВЕТА:
        {"resolved_name": "Имя из списка", "confidence": 0.95, "reasoning": "почему так решил"}

        Если не уверен:
        {"resolved_name": "unknown", "confidence": 0.0, "reasoning": "не удалось определить"}
        """;

    /// <summary>
    /// Chat user info for nickname resolution
    /// </summary>
    public record ChatUser(string DisplayName, string? Username, int MessageCount);

    /// <summary>
    /// Result of nickname resolution
    /// </summary>
    public record ResolvedNickname(
        string OriginalNick,
        string? ResolvedName,
        double Confidence,
        string? Reasoning);

    /// <summary>
    /// Resolve a nickname to a real username from the chat
    /// </summary>
    public async Task<ResolvedNickname> ResolveNicknameAsync(
        long chatId,
        string nickname,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(nickname))
        {
            return new ResolvedNickname(nickname, null, 0, "Empty nickname");
        }

        try
        {
            // Get users in chat
            var users = await GetChatUsersAsync(chatId, ct);

            if (users.Count == 0)
            {
                logger.LogWarning("[NicknameResolver] No users found in chat {ChatId}", chatId);
                return new ResolvedNickname(nickname, null, 0, "No users in chat");
            }

            // Quick exact match check (case-insensitive)
            var exactMatch = users.FirstOrDefault(u =>
                u.DisplayName.Equals(nickname, StringComparison.OrdinalIgnoreCase) ||
                (u.Username?.Equals(nickname.TrimStart('@'), StringComparison.OrdinalIgnoreCase) ?? false));

            if (exactMatch != null)
            {
                logger.LogInformation("[NicknameResolver] Exact match: '{Nick}' → '{Name}'",
                    nickname, exactMatch.DisplayName);
                return new ResolvedNickname(nickname, exactMatch.DisplayName, 1.0, "Exact match");
            }

            // Use LLM to resolve nickname
            var userList = string.Join("\n", users.Take(20).Select(u =>
                $"- {u.DisplayName}" + (u.Username != null ? $" (@{u.Username})" : "") + $" — {u.MessageCount} сообщений"));

            var prompt = $"""
                Пользователи чата (топ-20 по активности):
                {userList}

                Вопрос содержит ник/упоминание: "{nickname}"

                Кого из списка имеют в виду?
                """;

            var response = await llmRouter.CompleteAsync(new LlmRequest
            {
                SystemPrompt = SystemPrompt,
                UserPrompt = prompt,
                Temperature = 0.1
            }, ct);

            var result = ParseResponse(response.Content, nickname);

            logger.LogInformation("[NicknameResolver] Resolved '{Nick}' → '{Name}' (conf: {Conf:F2})",
                nickname, result.ResolvedName ?? "unknown", result.Confidence);

            return result;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[NicknameResolver] Failed to resolve '{Nick}'", nickname);
            return new ResolvedNickname(nickname, null, 0, ex.Message);
        }
    }

    /// <summary>
    /// Extract potential nicknames from a question that might need resolution
    /// </summary>
    public async Task<List<string>> ExtractNicknamesAsync(
        string question,
        CancellationToken ct = default)
    {
        // Use LLM to extract nicknames that might need resolution
        var prompt = $$"""
            Из вопроса извлеки ТОЛЬКО ники/имена людей, которые упоминаются:
            "{{question}}"

            Отвечай JSON: {"nicknames": ["ник1", "ник2"]}
            Если ников нет — верни пустой массив.
            НЕ включай местоимения (я, ты, он).
            """;

        try
        {
            var response = await llmRouter.CompleteAsync(new LlmRequest
            {
                SystemPrompt = "Извлекай ники/имена из текста. Отвечай ТОЛЬКО JSON.",
                UserPrompt = prompt,
                Temperature = 0.1
            }, ct);

            var json = ExtractJson(response.Content);
            if (json != null)
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("nicknames", out var arr))
                {
                    return arr.EnumerateArray()
                        .Select(x => x.GetString())
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .ToList()!;
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[NicknameResolver] Failed to extract nicknames from '{Question}'", question);
        }

        return [];
    }

    /// <summary>
    /// Get top users in chat (cached)
    /// </summary>
    public async Task<List<ChatUser>> GetChatUsersAsync(long chatId, CancellationToken ct = default)
    {
        // Check cache
        if (_userCache.TryGetValue(chatId, out var cached) &&
            DateTime.UtcNow - cached.cachedAt < CacheExpiry)
        {
            return cached.users;
        }

        using var connection = await connectionFactory.CreateConnectionAsync();

        var users = await connection.QueryAsync<ChatUser>(
            """
            SELECT
                display_name as DisplayName,
                username as Username,
                COUNT(*) as MessageCount
            FROM messages
            WHERE chat_id = @ChatId
              AND display_name IS NOT NULL
              AND display_name != ''
            GROUP BY display_name, username
            ORDER BY COUNT(*) DESC
            LIMIT 50
            """,
            new { ChatId = chatId });

        var userList = users.ToList();

        // Update cache
        _userCache[chatId] = (DateTime.UtcNow, userList);

        logger.LogDebug("[NicknameResolver] Loaded {Count} users for chat {ChatId}", userList.Count, chatId);

        return userList;
    }

    private ResolvedNickname ParseResponse(string content, string originalNick)
    {
        try
        {
            var json = ExtractJson(content);
            if (json == null)
            {
                return new ResolvedNickname(originalNick, null, 0, "Failed to parse JSON");
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var resolvedName = root.TryGetProperty("resolved_name", out var nameProp)
                ? nameProp.GetString()
                : null;

            var confidence = root.TryGetProperty("confidence", out var confProp)
                ? confProp.GetDouble()
                : 0.0;

            var reasoning = root.TryGetProperty("reasoning", out var reasonProp)
                ? reasonProp.GetString()
                : null;

            // "unknown" means not resolved
            if (resolvedName?.Equals("unknown", StringComparison.OrdinalIgnoreCase) == true)
            {
                resolvedName = null;
                confidence = 0;
            }

            return new ResolvedNickname(originalNick, resolvedName, confidence, reasoning);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[NicknameResolver] JSON parse error: {Content}", content);
            return new ResolvedNickname(originalNick, null, 0, "JSON parse error");
        }
    }

    private static string? ExtractJson(string content)
    {
        // Try to extract JSON from possible markdown code blocks
        var jsonStart = content.IndexOf('{');
        var jsonEnd = content.LastIndexOf('}');

        if (jsonStart >= 0 && jsonEnd > jsonStart)
        {
            return content[jsonStart..(jsonEnd + 1)];
        }

        return null;
    }
}
