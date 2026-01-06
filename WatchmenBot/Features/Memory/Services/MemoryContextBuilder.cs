using System.Text;
using WatchmenBot.Features.Memory.Models;

namespace WatchmenBot.Features.Memory.Services;

/// <summary>
/// Service for building formatted memory context strings for LLM prompts
/// </summary>
public class MemoryContextBuilder(
    ProfileManagementService profileManagement,
    ConversationMemoryService conversationMemory,
    RelationshipService relationshipService)
{
    private const int MaxRecentMemories = 5;

    /// <summary>
    /// Build basic memory context string for LLM prompt
    /// </summary>
    public async Task<string?> BuildMemoryContextAsync(
        long chatId, long userId, string? displayName, CancellationToken ct = default)
    {
        var profile = await profileManagement.GetProfileAsync(chatId, userId, ct);
        var memories = await conversationMemory.GetRecentMemoriesAsync(chatId, userId, MaxRecentMemories, ct);

        if (profile == null && memories.Count == 0)
            return null;

        var sb = new StringBuilder();
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
                var ago = MemoryHelpers.GetTimeAgo(m.CreatedAt);
                sb.AppendLine($"  • [{ago}] {MemoryHelpers.TruncateText(m.Query, 100)} → {MemoryHelpers.TruncateText(m.ResponseSummary, 100)}");
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
        var profile = await profileManagement.GetEnhancedProfileAsync(chatId, userId, ct);
        var facts = await profileManagement.GetUserFactsAsync(chatId, userId, limit: 15, ct);

        if (profile == null && facts.Count == 0)
            return null;

        var sb = new StringBuilder();
        sb.AppendLine("=== ПАМЯТЬ О ПОЛЬЗОВАТЕЛЕ ===");

        var name = displayName ?? profile?.DisplayName ?? $"User_{userId}";
        sb.AppendLine($"Пользователь: {name}");

        // Add gender for proper pronoun usage
        if (profile is { Gender: not Gender.Unknown, GenderConfidence: >= 0.5 })
        {
            var genderStr = profile.Gender == Gender.Male ? "мужской" : "женский";
            sb.AppendLine($"Пол: {genderStr}");
        }

        if (profile != null)
        {
            if (profile.MessageCount > 0)
                sb.AppendLine($"Сообщений в чате: {profile.MessageCount}");

            if (!string.IsNullOrEmpty(profile.Summary))
                sb.AppendLine($"О пользователе: {profile.Summary}");

            if (!string.IsNullOrEmpty(profile.CommunicationStyle))
                sb.AppendLine($"Стиль общения: {profile.CommunicationStyle}");

            if (!string.IsNullOrEmpty(profile.RoleInChat))
                sb.AppendLine($"Роль в чате: {profile.RoleInChat}");

            if (profile.Interests.Count > 0)
                sb.AppendLine($"Интересы: {string.Join(", ", profile.Interests.Take(5))}");

            if (profile.RoastMaterial.Count > 0)
                sb.AppendLine($"Над чем подколоть: {string.Join("; ", profile.RoastMaterial.Take(3))}");
        }

        if (facts.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Известные факты:");

            // Filter relevant facts if question provided
            var relevantFacts = question != null
                ? FilterRelevantFacts(facts, question)
                : facts.Take(8).ToList();

            foreach (var fact in relevantFacts)
            {
                sb.AppendLine($"  • [{fact.FactType}] {fact.FactText}");
            }
        }

        // Add known relationships
        var relationships = await relationshipService.GetUserRelationshipsAsync(chatId, userId, minConfidence: 0.5);
        if (relationships.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Известные связи:");
            foreach (var rel in relationships.Take(10))
            {
                var label = rel.RelationshipLabel ?? rel.RelationshipType;
                sb.AppendLine($"  • {label}: {rel.RelatedPersonName}");
            }
        }

        sb.AppendLine("=== КОНЕЦ ПАМЯТИ ===");
        sb.AppendLine();
        sb.AppendLine("ВАЖНО: Используй эту информацию ТОЛЬКО если она РЕЛЕВАНТНА текущему вопросу!");

        return sb.ToString();
    }

    /// <summary>
    /// Filter facts relevant to the question
    /// </summary>
    private static List<UserFact> FilterRelevantFacts(List<UserFact> facts, string question)
    {
        var questionLower = question.ToLowerInvariant();
        var keywords = questionLower.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 3)
            .ToHashSet();

        // Score facts by relevance
        var scored = facts.Select(f =>
        {
            var factLower = f.FactText.ToLowerInvariant();
            var matchCount = keywords.Count(k => factLower.Contains(k));
            return (fact: f, score: matchCount + f.Confidence);
        })
        .OrderByDescending(x => x.score)
        .Take(8)
        .Select(x => x.fact)
        .ToList();

        return scored;
    }
}
