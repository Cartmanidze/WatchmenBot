namespace WatchmenBot.Features.Search.Services;

/// <summary>
/// Helper methods for text search: term extraction, Russian stemming
/// </summary>
public static class TextSearchHelpers
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "кто", "что", "где", "когда", "как", "почему", "зачем", "какой", "какая", "какое", "какие",
        "это", "эта", "этот", "эти", "тот", "та", "то", "те", "чем", "про", "об", "обо",
        "ли", "же", "бы", "не", "ни", "да", "нет", "или", "и", "а", "но", "в", "на", "с", "к", "у", "о",
        "за", "из", "по", "до", "от", "для", "при", "без", "над", "под", "между", "через",
        "самый", "самая", "самое", "очень", "много", "мало", "все", "всё", "всех", "весь", "вся",
        "был", "была", "было", "были", "есть", "будет", "можно", "нужно", "надо"
    };

    // Common Russian endings for stemming (ordered by length, longest first)
    private static readonly string[] RussianEndings =
    [
        // Noun endings (plural/genitive/etc)
        "ами", "ями", "ов", "ев", "ей", "ах", "ях", "ом", "ем", "ём",
        "ам", "ям", "ы", "и", "а", "я", "у", "ю", "е", "о",
        // Adjective endings
        "ый", "ий", "ой", "ая", "яя", "ое", "ее", "ые", "ие",
        "ого", "его", "ому", "ему", "ым", "им", "ой", "ей", "ую", "юю",
        // Verb endings
        "ать", "ять", "еть", "ить", "ут", "ют", "ет", "ит", "ешь", "ишь"
    ];

    /// <summary>
    /// Extract meaningful search terms from a query (removes stop words)
    /// </summary>
    public static string ExtractSearchTerms(string query)
    {
        var words = query
            .ToLowerInvariant()
            .Split([' ', ',', '.', '!', '?', ':', ';', '-', '(', ')', '[', ']', '"', '\''], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2 && !StopWords.Contains(w))
            .Distinct()
            .ToList();

        return string.Join(" ", words);
    }

    /// <summary>
    /// Extract words suitable for ILIKE search (3+ chars, expanded with stems)
    /// </summary>
    public static List<string> ExtractIlikeWords(string query, int maxWords = 5)
    {
        var words = query
            .Split([' ', ',', '.', '!', '?', ':', ';', '-', '(', ')', '[', ']', '"', '\''], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 3 && !StopWords.Contains(w)) // Include 3-char words, exclude stop words
            .Select(w => w.ToLowerInvariant())
            .Distinct()
            .ToList();

        // Expand with stems to match different word forms ("няма" → "ням", "делают" → "дела")
        var expanded = ExpandWithStems(words);

        return expanded.Take(maxWords).ToList();
    }

    /// <summary>
    /// Simple Russian stemmer - strips common word endings
    /// </summary>
    public static string GetRussianStem(string word)
    {
        if (string.IsNullOrEmpty(word) || word.Length < 4)
            return word;

        var lowerWord = word.ToLowerInvariant();

        foreach (var ending in RussianEndings)
        {
            if (lowerWord.Length > ending.Length + 2 && lowerWord.EndsWith(ending))
            {
                return lowerWord[..^ending.Length];
            }
        }

        return lowerWord;
    }

    /// <summary>
    /// Expand words with their stems for broader search coverage
    /// </summary>
    public static HashSet<string> ExpandWithStems(IEnumerable<string> words)
    {
        var expanded = new HashSet<string>(words);

        foreach (var word in words.ToList())
        {
            var stem = GetRussianStem(word);
            if (!string.IsNullOrEmpty(stem) && stem.Length >= 3 && stem != word)
            {
                expanded.Add(stem);
            }
        }

        return expanded;
    }

    /// <summary>
    /// Truncate text for logging purposes
    /// </summary>
    public static string TruncateForLog(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            return text ?? string.Empty;
        return text[..(maxLength - 3)] + "...";
    }
}
