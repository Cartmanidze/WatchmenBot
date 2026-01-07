using System.Text.Json;
using System.Text.RegularExpressions;
using WatchmenBot.Features.Llm.Services;

namespace WatchmenBot.Features.Search.Services;

/// <summary>
/// Generates hypothetical questions for messages at indexing time.
/// This bridges the Q→A semantic gap: questions are indexed alongside answers,
/// so searching by question finds the answer.
///
/// Example:
/// Message: "ты создан чтобы обрабатывать тупые вопросы"
/// Generated questions:
/// - "зачем ты создан?"
/// - "какая твоя цель?"
/// - "для чего ты существуешь?"
///
/// Now searching "цель существования" will find the generated question,
/// which links to the original answer.
/// </summary>
public partial class QuestionGenerationService(
    LlmRouter llmRouter,
    ILogger<QuestionGenerationService> logger)
{
    private const string SystemPrompt = """
        Ты генерируешь ВОПРОСЫ, на которые данное сообщение может быть ОТВЕТОМ.

        ЗАДАЧА:
        Придумай 2-3 вопроса, которые человек мог бы задать, и это сообщение было бы хорошим ответом.

        ПРАВИЛА:
        - Вопросы должны быть естественными, как в чате
        - Разные формулировки одного смысла
        - Короткие (до 10 слов)
        - На русском языке
        - НЕ повторяй слова из сообщения дословно
        - Используй синонимы и перефразирование

        ПРИМЕРЫ:

        Сообщение: "я работаю программистом уже 5 лет"
        ["кем ты работаешь?", "чем занимаешься?", "какая у тебя профессия?"]

        Сообщение: "ты создан чтобы обрабатывать тупые вопросы"
        ["зачем ты создан?", "какая твоя цель?", "для чего ты существуешь?"]

        Сообщение: "вчера ходили в кино на новый фильм"
        ["что делали вчера?", "куда ходили?", "как провели время?"]

        Сообщение: "мне 25 лет"
        ["сколько тебе лет?", "какой твой возраст?"]

        Отвечай ТОЛЬКО JSON массивом без markdown:
        ["вопрос1", "вопрос2", "вопрос3"]
        """;

    // Minimum message length to generate questions (user requested: 5 characters)
    private const int MinMessageLength = 5;

    // Stop words and patterns to filter out junk messages
    private static readonly HashSet<string> StopMessages = new(StringComparer.OrdinalIgnoreCase)
    {
        // Short reactions
        "да", "нет", "ок", "окей", "ага", "угу", "ну", "хм", "эм",
        "лол", "кек", "ржу", "хаха", "хех", "хехе", "ахах", "ахаха",
        "ээ", "аа", "ммм", "ууу", "ооо", "эээ",
        "плюс", "минус", "+", "-", "++", "--",
        "спс", "спасибо", "пжлст", "пожалуйста", "благодарю",
        "привет", "здарова", "хай", "хей", "hello", "hi", "yo",
        "пока", "бб", "bb", "bye", "досвидания",
        "круто", "класс", "топ", "огонь", "🔥", "👍", "👎",
        "норм", "нормально", "хорошо", "отлично", "супер",
        "понял", "ясно", "понятно", "окей понял",
        "да?", "нет?", "серьёзно?", "правда?", "реально?",
        "что", "чо", "шо", "а?", "э?",
        "бля", "блин", "чёрт", "damn", "fuck", "shit"
    };

    /// <summary>
    /// Check if a message should have questions generated for it.
    /// Filters out junk: short messages, reactions, stickers, links, forwards.
    /// </summary>
    public bool ShouldGenerateQuestions(string? text, bool isForwarded = false)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        // Minimum length check (user requested: 5 characters)
        if (text.Length < MinMessageLength)
            return false;

        var trimmed = text.Trim().ToLowerInvariant();

        // Skip stop messages (reactions, greetings, etc.)
        if (StopMessages.Contains(trimmed))
            return false;

        // Skip if mostly emojis
        if (IsMostlyEmojis(text))
            return false;

        // Skip URLs and links
        if (UrlRegex().IsMatch(text))
            return false;

        // Skip forwarded messages (they don't represent user's own knowledge)
        if (isForwarded)
            return false;

        // Skip sticker descriptions
        if (text.StartsWith("[стикер", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("[sticker", StringComparison.OrdinalIgnoreCase))
            return false;

        // Skip messages that are just punctuation or special characters
        if (PunctuationOnlyRegex().IsMatch(text))
            return false;

        return true;
    }

    /// <summary>
    /// Generate questions for a message that would have this message as an answer.
    /// </summary>
    public async Task<List<string>> GenerateQuestionsAsync(
        string messageText,
        CancellationToken ct = default)
    {
        if (!ShouldGenerateQuestions(messageText))
            return [];

        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var response = await llmRouter.CompleteAsync(new LlmRequest
            {
                SystemPrompt = SystemPrompt,
                UserPrompt = messageText,
                Temperature = 0.7
            }, ct);

            var questions = ParseQuestions(response.Content);

            sw.Stop();

            logger.LogDebug(
                "[QuestionGen] Message: '{Msg}' → {Count} questions in {Ms}ms: [{Questions}]",
                messageText.Length > 40 ? messageText[..40] + "..." : messageText,
                questions.Count,
                sw.ElapsedMilliseconds,
                string.Join(", ", questions.Take(3)));

            return questions;
        }
        catch (Exception ex)
        {
            sw.Stop();
            logger.LogWarning(ex, "[QuestionGen] Failed for message: {Msg}",
                messageText.Length > 50 ? messageText[..50] + "..." : messageText);
            return [];
        }
    }

    /// <summary>
    /// Batch generate questions for multiple messages.
    /// More efficient than individual calls.
    /// </summary>
    public async Task<Dictionary<string, List<string>>> GenerateQuestionsBatchAsync(
        IEnumerable<string> messages,
        CancellationToken ct = default)
    {
        var results = new Dictionary<string, List<string>>();
        var messagesToProcess = messages
            .Where(m => ShouldGenerateQuestions(m))
            .Distinct()
            .ToList();

        if (messagesToProcess.Count == 0)
            return results;

        // Process sequentially to avoid rate limits
        // Could be parallelized with semaphore if needed
        foreach (var message in messagesToProcess)
        {
            ct.ThrowIfCancellationRequested();

            var questions = await GenerateQuestionsAsync(message, ct);
            if (questions.Count > 0)
            {
                results[message] = questions;
            }
        }

        logger.LogInformation(
            "[QuestionGen] Batch: {Processed}/{Total} messages → {Questions} questions",
            results.Count, messagesToProcess.Count,
            results.Values.Sum(q => q.Count));

        return results;
    }

    #region Private Helpers

    private List<string> ParseQuestions(string content)
    {
        var cleaned = CleanJsonResponse(content);

        try
        {
            var questions = JsonSerializer.Deserialize<List<string>>(cleaned);
            return questions?
                .Where(q => !string.IsNullOrWhiteSpace(q) && q.Length >= 3)
                .Select(q => q.Trim())
                .Distinct()
                .Take(5) // Max 5 questions per message
                .ToList() ?? [];
        }
        catch (JsonException)
        {
            // Try to extract questions from malformed response
            return ExtractQuestionsFromText(content);
        }
    }

    private static string CleanJsonResponse(string content)
    {
        var cleaned = content.Trim();

        // Remove markdown code blocks
        if (cleaned.StartsWith("```"))
        {
            var lines = cleaned.Split('\n');
            cleaned = string.Join("\n", lines.Skip(1).TakeWhile(l => !l.StartsWith("```")));
        }

        // Find JSON array boundaries
        var start = cleaned.IndexOf('[');
        var end = cleaned.LastIndexOf(']');

        if (start >= 0 && end > start)
        {
            cleaned = cleaned[start..(end + 1)];
        }

        return cleaned.Trim();
    }

    private static List<string> ExtractQuestionsFromText(string content)
    {
        // Fallback: extract anything that looks like a question
        var questions = new List<string>();
        var lines = content.Split(['\n', ',', '"'], StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            var trimmed = line.Trim().Trim('[', ']', '"', '\'', ' ');
            if (trimmed.Length >= 5 && trimmed.EndsWith('?'))
            {
                questions.Add(trimmed);
            }
        }

        return questions.Distinct().Take(5).ToList();
    }

    private static bool IsMostlyEmojis(string text)
    {
        var emojiCount = 0;
        var textCount = 0;

        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
                textCount++;
            else if (IsEmoji(c))
                emojiCount++;
        }

        // If more than 70% emojis, skip
        return emojiCount > 0 && textCount == 0 ||
               (emojiCount + textCount > 0 && (double)emojiCount / (emojiCount + textCount) > 0.7);
    }

    private static bool IsEmoji(char c)
    {
        // Simplified emoji detection
        return c >= 0x1F300 || // Miscellaneous Symbols and Pictographs
               (c >= 0x2600 && c <= 0x26FF) || // Misc symbols
               (c >= 0x2700 && c <= 0x27BF); // Dingbats
    }

    [GeneratedRegex(@"https?://|www\.|t\.me/|@\w+\.\w+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"^[\p{P}\p{S}\s]+$")]
    private static partial Regex PunctuationOnlyRegex();

    #endregion
}
