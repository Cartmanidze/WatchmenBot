using System.Text.RegularExpressions;

namespace WatchmenBot.Features.Search;

/// <summary>
/// Normalizes user queries to reduce noise before intent classification and search.
/// Keeps meaning intact while removing invisible characters and excess whitespace.
/// </summary>
public static partial class QueryNormalizer
{
    /// <summary>
    /// Normalize query text for search and intent classification.
    /// </summary>
    /// <param name="input">Raw input text</param>
    /// <param name="removeEmoji">If true, replaces emoji with spaces (default: false to preserve meaning)</param>
    public static string Normalize(string? input, bool removeEmoji = false)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var text = input;

        // Replace common whitespace/control characters with spaces.
        text = text
            .Replace('\u00A0', ' ')
            .Replace('\t', ' ')
            .Replace('\r', ' ')
            .Replace('\n', ' ');

        // Remove zero-width and BOM characters.
        text = text
            .Replace("\u200B", "")
            .Replace("\u200C", "")
            .Replace("\u200D", "")
            .Replace("\uFEFF", "");

        // Normalize punctuation that often appears from copy/paste.
        text = text
            .Replace('\u2013', '-') // en dash
            .Replace('\u2014', '-') // em dash
            .Replace('\u2212', '-') // minus sign
            .Replace('\u2018', '\'')
            .Replace('\u2019', '\'')
            .Replace('\u201C', '"')
            .Replace('\u201D', '"')
            .Replace("\u2026", "..."); // ellipsis

        // Optionally remove emoji (replace with space to preserve word boundaries).
        // By default we keep emoji as they may carry meaning (e.g., "кто 🤡?" → clown context).
        if (removeEmoji)
        {
            text = EmojiRegex().Replace(text, " ");
        }

        // Collapse repeated whitespace and trim.
        text = WhitespaceRegex().Replace(text, " ").Trim();

        return text;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    /// <summary>
    /// Matches most common emoji ranges using Unicode categories and surrogate pairs.
    /// Covers: Symbols, Pictographs, Emoticons, Transport, Misc symbols, Dingbats.
    /// </summary>
    [GeneratedRegex(@"[\u2600-\u26FF\u2700-\u27BF]|\uD83C[\uDF00-\uDFFF]|\uD83D[\uDC00-\uDE4F\uDE80-\uDEFF]|\uD83E[\uDD00-\uDDFF]")]
    private static partial Regex EmojiRegex();
}
