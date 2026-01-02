using System.Data;
using System.Diagnostics;
using System.Text.Json;
using Dapper;
using WatchmenBot.Infrastructure.Database;
using WatchmenBot.Features.Llm.Services;

namespace WatchmenBot.Features.Profile.Services;

/// <summary>
/// Handler for generating deep user profiles.
/// Runs periodically (nightly) to generate comprehensive profiles from accumulated facts.
/// </summary>
public class ProfileGenerationHandler(
    IDbConnectionFactory connectionFactory,
    LlmRouter llmRouter,
    ProfileOptions options,
    ILogger<ProfileGenerationHandler> logger)
    : IProfileHandler
{
    public string Name => "profiles";

    public bool IsEnabled => true; // Always enabled for now

    public async Task<ProfileStats> GetStatsAsync(CancellationToken ct = default)
    {
        using var connection = await connectionFactory.CreateConnectionAsync();

        // Get count of users who need profile updates
        var pending = await connection.ExecuteScalarAsync<long>("""
            SELECT COUNT(*)
            FROM user_profiles
            WHERE message_count >= @MinMessages
              AND last_message_at > NOW() - INTERVAL '7 days'
            """,
            new { MinMessages = options.MinMessagesForProfile });

        var total = await connection.ExecuteScalarAsync<long>("""
            SELECT COUNT(*)
            FROM user_profiles
            WHERE profile_version > 0
            """);

        return new ProfileStats(
            TotalItems: pending,
            ProcessedItems: total,
            PendingItems: pending);
    }

    public async Task<ProfileResult> ProcessAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            logger.LogInformation("[ProfileHandler] Starting profile generation");

            using var connection = await connectionFactory.CreateConnectionAsync();

            // Get active users who need profiles
            var activeUsers = await connection.QueryAsync<ActiveUser>("""
                SELECT user_id AS UserId, chat_id AS ChatId, display_name AS DisplayName,
                       message_count AS MessageCount, active_hours AS ActiveHours
                FROM user_profiles
                WHERE message_count >= @MinMessages
                  AND last_message_at > NOW() - INTERVAL '7 days'
                ORDER BY message_count DESC
                """,
                new { MinMessages = options.MinMessagesForProfile });

            var users = activeUsers.ToList();
            logger.LogInformation("[ProfileHandler] Found {Count} active users for profile generation", users.Count);

            var generated = 0;
            foreach (var user in users)
            {
                if (ct.IsCancellationRequested)
                    break;

                try
                {
                    await GenerateUserProfileAsync(connection, user, ct);
                    generated++;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "[ProfileHandler] Failed to generate profile for user {UserId}", user.UserId);
                }

                // Delay between LLM requests
                if (options.LlmRequestDelaySeconds > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(options.LlmRequestDelaySeconds), ct);
                }
            }

            sw.Stop();

            logger.LogInformation("[ProfileHandler] Complete: Generated {Count}/{Total} profiles in {Elapsed:F1}s",
                generated, users.Count, sw.Elapsed.TotalSeconds);

            return new ProfileResult(
                ProcessedCount: generated,
                ElapsedTime: sw.Elapsed,
                HasMoreWork: false); // Nightly job, no more work until next run
        }
        catch (Exception ex)
        {
            sw.Stop();
            logger.LogError(ex, "[ProfileHandler] Error in ProcessAsync");
            throw;
        }
    }

    private async Task GenerateUserProfileAsync(
        IDbConnection connection,
        ActiveUser user,
        CancellationToken ct)
    {
        // Get facts
        var facts = await connection.QueryAsync<UserFact>("""
            SELECT fact_type AS FactType, fact_text AS FactText, confidence
            FROM user_facts
            WHERE chat_id = @ChatId AND user_id = @UserId
            ORDER BY confidence DESC
            LIMIT @MaxFacts
            """,
            new { user.ChatId, user.UserId, MaxFacts = options.MaxFactsPerUser });

        var factsList = facts.ToList();

        // Get sample messages
        var messages = await connection.QueryAsync<string>("""
            SELECT text
            FROM messages
            WHERE chat_id = @ChatId AND from_user_id = @UserId
              AND text IS NOT NULL AND LENGTH(text) > 10
            ORDER BY RANDOM()
            LIMIT @Limit
            """,
            new { user.ChatId, user.UserId, Limit = options.ProfileSampleSize });

        var messagesList = messages.ToList();

        if (messagesList.Count < 5 && factsList.Count < 3)
        {
            logger.LogDebug("[ProfileHandler] Not enough data for user {UserId}", user.UserId);
            return;
        }

        // Format data for LLM
        var factsText = factsList.Count > 0
            ? string.Join("\n", factsList.Select(f => $"• [{f.FactType}] {f.FactText} (уверенность: {f.Confidence:P0})"))
            : "Нет накопленных фактов";

        var messagesText = messagesList.Count > 0
            ? string.Join("\n", messagesList.Select((m, i) => $"{i + 1}. {m}"))
            : "Нет сообщений";

        var activeHoursText = FormatActiveHours(user.ActiveHours);

        var prompt = $"""
            Составь профиль пользователя на основе его сообщений и фактов.

            Имя: {user.DisplayName ?? $"User_{user.UserId}"}
            Сообщений за месяц: {user.MessageCount}
            Активные часы: {activeHoursText}

            Известные факты:
            {factsText}

            Выборка сообщений:
            {messagesText}

            Верни JSON с полями:
            - summary: 2-3 предложения о человеке
            - communication_style: стиль общения в 2-3 словах
            - role_in_chat: роль (активист/наблюдатель/тролль/эксперт/...)
            - main_interests: массив интересов
            - personality_traits: массив черт характера
            - roast_material: массив тем для добрых подколов

            ПРАВИЛА:
            - Имена ТОЧНО как указано (НЕ исправляй, НЕ транслитерируй!)
            - roast_material — добрые подколы, не оскорбления
            - Если данных мало — пиши "недостаточно данных" в соответствующих полях
            """;

        var llmResponse = await llmRouter.CompleteWithFallbackAsync(
            new LlmRequest { SystemPrompt = "", UserPrompt = prompt, Temperature = 0.3 },
            preferredTag: null, ct: ct);
        var response = llmResponse.Content;

        try
        {
            var json = ExtractJson(response);
            var profile = JsonSerializer.Deserialize<GeneratedProfile>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (profile != null)
            {
                await SaveProfileAsync(connection, user.ChatId, user.UserId, profile);
                logger.LogInformation("[ProfileHandler] Generated profile for {DisplayName} ({UserId})",
                    user.DisplayName, user.UserId);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[ProfileHandler] Failed to parse profile JSON: {Response}", response);
        }
    }

    private static string FormatActiveHours(string? activeHoursJson)
    {
        if (string.IsNullOrEmpty(activeHoursJson))
            return "нет данных";

        try
        {
            var hours = JsonSerializer.Deserialize<Dictionary<string, int>>(activeHoursJson);
            if (hours == null || hours.Count == 0)
                return "нет данных";

            var topHours = hours
                .OrderByDescending(kv => kv.Value)
                .Take(3)
                .Select(kv => $"{kv.Key}:00")
                .ToList();

            return string.Join(", ", topHours);
        }
        catch
        {
            return "нет данных";
        }
    }

    private static string ExtractJson(string response)
    {
        var start = response.IndexOf('{');
        var end = response.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            return response.Substring(start, end - start + 1);
        }
        return response;
    }

    private static async Task SaveProfileAsync(
        IDbConnection connection,
        long chatId,
        long userId,
        GeneratedProfile profile)
    {
        var interestsJson = JsonSerializer.Serialize(profile.MainInterests ?? []);
        var traitsJson = JsonSerializer.Serialize(profile.PersonalityTraits ?? []);
        var roastJson = JsonSerializer.Serialize(profile.RoastMaterial ?? []);

        await connection.ExecuteAsync("""
            UPDATE user_profiles SET
                summary = @Summary,
                communication_style = @CommunicationStyle,
                role_in_chat = @RoleInChat,
                interests = @Interests::jsonb,
                traits = @Traits::jsonb,
                roast_material = @RoastMaterial::jsonb,
                profile_version = profile_version + 1,
                last_profile_update = NOW(),
                updated_at = NOW()
            WHERE chat_id = @ChatId AND user_id = @UserId
            """,
            new
            {
                ChatId = chatId,
                UserId = userId,
                Summary = profile.Summary ?? "",
                CommunicationStyle = profile.CommunicationStyle ?? "",
                RoleInChat = profile.RoleInChat ?? "",
                Interests = interestsJson,
                Traits = traitsJson,
                RoastMaterial = roastJson
            });
    }

    private class ActiveUser
    {
        public long UserId { get; set; }
        public long ChatId { get; set; }
        public string? DisplayName { get; set; }
        public int MessageCount { get; set; }
        public string? ActiveHours { get; set; }
    }

    private class UserFact
    {
        public string FactType { get; set; } = string.Empty;
        public string FactText { get; set; } = string.Empty;
        public double Confidence { get; set; }
    }

    private class GeneratedProfile
    {
        public string? Summary { get; set; }
        public string? CommunicationStyle { get; set; }
        public string? RoleInChat { get; set; }
        public List<string>? MainInterests { get; set; }
        public List<string>? PersonalityTraits { get; set; }
        public List<string>? RoastMaterial { get; set; }
    }
}
