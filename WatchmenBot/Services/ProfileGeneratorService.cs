using System.Text.Json;
using Dapper;
using WatchmenBot.Infrastructure.Database;
using WatchmenBot.Services.Llm;

namespace WatchmenBot.Services;

/// <summary>
/// Ночной сервис для генерации глубоких профилей пользователей.
/// Раз в сутки (по умолчанию в 03:00) генерирует профиль на основе
/// всех накопленных фактов и выборки сообщений.
/// </summary>
public class ProfileGeneratorService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ProfileGeneratorService> _logger;
    private readonly IConfiguration _configuration;

    private readonly TimeSpan _nightlyTime;
    private readonly int _minMessagesForProfile;
    private readonly int _profileSampleSize;

    public ProfileGeneratorService(
        IServiceProvider serviceProvider,
        ILogger<ProfileGeneratorService> logger,
        IConfiguration configuration)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _configuration = configuration;

        var timeStr = configuration.GetValue<string>("ProfileService:NightlyProfileTime", "03:00");
        _nightlyTime = TimeSpan.Parse(timeStr);
        _minMessagesForProfile = configuration.GetValue<int>("ProfileService:MinMessagesForProfile", 10);
        _profileSampleSize = configuration.GetValue<int>("ProfileService:ProfileSampleSize", 40);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ProfileGeneratorService started. Nightly run at: {Time}", _nightlyTime);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.UtcNow;
                var nextRun = CalculateNextRun(now);
                var delay = nextRun - now;

                _logger.LogInformation("Next profile generation at: {NextRun} UTC (in {Delay})",
                    nextRun, delay);

                await Task.Delay(delay, stoppingToken);

                await GenerateProfilesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in ProfileGeneratorService");
                // Ждём час перед повторной попыткой
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
            }
        }

        _logger.LogInformation("ProfileGeneratorService stopped");
    }

    private DateTime CalculateNextRun(DateTime now)
    {
        var todayRun = now.Date.Add(_nightlyTime);

        if (now < todayRun)
            return todayRun;

        return todayRun.AddDays(1);
    }

    private async Task GenerateProfilesAsync(CancellationToken ct)
    {
        _logger.LogInformation("Starting nightly profile generation");

        using var scope = _serviceProvider.CreateScope();
        var connectionFactory = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>();
        var llmRouter = scope.ServiceProvider.GetRequiredService<LlmRouter>();

        using var connection = await connectionFactory.CreateConnectionAsync();

        // Получаем активных пользователей
        var activeUsers = await connection.QueryAsync<ActiveUser>("""
            SELECT user_id AS UserId, chat_id AS ChatId, display_name AS DisplayName,
                   message_count AS MessageCount, active_hours AS ActiveHours
            FROM user_profiles
            WHERE message_count >= @MinMessages
              AND last_message_at > NOW() - INTERVAL '7 days'
            ORDER BY message_count DESC
            """,
            new { MinMessages = _minMessagesForProfile });

        var users = activeUsers.ToList();
        _logger.LogInformation("Found {Count} active users for profile generation", users.Count);

        var generated = 0;
        foreach (var user in users)
        {
            try
            {
                await GenerateUserProfileAsync(connection, llmRouter, user, ct);
                generated++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to generate profile for user {UserId}", user.UserId);
            }

            // Пауза между запросами к LLM
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }

        _logger.LogInformation("Profile generation complete. Generated {Count}/{Total} profiles",
            generated, users.Count);
    }

    private async Task GenerateUserProfileAsync(
        System.Data.IDbConnection connection,
        LlmRouter llmRouter,
        ActiveUser user,
        CancellationToken ct)
    {
        // Получаем факты
        var facts = await connection.QueryAsync<UserFact>("""
            SELECT fact_type AS FactType, fact_text AS FactText, confidence
            FROM user_facts
            WHERE chat_id = @ChatId AND user_id = @UserId
            ORDER BY confidence DESC
            LIMIT 30
            """,
            new { user.ChatId, user.UserId });

        var factsList = facts.ToList();

        // Получаем выборку сообщений
        var messages = await connection.QueryAsync<string>("""
            SELECT text
            FROM messages
            WHERE chat_id = @ChatId AND from_user_id = @UserId
              AND text IS NOT NULL AND LENGTH(text) > 10
            ORDER BY RANDOM()
            LIMIT @Limit
            """,
            new { user.ChatId, user.UserId, Limit = _profileSampleSize });

        var messagesList = messages.ToList();

        if (messagesList.Count < 5 && factsList.Count < 3)
        {
            _logger.LogDebug("Not enough data for user {UserId}", user.UserId);
            return;
        }

        // Форматируем данные
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
                _logger.LogInformation("Generated profile for {DisplayName} ({UserId})",
                    user.DisplayName, user.UserId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse profile JSON: {Response}", response);
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
        System.Data.IDbConnection connection,
        long chatId,
        long userId,
        GeneratedProfile profile)
    {
        var interestsJson = JsonSerializer.Serialize(profile.MainInterests ?? new List<string>());
        var traitsJson = JsonSerializer.Serialize(profile.PersonalityTraits ?? new List<string>());
        var roastJson = JsonSerializer.Serialize(profile.RoastMaterial ?? new List<string>());

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
