using System.Text.Json;
using Dapper;
using WatchmenBot.Infrastructure.Database;
using WatchmenBot.Services.Llm;

namespace WatchmenBot.Services;

/// <summary>
/// Service for managing LLM long-term memory and user profiles
/// </summary>
public class LlmMemoryService
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly LlmRouter _llmRouter;
    private readonly ILogger<LlmMemoryService> _logger;

    // Limit memory items to avoid token overflow
    private const int MaxRecentMemories = 5;
    private const int MaxFacts = 10;
    private const int MaxTraits = 5;
    private const int MaxQuotes = 5;

    public LlmMemoryService(
        IDbConnectionFactory connectionFactory,
        LlmRouter llmRouter,
        ILogger<LlmMemoryService> logger)
    {
        _connectionFactory = connectionFactory;
        _llmRouter = llmRouter;
        _logger = logger;
    }

    #region User Profile

    /// <summary>
    /// Get user profile for a specific chat
    /// </summary>
    public async Task<UserProfile?> GetProfileAsync(long chatId, long userId, CancellationToken ct = default)
    {
        try
        {
            using var connection = await _connectionFactory.CreateConnectionAsync();

            var profile = await connection.QueryFirstOrDefaultAsync<UserProfileRecord>(
                """
                SELECT user_id, chat_id, display_name, username,
                       facts, traits, interests, notable_quotes,
                       interaction_count, last_interaction
                FROM user_profiles
                WHERE chat_id = @ChatId AND user_id = @UserId
                """,
                new { ChatId = chatId, UserId = userId });

            if (profile == null)
                return null;

            return new UserProfile
            {
                UserId = profile.user_id,
                ChatId = profile.chat_id,
                DisplayName = profile.display_name,
                Username = profile.username,
                Facts = ParseJsonArray(profile.facts),
                Traits = ParseJsonArray(profile.traits),
                Interests = ParseJsonArray(profile.interests),
                NotableQuotes = ParseJsonArray(profile.notable_quotes),
                InteractionCount = profile.interaction_count,
                LastInteraction = profile.last_interaction.HasValue
                    ? new DateTimeOffset(profile.last_interaction.Value, TimeSpan.Zero)
                    : null
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Memory] Failed to get profile for user {UserId} in chat {ChatId}", userId, chatId);
            return null;
        }
    }

    /// <summary>
    /// Update user profile with new information extracted from interaction
    /// </summary>
    public async Task UpdateProfileFromInteractionAsync(
        long chatId, long userId, string? displayName, string? username,
        string query, string response, CancellationToken ct = default)
    {
        try
        {
            // Extract facts from interaction using LLM
            var extraction = await ExtractProfileUpdatesAsync(query, response, ct);

            if (extraction == null || (!extraction.Facts.Any() && !extraction.Traits.Any()))
            {
                // Just update interaction count
                await IncrementInteractionCountAsync(chatId, userId, displayName, username, ct);
                return;
            }

            using var connection = await _connectionFactory.CreateConnectionAsync();

            // Get existing profile
            var existing = await GetProfileAsync(chatId, userId, ct);

            // Merge facts (deduplicate and limit)
            var mergedFacts = MergeLists(existing?.Facts ?? new(), extraction.Facts, MaxFacts);
            var mergedTraits = MergeLists(existing?.Traits ?? new(), extraction.Traits, MaxTraits);
            var mergedInterests = MergeLists(existing?.Interests ?? new(), extraction.Interests, MaxFacts);
            var mergedQuotes = MergeLists(existing?.NotableQuotes ?? new(), extraction.Quotes, MaxQuotes);

            await connection.ExecuteAsync(
                """
                INSERT INTO user_profiles (user_id, chat_id, display_name, username, facts, traits, interests, notable_quotes, interaction_count, last_interaction, updated_at)
                VALUES (@UserId, @ChatId, @DisplayName, @Username, @Facts::jsonb, @Traits::jsonb, @Interests::jsonb, @Quotes::jsonb, 1, NOW(), NOW())
                ON CONFLICT (chat_id, user_id) DO UPDATE SET
                    display_name = COALESCE(@DisplayName, user_profiles.display_name),
                    username = COALESCE(@Username, user_profiles.username),
                    facts = @Facts::jsonb,
                    traits = @Traits::jsonb,
                    interests = @Interests::jsonb,
                    notable_quotes = @Quotes::jsonb,
                    interaction_count = user_profiles.interaction_count + 1,
                    last_interaction = NOW(),
                    updated_at = NOW()
                """,
                new
                {
                    UserId = userId,
                    ChatId = chatId,
                    DisplayName = displayName,
                    Username = username,
                    Facts = JsonSerializer.Serialize(mergedFacts),
                    Traits = JsonSerializer.Serialize(mergedTraits),
                    Interests = JsonSerializer.Serialize(mergedInterests),
                    Quotes = JsonSerializer.Serialize(mergedQuotes)
                });

            _logger.LogInformation("[Memory] Updated profile for {User}: +{Facts} facts, +{Traits} traits",
                displayName ?? userId.ToString(), extraction.Facts.Count, extraction.Traits.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Memory] Failed to update profile for user {UserId}", userId);
        }
    }

    private async Task IncrementInteractionCountAsync(long chatId, long userId, string? displayName, string? username, CancellationToken ct)
    {
        try
        {
            using var connection = await _connectionFactory.CreateConnectionAsync();
            await connection.ExecuteAsync(
                """
                INSERT INTO user_profiles (user_id, chat_id, display_name, username, interaction_count, last_interaction)
                VALUES (@UserId, @ChatId, @DisplayName, @Username, 1, NOW())
                ON CONFLICT (chat_id, user_id) DO UPDATE SET
                    display_name = COALESCE(@DisplayName, user_profiles.display_name),
                    username = COALESCE(@Username, user_profiles.username),
                    interaction_count = user_profiles.interaction_count + 1,
                    last_interaction = NOW(),
                    updated_at = NOW()
                """,
                new { UserId = userId, ChatId = chatId, DisplayName = displayName, Username = username });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Memory] Failed to increment interaction count");
        }
    }

    #endregion

    #region Conversation Memory

    /// <summary>
    /// Get recent conversation memory for user
    /// </summary>
    public async Task<List<ConversationMemory>> GetRecentMemoriesAsync(
        long chatId, long userId, int limit = MaxRecentMemories, CancellationToken ct = default)
    {
        try
        {
            using var connection = await _connectionFactory.CreateConnectionAsync();

            var records = await connection.QueryAsync<ConversationMemoryRecord>(
                """
                SELECT id, user_id, chat_id, query, response_summary, topics, extracted_facts, created_at
                FROM conversation_memory
                WHERE chat_id = @ChatId AND user_id = @UserId
                ORDER BY created_at DESC
                LIMIT @Limit
                """,
                new { ChatId = chatId, UserId = userId, Limit = limit });

            return records.Select(r => new ConversationMemory
            {
                Id = r.id,
                UserId = r.user_id,
                ChatId = r.chat_id,
                Query = r.query,
                ResponseSummary = r.response_summary,
                Topics = ParseJsonArray(r.topics),
                ExtractedFacts = ParseJsonArray(r.extracted_facts),
                CreatedAt = new DateTimeOffset(r.created_at, TimeSpan.Zero)
            }).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Memory] Failed to get memories for user {UserId}", userId);
            return new List<ConversationMemory>();
        }
    }

    /// <summary>
    /// Store new conversation memory
    /// </summary>
    public async Task StoreMemoryAsync(
        long chatId, long userId, string query, string response, CancellationToken ct = default)
    {
        try
        {
            // Generate summary and extract topics
            var summary = await GenerateMemorySummaryAsync(query, response, ct);

            using var connection = await _connectionFactory.CreateConnectionAsync();

            await connection.ExecuteAsync(
                """
                INSERT INTO conversation_memory (user_id, chat_id, query, response_summary, topics, extracted_facts)
                VALUES (@UserId, @ChatId, @Query, @Summary, @Topics::jsonb, @Facts::jsonb)
                """,
                new
                {
                    UserId = userId,
                    ChatId = chatId,
                    Query = TruncateText(query, 500),
                    Summary = summary.Summary,
                    Topics = JsonSerializer.Serialize(summary.Topics),
                    Facts = JsonSerializer.Serialize(summary.Facts)
                });

            // Cleanup old memories (keep last 20)
            await connection.ExecuteAsync(
                """
                DELETE FROM conversation_memory
                WHERE chat_id = @ChatId AND user_id = @UserId
                AND id NOT IN (
                    SELECT id FROM conversation_memory
                    WHERE chat_id = @ChatId AND user_id = @UserId
                    ORDER BY created_at DESC
                    LIMIT 20
                )
                """,
                new { ChatId = chatId, UserId = userId });

            _logger.LogDebug("[Memory] Stored memory for user {UserId}: {Topics}",
                userId, string.Join(", ", summary.Topics));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Memory] Failed to store memory for user {UserId}", userId);
        }
    }

    #endregion

    #region Context Building

    /// <summary>
    /// Build memory context string for LLM prompt
    /// </summary>
    public async Task<string?> BuildMemoryContextAsync(
        long chatId, long userId, string? displayName, CancellationToken ct = default)
    {
        var profile = await GetProfileAsync(chatId, userId, ct);
        var memories = await GetRecentMemoriesAsync(chatId, userId, MaxRecentMemories, ct);

        if (profile == null && memories.Count == 0)
            return null;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== ПАМЯТЬ О ПОЛЬЗОВАТЕЛЕ ===");

        if (profile != null)
        {
            var name = displayName ?? profile.DisplayName ?? profile.Username ?? userId.ToString();
            sb.AppendLine($"Пользователь: {name}");
            sb.AppendLine($"Взаимодействий с ботом: {profile.InteractionCount}");

            if (profile.Facts.Count > 0)
            {
                sb.AppendLine($"Известные факты: {string.Join("; ", profile.Facts.Take(5))}");
            }

            if (profile.Traits.Count > 0)
            {
                sb.AppendLine($"Черты: {string.Join(", ", profile.Traits.Take(3))}");
            }

            if (profile.Interests.Count > 0)
            {
                sb.AppendLine($"Интересы: {string.Join(", ", profile.Interests.Take(5))}");
            }

            if (profile.NotableQuotes.Count > 0)
            {
                sb.AppendLine($"Запомнившиеся цитаты: \"{profile.NotableQuotes.First()}\"");
            }
        }

        if (memories.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Недавние вопросы этого пользователя:");
            foreach (var m in memories.Take(3))
            {
                var ago = GetTimeAgo(m.CreatedAt);
                sb.AppendLine($"  • [{ago}] {TruncateText(m.Query, 100)} → {TruncateText(m.ResponseSummary, 100)}");
            }
        }

        sb.AppendLine("=== КОНЕЦ ПАМЯТИ ===");

        return sb.ToString();
    }

    /// <summary>
    /// Build enhanced memory context using new facts system
    /// </summary>
    public async Task<string?> BuildEnhancedContextAsync(
        long chatId, long userId, string? displayName, string? question = null, CancellationToken ct = default)
    {
        try
        {
            using var connection = await _connectionFactory.CreateConnectionAsync();

            // Get enhanced profile
            var profile = await connection.QueryFirstOrDefaultAsync<EnhancedProfileRecord>(
                """
                SELECT user_id, chat_id, display_name, username,
                       message_count, summary, communication_style, role_in_chat,
                       interests, traits, roast_material
                FROM user_profiles
                WHERE chat_id = @ChatId AND user_id = @UserId
                """,
                new { ChatId = chatId, UserId = userId });

            // Get relevant facts from user_facts
            var facts = await connection.QueryAsync<UserFactRecord>(
                """
                SELECT fact_type, fact_text, confidence
                FROM user_facts
                WHERE chat_id = @ChatId AND user_id = @UserId
                ORDER BY confidence DESC
                LIMIT 15
                """,
                new { ChatId = chatId, UserId = userId });

            var factsList = facts.ToList();

            if (profile == null && factsList.Count == 0)
                return null;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== ПАМЯТЬ О ПОЛЬЗОВАТЕЛЕ ===");

            var name = displayName ?? profile?.display_name ?? $"User_{userId}";
            sb.AppendLine($"Пользователь: {name}");

            if (profile != null)
            {
                if (profile.message_count > 0)
                    sb.AppendLine($"Сообщений в чате: {profile.message_count}");

                if (!string.IsNullOrEmpty(profile.summary))
                    sb.AppendLine($"О пользователе: {profile.summary}");

                if (!string.IsNullOrEmpty(profile.communication_style))
                    sb.AppendLine($"Стиль общения: {profile.communication_style}");

                if (!string.IsNullOrEmpty(profile.role_in_chat))
                    sb.AppendLine($"Роль в чате: {profile.role_in_chat}");

                var interests = ParseJsonArray(profile.interests);
                if (interests.Count > 0)
                    sb.AppendLine($"Интересы: {string.Join(", ", interests.Take(5))}");

                var roastMaterial = ParseJsonArray(profile.roast_material);
                if (roastMaterial.Count > 0)
                    sb.AppendLine($"Над чем подколоть: {string.Join("; ", roastMaterial.Take(3))}");
            }

            if (factsList.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Известные факты:");

                // Filter relevant facts if question provided
                var relevantFacts = question != null
                    ? FilterRelevantFacts(factsList, question)
                    : factsList.Take(8).ToList();

                foreach (var fact in relevantFacts)
                {
                    sb.AppendLine($"  • [{fact.fact_type}] {fact.fact_text}");
                }
            }

            sb.AppendLine("=== КОНЕЦ ПАМЯТИ ===");
            sb.AppendLine();
            sb.AppendLine("ВАЖНО: Используй эту информацию ТОЛЬКО если она РЕЛЕВАНТНА текущему вопросу!");

            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Memory] Failed to build enhanced context for user {UserId}", userId);
            return null;
        }
    }

    /// <summary>
    /// Filter facts relevant to the question
    /// </summary>
    private static List<UserFactRecord> FilterRelevantFacts(List<UserFactRecord> facts, string question)
    {
        var questionLower = question.ToLowerInvariant();
        var keywords = questionLower.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 3)
            .ToHashSet();

        // Score facts by relevance
        var scored = facts.Select(f =>
        {
            var factLower = f.fact_text.ToLowerInvariant();
            var matchCount = keywords.Count(k => factLower.Contains(k));
            return (fact: f, score: matchCount + f.confidence);
        })
        .OrderByDescending(x => x.score)
        .Take(8)
        .Select(x => x.fact)
        .ToList();

        return scored;
    }

    #endregion

    #region LLM Extraction

    private async Task<ProfileExtraction?> ExtractProfileUpdatesAsync(string query, string response, CancellationToken ct)
    {
        try
        {
            var systemPrompt = """
                Извлеки информацию о пользователе из диалога. Отвечай ТОЛЬКО JSON.

                Правила:
                1. facts — конкретные факты о человеке (работа, семья, достижения)
                2. traits — черты характера или поведения
                3. interests — темы, которые интересуют
                4. quotes — запомнившиеся высказывания (если есть)

                КРИТИЧЕСКИ ВАЖНО про имена:
                - Имена, фамилии, никнеймы пиши ТОЧНО как в тексте
                - НЕ "исправляй" и НЕ транслитерируй (Gleb → Глеб ❌)
                - НЕ путай с похожими именами (Bezrukov ≠ Безухов!)
                - Используй оригинальное написание из контекста

                Формат:
                {
                  "facts": ["факт 1", "факт 2"],
                  "traits": ["черта 1"],
                  "interests": ["интерес 1"],
                  "quotes": []
                }

                Если ничего полезного нет — верни пустые массивы.
                """;

            var userPrompt = $"""
                Вопрос пользователя: {query}
                Ответ бота: {TruncateText(response, 500)}

                Извлеки информацию о пользователе:
                """;

            var llmResponse = await _llmRouter.CompleteWithFallbackAsync(
                new LlmRequest
                {
                    SystemPrompt = systemPrompt,
                    UserPrompt = userPrompt,
                    Temperature = 0.1
                },
                preferredTag: null,
                ct: ct);

            var json = llmResponse.Content.Trim();
            if (json.StartsWith("```"))
            {
                var lines = json.Split('\n');
                json = string.Join("\n", lines.Skip(1).TakeWhile(l => !l.StartsWith("```")));
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            return new ProfileExtraction
            {
                Facts = GetJsonStringArray(root, "facts"),
                Traits = GetJsonStringArray(root, "traits"),
                Interests = GetJsonStringArray(root, "interests"),
                Quotes = GetJsonStringArray(root, "quotes")
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Memory] Failed to extract profile updates");
            return null;
        }
    }

    private async Task<MemorySummary> GenerateMemorySummaryAsync(string query, string response, CancellationToken ct)
    {
        try
        {
            var systemPrompt = """
                Создай краткое резюме диалога. Отвечай ТОЛЬКО JSON.

                Формат:
                {
                  "summary": "краткое резюме в 1 предложение",
                  "topics": ["тема1", "тема2"],
                  "facts": ["факт если есть"]
                }
                """;

            var userPrompt = $"""
                Вопрос: {TruncateText(query, 200)}
                Ответ: {TruncateText(response, 300)}
                """;

            var llmResponse = await _llmRouter.CompleteWithFallbackAsync(
                new LlmRequest
                {
                    SystemPrompt = systemPrompt,
                    UserPrompt = userPrompt,
                    Temperature = 0.1
                },
                preferredTag: null,
                ct: ct);

            var json = llmResponse.Content.Trim();
            if (json.StartsWith("```"))
            {
                var lines = json.Split('\n');
                json = string.Join("\n", lines.Skip(1).TakeWhile(l => !l.StartsWith("```")));
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            return new MemorySummary
            {
                Summary = root.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : "",
                Topics = GetJsonStringArray(root, "topics"),
                Facts = GetJsonStringArray(root, "facts")
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Memory] Failed to generate memory summary");
            return new MemorySummary
            {
                Summary = TruncateText(query, 100),
                Topics = new List<string>(),
                Facts = new List<string>()
            };
        }
    }

    #endregion

    #region Helpers

    private static List<string> ParseJsonArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new List<string>();

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    private static List<string> GetJsonStringArray(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return new List<string>();

        return arr.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString() ?? "")
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
    }

    private static List<string> MergeLists(List<string> existing, List<string> newItems, int maxItems)
    {
        var merged = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        foreach (var item in newItems)
        {
            if (!string.IsNullOrWhiteSpace(item))
                merged.Add(item);
        }
        return merged.Take(maxItems).ToList();
    }

    private static string TruncateText(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            return text;
        return text[..(maxLength - 3)] + "...";
    }

    private static string GetTimeAgo(DateTimeOffset time)
    {
        var diff = DateTimeOffset.UtcNow - time;
        if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes}м назад";
        if (diff.TotalHours < 24) return $"{(int)diff.TotalHours}ч назад";
        if (diff.TotalDays < 7) return $"{(int)diff.TotalDays}д назад";
        return time.ToString("dd.MM");
    }

    #endregion
}

#region Models

public class UserProfile
{
    public long UserId { get; set; }
    public long ChatId { get; set; }
    public string? DisplayName { get; set; }
    public string? Username { get; set; }
    public List<string> Facts { get; set; } = new();
    public List<string> Traits { get; set; } = new();
    public List<string> Interests { get; set; } = new();
    public List<string> NotableQuotes { get; set; } = new();
    public int InteractionCount { get; set; }
    public DateTimeOffset? LastInteraction { get; set; }
}

public class ConversationMemory
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public long ChatId { get; set; }
    public string Query { get; set; } = "";
    public string ResponseSummary { get; set; } = "";
    public List<string> Topics { get; set; } = new();
    public List<string> ExtractedFacts { get; set; } = new();
    public DateTimeOffset CreatedAt { get; set; }
}

// Internal classes for Dapper mapping (records don't work well with nullable DateTimeOffset)
internal class UserProfileRecord
{
    public long user_id { get; set; }
    public long chat_id { get; set; }
    public string? display_name { get; set; }
    public string? username { get; set; }
    public string? facts { get; set; }
    public string? traits { get; set; }
    public string? interests { get; set; }
    public string? notable_quotes { get; set; }
    public int interaction_count { get; set; }
    public DateTime? last_interaction { get; set; }
}

internal class ConversationMemoryRecord
{
    public long id { get; set; }
    public long user_id { get; set; }
    public long chat_id { get; set; }
    public string query { get; set; } = "";
    public string response_summary { get; set; } = "";
    public string? topics { get; set; }
    public string? extracted_facts { get; set; }
    public DateTime created_at { get; set; }
}

internal class ProfileExtraction
{
    public List<string> Facts { get; set; } = new();
    public List<string> Traits { get; set; } = new();
    public List<string> Interests { get; set; } = new();
    public List<string> Quotes { get; set; } = new();
}

internal class MemorySummary
{
    public string Summary { get; set; } = "";
    public List<string> Topics { get; set; } = new();
    public List<string> Facts { get; set; } = new();
}

internal class EnhancedProfileRecord
{
    public long user_id { get; set; }
    public long chat_id { get; set; }
    public string? display_name { get; set; }
    public string? username { get; set; }
    public int message_count { get; set; }
    public string? summary { get; set; }
    public string? communication_style { get; set; }
    public string? role_in_chat { get; set; }
    public string? interests { get; set; }
    public string? traits { get; set; }
    public string? roast_material { get; set; }
}

internal class UserFactRecord
{
    public string fact_type { get; set; } = "";
    public string fact_text { get; set; } = "";
    public double confidence { get; set; }
}

#endregion
