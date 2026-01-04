using System.Text.Json;
using Dapper;
using WatchmenBot.Features.Memory.Models;
using WatchmenBot.Infrastructure.Database;

namespace WatchmenBot.Features.Memory.Services;

/// <summary>
/// Service for managing user profiles in database
/// </summary>
public class ProfileManagementService(
    IDbConnectionFactory connectionFactory,
    GenderDetectionService genderDetection,
    ILogger<ProfileManagementService> logger)
{
    // Limit memory items to avoid token overflow
    private const int MaxFacts = 10;
    private const int MaxTraits = 5;
    private const int MaxQuotes = 5;

    /// <summary>
    /// Get user profile for a specific chat
    /// </summary>
    public async Task<UserProfile?> GetProfileAsync(long chatId, long userId, CancellationToken ct = default)
    {
        try
        {
            using var connection = await connectionFactory.CreateConnectionAsync();

            var profile = await connection.QueryFirstOrDefaultAsync<UserProfileRecord>(
                """
                SELECT user_id, chat_id, display_name, username,
                       facts, traits, interests, notable_quotes,
                       interaction_count, last_interaction,
                       gender, gender_confidence
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
                Facts = MemoryHelpers.ParseJsonArray(profile.facts),
                Traits = MemoryHelpers.ParseJsonArray(profile.traits),
                Interests = MemoryHelpers.ParseJsonArray(profile.interests),
                NotableQuotes = MemoryHelpers.ParseJsonArray(profile.notable_quotes),
                InteractionCount = profile.interaction_count,
                LastInteraction = profile.last_interaction.HasValue
                    ? new DateTimeOffset(profile.last_interaction.Value, TimeSpan.Zero)
                    : null,
                Gender = ParseGender(profile.gender),
                GenderConfidence = profile.gender_confidence
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Memory] Failed to get profile for user {UserId} in chat {ChatId}", userId, chatId);
            return null;
        }
    }

    /// <summary>
    /// Get enhanced profile with deep analysis fields
    /// </summary>
    public async Task<EnhancedProfile?> GetEnhancedProfileAsync(long chatId, long userId, CancellationToken ct = default)
    {
        try
        {
            using var connection = await connectionFactory.CreateConnectionAsync();

            var profile = await connection.QueryFirstOrDefaultAsync<EnhancedProfileRecord>(
                """
                SELECT user_id, chat_id, display_name, username,
                       message_count, summary, communication_style, role_in_chat,
                       interests, traits, roast_material,
                       gender, gender_confidence
                FROM user_profiles
                WHERE chat_id = @ChatId AND user_id = @UserId
                """,
                new { ChatId = chatId, UserId = userId });

            if (profile == null)
                return null;

            return new EnhancedProfile
            {
                UserId = profile.user_id,
                ChatId = profile.chat_id,
                DisplayName = profile.display_name,
                Username = profile.username,
                MessageCount = profile.message_count,
                Summary = profile.summary,
                CommunicationStyle = profile.communication_style,
                RoleInChat = profile.role_in_chat,
                Interests = MemoryHelpers.ParseJsonArray(profile.interests),
                Traits = MemoryHelpers.ParseJsonArray(profile.traits),
                RoastMaterial = MemoryHelpers.ParseJsonArray(profile.roast_material),
                Gender = ParseGender(profile.gender),
                GenderConfidence = profile.gender_confidence
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Memory] Failed to get enhanced profile for user {UserId}", userId);
            return null;
        }
    }

    /// <summary>
    /// Get user facts from user_facts table
    /// </summary>
    public async Task<List<UserFact>> GetUserFactsAsync(long chatId, long userId, int limit = 15, CancellationToken ct = default)
    {
        try
        {
            using var connection = await connectionFactory.CreateConnectionAsync();

            var facts = await connection.QueryAsync<UserFactRecord>(
                """
                SELECT fact_type, fact_text, confidence
                FROM user_facts
                WHERE chat_id = @ChatId AND user_id = @UserId
                ORDER BY confidence DESC
                LIMIT @Limit
                """,
                new { ChatId = chatId, UserId = userId, Limit = limit });

            return facts.Select(f => new UserFact
            {
                FactType = f.fact_type,
                FactText = f.fact_text,
                Confidence = f.confidence
            }).ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Memory] Failed to get user facts for user {UserId}", userId);
            return [];
        }
    }

    /// <summary>
    /// Update user profile with extracted information
    /// </summary>
    public async Task UpdateProfileAsync(
        long chatId, long userId, string? displayName, string? username,
        ProfileExtraction extraction, CancellationToken ct = default)
    {
        try
        {
            using var connection = await connectionFactory.CreateConnectionAsync();

            // Get existing profile
            var existing = await GetProfileAsync(chatId, userId, ct);

            // Merge facts (deduplicate and limit)
            var mergedFacts = MemoryHelpers.MergeLists(existing?.Facts ?? [], extraction.Facts, MaxFacts);
            var mergedTraits = MemoryHelpers.MergeLists(existing?.Traits ?? [], extraction.Traits, MaxTraits);
            var mergedInterests = MemoryHelpers.MergeLists(existing?.Interests ?? [], extraction.Interests, MaxFacts);
            var mergedQuotes = MemoryHelpers.MergeLists(existing?.NotableQuotes ?? [], extraction.Quotes, MaxQuotes);

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

            logger.LogInformation("[Memory] Updated profile for {User}: +{Facts} facts, +{Traits} traits",
                displayName ?? userId.ToString(), extraction.Facts.Count, extraction.Traits.Count);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Memory] Failed to update profile for user {UserId}", userId);
        }
    }

    /// <summary>
    /// Increment interaction count without updating profile data
    /// </summary>
    public async Task IncrementInteractionCountAsync(
        long chatId, long userId, string? displayName, string? username, CancellationToken ct = default)
    {
        try
        {
            using var connection = await connectionFactory.CreateConnectionAsync();

            // Try to detect gender from name on first interaction
            var genderResult = genderDetection.DetectFromName(displayName);
            var genderStr = genderResult.Gender != Gender.Unknown ? genderResult.Gender.ToString() : null;

            await connection.ExecuteAsync(
                """
                INSERT INTO user_profiles (user_id, chat_id, display_name, username, interaction_count, last_interaction, gender, gender_confidence)
                VALUES (@UserId, @ChatId, @DisplayName, @Username, 1, NOW(), @Gender, @GenderConfidence)
                ON CONFLICT (chat_id, user_id) DO UPDATE SET
                    display_name = COALESCE(@DisplayName, user_profiles.display_name),
                    username = COALESCE(@Username, user_profiles.username),
                    interaction_count = user_profiles.interaction_count + 1,
                    last_interaction = NOW(),
                    updated_at = NOW(),
                    -- Only update gender if new detection is more confident
                    gender = CASE
                        WHEN @GenderConfidence > COALESCE(user_profiles.gender_confidence, 0)
                        THEN @Gender
                        ELSE user_profiles.gender
                    END,
                    gender_confidence = CASE
                        WHEN @GenderConfidence > COALESCE(user_profiles.gender_confidence, 0)
                        THEN @GenderConfidence
                        ELSE user_profiles.gender_confidence
                    END
                """,
                new
                {
                    UserId = userId,
                    ChatId = chatId,
                    DisplayName = displayName,
                    Username = username,
                    Gender = genderStr,
                    GenderConfidence = genderResult.Confidence
                });
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[Memory] Failed to increment interaction count");
        }
    }

    /// <summary>
    /// Update gender for a user based on their messages
    /// Call this periodically to refine gender detection
    /// </summary>
    public async Task UpdateGenderFromMessagesAsync(
        long chatId, long userId, IEnumerable<string> messages, CancellationToken ct = default)
    {
        try
        {
            // Get existing profile to check current confidence
            var existing = await GetProfileAsync(chatId, userId, ct);

            // Detect gender from messages
            var genderResult = genderDetection.DetectHybrid(existing?.DisplayName, messages);

            // Only update if new detection is more confident
            if (genderResult.Gender == Gender.Unknown ||
                (existing != null && genderResult.Confidence <= existing.GenderConfidence))
            {
                return;
            }

            using var connection = await connectionFactory.CreateConnectionAsync();
            await connection.ExecuteAsync(
                """
                UPDATE user_profiles
                SET gender = @Gender,
                    gender_confidence = @Confidence,
                    updated_at = NOW()
                WHERE chat_id = @ChatId AND user_id = @UserId
                  AND (@Confidence > COALESCE(gender_confidence, 0))
                """,
                new
                {
                    ChatId = chatId,
                    UserId = userId,
                    Gender = genderResult.Gender.ToString(),
                    Confidence = genderResult.Confidence
                });

            logger.LogInformation("[Memory] Updated gender for user {UserId}: {Gender} ({Conf:P0})",
                userId, genderResult.Gender, genderResult.Confidence);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[Memory] Failed to update gender for user {UserId}", userId);
        }
    }

    #region Helpers

    /// <summary>
    /// Parse gender string from database to enum
    /// </summary>
    private static Gender ParseGender(string? genderStr)
    {
        if (string.IsNullOrEmpty(genderStr))
            return Gender.Unknown;

        return genderStr.ToLowerInvariant() switch
        {
            "male" => Gender.Male,
            "female" => Gender.Female,
            _ => Gender.Unknown
        };
    }

    #endregion
}
