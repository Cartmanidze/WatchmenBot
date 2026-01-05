using System.Text.RegularExpressions;
using Telegram.Bot.Types;

namespace WatchmenBot.Features.Messages.Services;

/// <summary>
/// Extracts nicknames from message addressing patterns in replies.
/// When user A replies to user B with "Бекс, ты прав", associates "Бекс" with B's user_id.
/// </summary>
public partial class NicknameExtractionService(
    UserAliasService userAliasService,
    ILogger<NicknameExtractionService> logger)
{
    private const int MinNicknameLength = 2;
    private const int MaxNicknameLength = 20;

    /// <summary>
    /// Common words to exclude from nickname extraction (Russian and English).
    /// </summary>
    private static readonly HashSet<string> ExcludedWords = new(StringComparer.OrdinalIgnoreCase)
    {
        // Russian particles, prepositions, pronouns
        "да", "нет", "ок", "ну", "эй", "ой", "ах", "ух", "хм", "ага", "угу",
        "ты", "вы", "он", "она", "они", "мы", "я",
        "это", "то", "что", "как", "так", "вот", "там", "тут", "где",
        "ещё", "еще", "уже", "тоже", "также", "потом", "сейчас", "завтра", "вчера",
        "блин", "черт", "чёрт", "бля", "нах",
        "все", "всё", "кто", "чего", "зачем", "почему", "когда",
        // English
        "ok", "no", "yes", "hey", "hi", "yo", "lol", "omg", "wtf", "bruh",
        "the", "a", "an", "is", "are", "was", "were", "be", "been",
        "you", "me", "he", "she", "it", "we", "they",
        "what", "why", "how", "when", "where", "who",
        // Common chat expressions
        "хаха", "хах", "лол", "кек", "ржу", "пон", "ясн", "оке", "окей", "окс",
        "норм", "збс", "ору", "жиза", "база", "кринж", "имба", "рил", "факт",
        // Short affirmatives
        "ок", "да", "не", "но", "ну", "аа", "оо", "ээ", "мм"
    };

    /// <summary>
    /// Extract nickname from a reply message and associate with the reply target user.
    /// </summary>
    /// <returns>Extracted nickname if found, null otherwise.</returns>
    public async Task<string?> ExtractAndRecordAsync(
        Message message,
        CancellationToken ct = default)
    {
        // Only process messages that are replies with known target
        if (message.ReplyToMessage?.From == null)
            return null;

        var text = message.Text ?? message.Caption;
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var targetUserId = message.ReplyToMessage.From.Id;
        var chatId = message.Chat.Id;

        // Don't extract if replying to self
        if (message.From?.Id == targetUserId)
            return null;

        // Try to extract nickname from addressing pattern
        var nickname = ExtractAddressingNickname(text);

        if (nickname == null)
            return null;

        // Validate the extracted nickname
        if (!IsValidNickname(nickname))
        {
            logger.LogDebug("[NicknameExtract] Rejected: '{Nick}' (validation failed)", nickname);
            return null;
        }

        // Record the nickname
        await userAliasService.RecordAliasAsync(
            chatId,
            targetUserId,
            nickname,
            "nickname",
            ct);

        logger.LogInformation(
            "[NicknameExtract] Recorded '{Nick}' → user {UserId} in chat {ChatId}",
            nickname, targetUserId, chatId);

        return nickname;
    }

    /// <summary>
    /// Extract a nickname from message text using addressing patterns.
    /// </summary>
    public string? ExtractAddressingNickname(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var trimmed = text.Trim();

        // Pattern 1: "Эй Name, ..." or "Эй, Name, ..." (Russian "hey")
        var heyMatch = HeyPatternRegex().Match(trimmed);
        if (heyMatch.Success)
        {
            return CleanNickname(heyMatch.Groups[1].Value);
        }

        // Pattern 2: "Name, text" at start (comma-separated addressing)
        var commaMatch = CommaAddressingRegex().Match(trimmed);
        if (commaMatch.Success)
        {
            var potentialNick = commaMatch.Groups[1].Value;
            var restOfMessage = commaMatch.Groups[2].Value.Trim();
            // Ensure the rest has meaningful content (not just punctuation)
            if (restOfMessage.Length >= 2)
            {
                return CleanNickname(potentialNick);
            }
        }

        // Pattern 3: "Name: text" at start (colon addressing)
        var colonMatch = ColonAddressingRegex().Match(trimmed);
        if (colonMatch.Success)
        {
            var potentialNick = colonMatch.Groups[1].Value;
            var restOfMessage = colonMatch.Groups[2].Value.Trim();
            if (restOfMessage.Length >= 2)
            {
                return CleanNickname(potentialNick);
            }
        }

        // Pattern 4: Short reply that is just a name (< 25 chars, single word, capitalized)
        if (trimmed.Length <= 25 && !trimmed.Contains(' '))
        {
            var singleWordMatch = SingleWordNameRegex().Match(trimmed);
            if (singleWordMatch.Success)
            {
                return CleanNickname(singleWordMatch.Groups[1].Value);
            }
        }

        return null;
    }

    /// <summary>
    /// Validate if extracted string is a valid nickname.
    /// </summary>
    private bool IsValidNickname(string? nickname)
    {
        if (string.IsNullOrWhiteSpace(nickname))
            return false;

        // Length check
        if (nickname.Length < MinNicknameLength || nickname.Length > MaxNicknameLength)
            return false;

        // Not in excluded words
        if (ExcludedWords.Contains(nickname))
            return false;

        // Must contain at least one letter
        if (!nickname.Any(char.IsLetter))
            return false;

        // Should not be all uppercase (likely shouting, not addressing)
        if (nickname.Length > 3 && nickname.All(c => !char.IsLetter(c) || char.IsUpper(c)))
            return false;

        return true;
    }

    /// <summary>
    /// Clean up extracted nickname (trim, remove punctuation).
    /// </summary>
    private static string CleanNickname(string raw)
    {
        var cleaned = raw.Trim();

        // Remove trailing punctuation
        cleaned = cleaned.TrimEnd('!', '?', '.', ':', ';', ',', '-', '_', ')');

        // Remove leading punctuation
        cleaned = cleaned.TrimStart('@', '#', '!', '?', '(');

        return cleaned.Trim();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Compiled regex patterns for performance
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// "Эй Name," or "Эй, Name," pattern (Russian "hey")
    /// </summary>
    [GeneratedRegex(@"^[Ээ]й[,\s]+([А-Яа-яЁёA-Za-z][А-Яа-яЁёA-Za-z0-9_-]*)[,!?\s]", RegexOptions.Compiled)]
    private static partial Regex HeyPatternRegex();

    /// <summary>
    /// "Name, text" - name at start followed by comma and text
    /// Matches Cyrillic and Latin names (2-20 chars)
    /// </summary>
    [GeneratedRegex(@"^([А-Яа-яЁёA-Za-z][А-Яа-яЁёA-Za-z0-9_-]{1,19})\s*,\s*(.+)$", RegexOptions.Compiled | RegexOptions.Singleline)]
    private static partial Regex CommaAddressingRegex();

    /// <summary>
    /// "Name: text" - name at start followed by colon and text
    /// </summary>
    [GeneratedRegex(@"^([А-Яа-яЁёA-Za-z][А-Яа-яЁёA-Za-z0-9_-]{1,19})\s*:\s*(.+)$", RegexOptions.Compiled | RegexOptions.Singleline)]
    private static partial Regex ColonAddressingRegex();

    /// <summary>
    /// Single capitalized word (for short replies that are just addressing)
    /// Must start with uppercase letter (Cyrillic or Latin)
    /// </summary>
    [GeneratedRegex(@"^([А-ЯЁA-Z][а-яёa-z][а-яёa-z0-9_-]*)[!?.,)]*$", RegexOptions.Compiled)]
    private static partial Regex SingleWordNameRegex();
}