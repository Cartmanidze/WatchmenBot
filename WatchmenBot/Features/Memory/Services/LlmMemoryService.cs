using WatchmenBot.Features.Memory.Models;

namespace WatchmenBot.Features.Memory.Services;

/// <summary>
/// Service for managing LLM long-term memory and user profiles
/// Facade that delegates to specialized memory services
/// </summary>
public class LlmMemoryService(
    ProfileManagementService profileManagement,
    ConversationMemoryService conversationMemory,
    LlmExtractionService llmExtraction,
    MemoryContextBuilder contextBuilder,
    ILogger<LlmMemoryService> logger)
{
    #region User Profile

    /// <summary>
    /// Get user profile for a specific chat
    /// </summary>
    public Task<UserProfile?> GetProfileAsync(long chatId, long userId, CancellationToken ct = default)
        => profileManagement.GetProfileAsync(chatId, userId, ct);

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
            var extraction = await llmExtraction.ExtractProfileUpdatesAsync(query, response, ct);

            if (extraction == null || (extraction.Facts.Count == 0 && !extraction.Traits.Any()))
            {
                // Just update interaction count
                await profileManagement.IncrementInteractionCountAsync(chatId, userId, displayName, username, ct);
                return;
            }

            await profileManagement.UpdateProfileAsync(chatId, userId, displayName, username, extraction, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Memory] Failed to update profile for user {UserId}", userId);
        }
    }

    #endregion

    #region Conversation Memory

    /// <summary>
    /// Get recent conversation memory for user
    /// </summary>
    public Task<List<ConversationMemory>> GetRecentMemoriesAsync(
        long chatId, long userId, int limit = 5, CancellationToken ct = default)
        => conversationMemory.GetRecentMemoriesAsync(chatId, userId, limit, ct);

    /// <summary>
    /// Store new conversation memory
    /// </summary>
    public async Task StoreMemoryAsync(
        long chatId, long userId, string query, string response, CancellationToken ct = default)
    {
        try
        {
            // Generate summary and extract topics
            var summary = await llmExtraction.GenerateMemorySummaryAsync(query, response, ct);

            await conversationMemory.StoreMemoryAsync(chatId, userId, query, summary, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Memory] Failed to store memory for user {UserId}", userId);
        }
    }

    #endregion

    #region Context Building

    /// <summary>
    /// Build memory context string for LLM prompt
    /// </summary>
    public Task<string?> BuildMemoryContextAsync(
        long chatId, long userId, string? displayName, CancellationToken ct = default)
        => contextBuilder.BuildMemoryContextAsync(chatId, userId, displayName, ct);

    /// <summary>
    /// Build enhanced memory context using new facts system
    /// </summary>
    public Task<string?> BuildEnhancedContextAsync(
        long chatId, long userId, string? displayName, string? question = null, CancellationToken ct = default)
        => contextBuilder.BuildEnhancedContextAsync(chatId, userId, displayName, question, ct);

    #endregion
}
