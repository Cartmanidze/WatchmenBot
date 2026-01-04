using WatchmenBot.Features.Memory.Models;

namespace WatchmenBot.Features.Memory.Services;

/// <summary>
/// Service for detecting user gender using hybrid approach:
/// 1. Name-based detection (fast, immediate)
/// 2. LLM-based detection from message context (accurate, requires messages)
/// </summary>
public class GenderDetectionService(ILogger<GenderDetectionService> logger)
{
    /// <summary>
    /// Result of gender detection
    /// </summary>
    public record GenderResult(Gender Gender, double Confidence, string Source);

    #region Name-based Detection

    // Common Russian male names (lowercase)
    private static readonly HashSet<string> MaleNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // А
        "александр", "алексей", "анатолий", "андрей", "антон", "аркадий", "артем", "артём",
        // Б
        "богдан", "борис",
        // В
        "вадим", "валентин", "валерий", "василий", "виктор", "виталий", "владимир", "владислав", "вячеслав",
        // Г
        "геннадий", "георгий", "глеб", "григорий",
        // Д
        "давид", "даниил", "данил", "денис", "дмитрий",
        // Е
        "евгений", "егор",
        // И
        "иван", "игорь", "илья",
        // К
        "кирилл", "константин",
        // Л
        "лев", "леонид",
        // М
        "максим", "марк", "матвей", "михаил",
        // Н
        "никита", "николай",
        // О
        "олег",
        // П
        "павел", "пётр", "петр",
        // Р
        "роман", "руслан",
        // С
        "семён", "семен", "сергей", "станислав", "степан",
        // Т
        "тимофей", "тимур",
        // Ф
        "фёдор", "федор", "филипп",
        // Э
        "эдуард",
        // Ю
        "юрий",
        // Я
        "ярослав",
        // Уменьшительные
        "саша", "саня", "шура", "лёша", "леша", "лёха", "леха", "толя", "андрюха", "антоха", "тоха",
        "тёма", "тема", "боря", "вадик", "валера", "вася", "васёк", "витя", "витёк", "вова", "володя",
        "слава", "гена", "гоша", "жора", "глебушка", "гриша", "давидик", "данька", "даня", "дэн",
        "дениска", "дима", "димон", "женя", "жека", "егорка", "ваня", "ванька", "игорёха", "илюха",
        "илюша", "кирюха", "костя", "костик", "лёва", "лева", "лёня", "леня", "макс", "максик",
        "миша", "мишка", "ник", "никитос", "коля", "колян", "олежа", "олежка", "паша", "пашка",
        "петька", "рома", "ромка", "ромчик", "руся", "сёма", "сема", "серёга", "серега", "серый",
        "стас", "стасик", "стёпа", "степа", "тим", "тимоха", "тимка", "федя", "фил", "эд", "эдик",
        "юра", "юрик", "ярик", "славик"
    };

    // Common Russian female names (lowercase)
    private static readonly HashSet<string> FemaleNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // А
        "александра", "алина", "алиса", "алла", "анастасия", "анна", "антонина",
        // В
        "валентина", "валерия", "варвара", "вера", "вероника", "виктория",
        // Г
        "галина",
        // Д
        "дарья", "диана", "дина",
        // Е
        "ева", "евгения", "екатерина", "елена", "елизавета",
        // З
        "зинаида", "зоя",
        // И
        "инна", "ирина",
        // К
        "карина", "кира", "кристина", "ксения",
        // Л
        "лариса", "лидия", "любовь", "людмила",
        // М
        "маргарита", "марина", "мария", "милана",
        // Н
        "надежда", "наталья", "нина",
        // О
        "оксана", "ольга",
        // П
        "полина",
        // Р
        "раиса", "регина", "римма", "роза",
        // С
        "светлана", "снежана", "софия", "софья",
        // Т
        "тамара", "татьяна",
        // У
        "ульяна",
        // Э
        "элина", "эмма",
        // Ю
        "юлия",
        // Я
        "яна",
        // Уменьшительные
        "саша", "шура", "алинка", "алиска", "аля", "настя", "настюха", "настёна", "аня", "анечка",
        "анюта", "нюра", "нюша", "тоня", "валя", "лера", "варя", "варюша", "вика", "викуся",
        "галя", "галочка", "даша", "дашка", "дашуля", "диана", "динка", "женя", "жека",
        "катя", "катюша", "катерина", "лена", "ленка", "леночка", "лиза", "лизок", "лизочка",
        "зина", "зоенька", "инка", "ира", "ирка", "ируся", "каринка", "кирюша", "кристи",
        "ксюша", "ксюха", "лара", "лида", "люба", "любаша", "люда", "людочка", "мила",
        "рита", "маришка", "маша", "машка", "манька", "надя", "надюша", "наташа", "наталка",
        "оксанка", "оля", "олька", "оленька", "полинка", "рая", "регинка", "роза", "розочка",
        "света", "светик", "светка", "снежок", "соня", "сонечка", "софа", "тома", "таня", "танюша",
        "уля", "эля", "эмочка", "юля", "юлька", "янка", "яночка"
    };

    /// <summary>
    /// Detect gender from display name (fast, name-based)
    /// </summary>
    public GenderResult DetectFromName(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            return new GenderResult(Gender.Unknown, 0, "no_name");

        // Extract first word (likely the name)
        var firstName = displayName.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(firstName))
            return new GenderResult(Gender.Unknown, 0, "empty_name");

        // Clean the name
        firstName = firstName.Trim().ToLowerInvariant();

        // Check male names
        if (MaleNames.Contains(firstName))
        {
            logger.LogDebug("[Gender] Name '{Name}' detected as Male (dictionary)", displayName);
            return new GenderResult(Gender.Male, 0.85, "name_dictionary");
        }

        // Check female names
        if (FemaleNames.Contains(firstName))
        {
            logger.LogDebug("[Gender] Name '{Name}' detected as Female (dictionary)", displayName);
            return new GenderResult(Gender.Female, 0.85, "name_dictionary");
        }

        // Try ending-based heuristics for Russian names
        var genderByEnding = DetectByNameEnding(firstName);
        if (genderByEnding.Gender != Gender.Unknown)
        {
            logger.LogDebug("[Gender] Name '{Name}' detected as {Gender} (ending heuristic)", displayName, genderByEnding.Gender);
            return genderByEnding;
        }

        logger.LogDebug("[Gender] Could not detect gender from name '{Name}'", displayName);
        return new GenderResult(Gender.Unknown, 0, "name_unknown");
    }

    /// <summary>
    /// Detect gender by Russian name endings
    /// </summary>
    private static GenderResult DetectByNameEnding(string name)
    {
        // Female endings (high confidence)
        if (name.EndsWith("а") || name.EndsWith("я"))
        {
            // Exceptions: Никита, Илья, Саша, etc. are male with -а/-я ending
            var maleExceptions = new[] { "никита", "илья", "саша", "кузьма", "фома", "лука" };
            if (maleExceptions.Contains(name))
                return new GenderResult(Gender.Male, 0.7, "name_ending_exception");

            return new GenderResult(Gender.Female, 0.65, "name_ending");
        }

        // Male endings (medium confidence)
        // Most Russian male names end with consonants or -й/-ь
        if (name.EndsWith("й") || name.EndsWith("н") || name.EndsWith("р") ||
            name.EndsWith("л") || name.EndsWith("м") || name.EndsWith("в") ||
            name.EndsWith("д") || name.EndsWith("г") || name.EndsWith("к"))
        {
            return new GenderResult(Gender.Male, 0.6, "name_ending");
        }

        return new GenderResult(Gender.Unknown, 0, "no_pattern");
    }

    #endregion

    #region LLM-based Detection

    /// <summary>
    /// Patterns that indicate male gender in Russian text
    /// </summary>
    private static readonly string[] MalePatterns =
    [
        // Past tense verbs (male form)
        " был ", " сказал ", " пошёл ", " пошел ", " сделал ", " написал ", " подумал ",
        " увидел ", " понял ", " решил ", " хотел ", " мог ", " знал ", " думал ",
        " работал ", " играл ", " смотрел ", " читал ", " ел ", " пил ", " спал ",
        " купил ", " взял ", " дал ", " нашёл ", " нашел ", " потерял ", " забыл ",
        // Self-references
        "я сам ", "сам я", " готов ", " рад ", " доволен ", " уверен ", " согласен ",
        " занят ", " свободен ", " болен ", " здоров "
    ];

    /// <summary>
    /// Patterns that indicate female gender in Russian text
    /// </summary>
    private static readonly string[] FemalePatterns =
    [
        // Past tense verbs (female form)
        " была ", " сказала ", " пошла ", " сделала ", " написала ", " подумала ",
        " увидела ", " поняла ", " решила ", " хотела ", " могла ", " знала ", " думала ",
        " работала ", " играла ", " смотрела ", " читала ", " ела ", " пила ", " спала ",
        " купила ", " взяла ", " дала ", " нашла ", " потеряла ", " забыла ",
        // Self-references
        "я сама ", "сама я", " готова ", " рада ", " довольна ", " уверена ", " согласна ",
        " занята ", " свободна ", " больна ", " здорова "
    ];

    /// <summary>
    /// Detect gender from user messages using pattern matching
    /// </summary>
    public GenderResult DetectFromMessages(IEnumerable<string> messages)
    {
        var messageList = messages.ToList();
        if (messageList.Count == 0)
            return new GenderResult(Gender.Unknown, 0, "no_messages");

        var combinedText = " " + string.Join(" ", messageList).ToLowerInvariant() + " ";

        var maleMatches = MalePatterns.Count(pattern => combinedText.Contains(pattern));
        var femaleMatches = FemalePatterns.Count(pattern => combinedText.Contains(pattern));

        logger.LogDebug("[Gender] Pattern matches: Male={Male}, Female={Female} from {Count} messages",
            maleMatches, femaleMatches, messageList.Count);

        if (maleMatches == 0 && femaleMatches == 0)
            return new GenderResult(Gender.Unknown, 0, "no_patterns");

        var total = maleMatches + femaleMatches;

        if (maleMatches > femaleMatches)
        {
            var confidence = Math.Min(0.95, 0.5 + (maleMatches - femaleMatches) * 0.1 + total * 0.02);
            return new GenderResult(Gender.Male, confidence, "message_patterns");
        }

        if (femaleMatches > maleMatches)
        {
            var confidence = Math.Min(0.95, 0.5 + (femaleMatches - maleMatches) * 0.1 + total * 0.02);
            return new GenderResult(Gender.Female, confidence, "message_patterns");
        }

        // Equal matches - inconclusive
        return new GenderResult(Gender.Unknown, 0.3, "patterns_ambiguous");
    }

    #endregion

    #region Hybrid Detection

    /// <summary>
    /// Hybrid gender detection: combines name-based and message-based approaches
    /// </summary>
    public GenderResult DetectHybrid(string? displayName, IEnumerable<string>? messages = null)
    {
        // Step 1: Try name-based detection
        var nameResult = DetectFromName(displayName);

        // If high confidence from name, use it
        if (nameResult is { Gender: not Gender.Unknown, Confidence: >= 0.8 })
        {
            logger.LogInformation("[Gender] High confidence from name: {Name} → {Gender} ({Conf:P0})",
                displayName, nameResult.Gender, nameResult.Confidence);
            return nameResult;
        }

        // Step 2: Try message-based detection
        var messageList = messages?.ToList();
        if (messageList is { Count: > 0 })
        {
            var messageResult = DetectFromMessages(messageList);

            // If message detection is more confident, use it
            if (messageResult.Confidence > nameResult.Confidence)
            {
                logger.LogInformation("[Gender] Using message-based detection: {Gender} ({Conf:P0})",
                    messageResult.Gender, messageResult.Confidence);
                return messageResult;
            }

            // If both agree, boost confidence
            if (nameResult.Gender != Gender.Unknown && nameResult.Gender == messageResult.Gender)
            {
                var boostedConfidence = Math.Min(0.98, nameResult.Confidence + messageResult.Confidence * 0.3);
                logger.LogInformation("[Gender] Name and messages agree: {Gender} ({Conf:P0})",
                    nameResult.Gender, boostedConfidence);
                return new GenderResult(nameResult.Gender, boostedConfidence, "hybrid_agreement");
            }
        }

        // Return name result (even if low confidence)
        if (nameResult.Gender != Gender.Unknown)
        {
            return nameResult;
        }

        return new GenderResult(Gender.Unknown, 0, "detection_failed");
    }

    #endregion
}
