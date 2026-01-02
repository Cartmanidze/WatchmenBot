using System.Text.RegularExpressions;

namespace WatchmenBot.Features.Search.Services;

/// <summary>
/// Detects news dump messages (long texts with many links, emojis, etc.)
/// These are typically less relevant for personal questions
/// </summary>
public static partial class NewsDumpDetector
{
    private static readonly string[] NewsPatterns =
    [
        "— СМИ", "Подписаться", "⚡", "❗", "🔴", "BREAKING", "Срочно:", "Источник:"
    ];

    /// <summary>
    /// Detect if text looks like a news dump (long, lots of links, emojis)
    /// </summary>
    public static bool IsNewsDump(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        var indicators = 0;

        // Long text
        if (text.Length > 800) indicators++;

        // Multiple URLs
        var urlCount = UrlRegex().Matches(text).Count;
        if (urlCount >= 2) indicators++;

        // News indicators
        if (NewsPatterns.Any(p => text.Contains(p, StringComparison.OrdinalIgnoreCase)))
            indicators++;

        // Many emojis at the start
        if (text.Length > 0 && char.IsHighSurrogate(text[0]))
            indicators++;

        return indicators >= 2;
    }

    [GeneratedRegex(@"https?://")]
    private static partial Regex UrlRegex();
}
