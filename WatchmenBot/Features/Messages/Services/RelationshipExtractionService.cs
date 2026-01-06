using System.Text.RegularExpressions;
using WatchmenBot.Features.Memory.Services;

namespace WatchmenBot.Features.Messages.Services;

/// <summary>
/// Real-time extraction of relationships from messages using regex patterns.
/// High-confidence patterns only — lower confidence cases go through LLM batch processing.
/// </summary>
public partial class RelationshipExtractionService(
    RelationshipService relationshipService,
    ILogger<RelationshipExtractionService> logger)
{
    /// <summary>
    /// Relationship type mappings from Russian labels to canonical types
    /// </summary>
    private static readonly Dictionary<string, (string Type, string InverseType)> RelationshipMappings = new(StringComparer.OrdinalIgnoreCase)
    {
        // Spouse (symmetric)
        ["жена"] = ("spouse", "spouse"),
        ["муж"] = ("spouse", "spouse"),
        ["супруга"] = ("spouse", "spouse"),
        ["супруг"] = ("spouse", "spouse"),

        // Partner (symmetric)
        ["девушка"] = ("partner", "partner"),
        ["парень"] = ("partner", "partner"),
        ["невеста"] = ("partner", "partner"),
        ["жених"] = ("partner", "partner"),

        // Sibling (symmetric)
        ["брат"] = ("sibling", "sibling"),
        ["сестра"] = ("sibling", "sibling"),
        ["братан"] = ("sibling", "sibling"), // Can be friend too, but regex context helps
        ["братик"] = ("sibling", "sibling"),
        ["сестрёнка"] = ("sibling", "sibling"),
        ["сестренка"] = ("sibling", "sibling"),

        // Parent → Child
        ["мама"] = ("parent", "child"),
        ["папа"] = ("parent", "child"),
        ["мать"] = ("parent", "child"),
        ["отец"] = ("parent", "child"),
        ["батя"] = ("parent", "child"),
        ["маман"] = ("parent", "child"),
        ["мамуля"] = ("parent", "child"),
        ["папуля"] = ("parent", "child"),

        // Child → Parent
        ["сын"] = ("child", "parent"),
        ["дочь"] = ("child", "parent"),
        ["дочка"] = ("child", "parent"),
        ["сынок"] = ("child", "parent"),
        ["сыночек"] = ("child", "parent"),
        ["дочурка"] = ("child", "parent"),

        // Friend (symmetric)
        ["друг"] = ("friend", "friend"),
        ["подруга"] = ("friend", "friend"),
        ["кореш"] = ("friend", "friend"),
        ["корефан"] = ("friend", "friend"),
        ["товарищ"] = ("friend", "friend"),

        // Colleague (symmetric for simplicity)
        ["коллега"] = ("colleague", "colleague"),
        ["начальник"] = ("colleague", "colleague"),
        ["босс"] = ("colleague", "colleague"),
        ["шеф"] = ("colleague", "colleague"),

        // Relative (symmetric for simplicity)
        ["дядя"] = ("relative", "relative"),
        ["тётя"] = ("relative", "relative"),
        ["тетя"] = ("relative", "relative"),
        ["бабушка"] = ("relative", "relative"),
        ["дедушка"] = ("relative", "relative"),
        ["дед"] = ("relative", "relative"),
        ["баба"] = ("relative", "relative"),
        ["внук"] = ("relative", "relative"),
        ["внучка"] = ("relative", "relative"),
        ["племянник"] = ("relative", "relative"),
        ["племянница"] = ("relative", "relative"),
        ["кузен"] = ("relative", "relative"),
        ["кузина"] = ("relative", "relative"),
        ["свекровь"] = ("relative", "relative"),
        ["свёкор"] = ("relative", "relative"),
        ["свекор"] = ("relative", "relative"),
        ["тёща"] = ("relative", "relative"),
        ["теща"] = ("relative", "relative"),
        ["тесть"] = ("relative", "relative"),
        ["зять"] = ("relative", "relative"),
        ["невестка"] = ("relative", "relative"),
    };

    /// <summary>
    /// Extract relationship from message text (fire-and-forget safe)
    /// </summary>
    public async Task ExtractAndSaveAsync(long chatId, long userId, long messageId, string text)
    {
        try
        {
            var extractions = ExtractRelationships(text);

            foreach (var (personName, relationshipLabel, confidence) in extractions)
            {
                if (!RelationshipMappings.TryGetValue(relationshipLabel, out var mapping))
                {
                    logger.LogDebug("[RelExtract] Unknown label: {Label}", relationshipLabel);
                    continue;
                }

                await relationshipService.UpsertRelationshipAsync(
                    chatId,
                    userId,
                    personName,
                    mapping.Type,
                    relationshipLabel,
                    confidence,
                    messageId);

                logger.LogInformation(
                    "[RelExtract] Extracted {Type} ({Label}): {User} → {Person} (conf: {Conf:F2})",
                    mapping.Type, relationshipLabel, userId, personName, confidence);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[RelExtract] Failed to extract relationships from message {MessageId}", messageId);
        }
    }

    /// <summary>
    /// Extract relationships from text using regex patterns.
    /// Returns list of (personName, relationshipLabel, confidence)
    /// </summary>
    public List<(string PersonName, string RelationshipLabel, double Confidence)> ExtractRelationships(string text)
    {
        var results = new List<(string, string, double)>();
        if (string.IsNullOrWhiteSpace(text))
            return results;

        // Pattern 1: "это моя/мой жена/муж Маша" — highest confidence
        foreach (Match match in IntroductionPattern().Matches(text))
        {
            var label = match.Groups["label"].Value.ToLowerInvariant();
            var name = NormalizeName(match.Groups["name"].Value);
            if (IsValidName(name) && RelationshipMappings.ContainsKey(label))
            {
                results.Add((name, label, 0.95));
            }
        }

        // Pattern 2: "моя жена Маша сказала" / "мой брат Петя пришёл"
        foreach (Match match in PossessivePattern().Matches(text))
        {
            var label = match.Groups["label"].Value.ToLowerInvariant();
            var name = NormalizeName(match.Groups["name"].Value);
            if (IsValidName(name) && RelationshipMappings.ContainsKey(label))
            {
                // Slightly lower confidence as context is less explicit
                results.Add((name, label, 0.90));
            }
        }

        // Pattern 3: "Маша — моя жена" / "Петя - мой друг"
        foreach (Match match in ReverseIntroPattern().Matches(text))
        {
            var name = NormalizeName(match.Groups["name"].Value);
            var label = match.Groups["label"].Value.ToLowerInvariant();
            if (IsValidName(name) && RelationshipMappings.ContainsKey(label))
            {
                results.Add((name, label, 0.90));
            }
        }

        // Pattern 4: "познакомьтесь с моей женой Машей" (instrumental case)
        foreach (Match match in MeetPattern().Matches(text))
        {
            var labelInstr = match.Groups["label"].Value.ToLowerInvariant();
            var name = NormalizeName(match.Groups["name"].Value);
            var normalizedLabel = NormalizeInstrumentalCase(labelInstr);
            if (IsValidName(name) && normalizedLabel != null && RelationshipMappings.ContainsKey(normalizedLabel))
            {
                results.Add((name, normalizedLabel, 0.92));
            }
        }

        // Pattern 5: "жена звонит" / "муж написал" — lower confidence, no name
        // Skip these for regex — they go to LLM batch processing

        // Deduplicate by (name, label) keeping highest confidence
        return results
            .GroupBy(r => (r.Item1.ToLowerInvariant(), r.Item2.ToLowerInvariant()))
            .Select(g => g.OrderByDescending(r => r.Item3).First())
            .ToList();
    }

    /// <summary>
    /// Check if extracted name is valid (not a common word, proper capitalization, etc.)
    /// </summary>
    private static bool IsValidName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length < 2)
            return false;

        // Must start with uppercase Cyrillic
        if (!char.IsUpper(name[0]) || !IsCyrillic(name[0]))
            return false;

        // Filter out common false positives
        var lowerName = name.ToLowerInvariant();
        string[] invalidNames = ["сегодня", "завтра", "вчера", "потом", "теперь", "всегда", "никогда"];
        return !invalidNames.Contains(lowerName);
    }

    private static bool IsCyrillic(char c) =>
        c is >= 'А' and <= 'я' or 'Ё' or 'ё';

    private static string NormalizeName(string name) =>
        name.Trim().TrimEnd(',', '.', '!', '?', ':');

    /// <summary>
    /// Convert instrumental case endings to nominative for lookup
    /// </summary>
    private static string? NormalizeInstrumentalCase(string instrumentalForm)
    {
        // женой → жена, мужем → муж, братом → брат, etc.
        return instrumentalForm switch
        {
            "женой" => "жена",
            "мужем" => "муж",
            "братом" => "брат",
            "сестрой" => "сестра",
            "мамой" => "мама",
            "папой" => "папа",
            "другом" => "друг",
            "подругой" => "подруга",
            "девушкой" => "девушка",
            "парнем" => "парень",
            "коллегой" => "коллега",
            "начальником" => "начальник",
            _ => null
        };
    }

    // Compiled regex patterns for performance

    /// <summary>
    /// "это моя/мой жена/муж Маша"
    /// </summary>
    [GeneratedRegex(
        @"это\s+мо[йяеи]\s+(?<label>жена|муж|брат|сестра|мама|папа|девушка|парень|друг|подруга|коллега)\s+(?<name>[А-ЯЁ][а-яё]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex IntroductionPattern();

    /// <summary>
    /// "моя жена Маша сказала"
    /// </summary>
    [GeneratedRegex(
        @"мо[йяеи]\s+(?<label>жена|муж|брат|сестра|братан|мама|папа|батя|девушка|парень|друг|подруга|сын|дочь|дочка)\s+(?<name>[А-ЯЁ][а-яё]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex PossessivePattern();

    /// <summary>
    /// "Маша — моя жена" / "Петя - мой друг"
    /// </summary>
    [GeneratedRegex(
        @"(?<name>[А-ЯЁ][а-яё]+)\s*[-—]\s*мо[йяеи]\s+(?<label>жена|муж|брат|сестра|мама|папа|девушка|парень|друг|подруга)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex ReverseIntroPattern();

    /// <summary>
    /// "познакомьтесь с моей женой Машей"
    /// </summary>
    [GeneratedRegex(
        @"(?:познакомьтесь|знакомьтесь|представляю)\s+(?:вам\s+)?(?:с\s+)?мо[йяеи][мй]?\s+(?<label>женой|мужем|братом|сестрой|девушкой|парнем|другом|подругой)\s+(?<name>[А-ЯЁ][а-яё]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex MeetPattern();
}
