using System.Text;
using System.Text.RegularExpressions;

namespace WatchmenBot.Features.Webhook.Services;

/// <summary>
/// Sanitizes HTML output for Telegram's supported subset.
/// Telegram supports: b, strong, i, em, u, ins, s, strike, del, a, code, pre, tg-spoiler
/// </summary>
public static partial class TelegramHtmlSanitizer
{
    // Tags supported by Telegram
    private static readonly HashSet<string> SupportedTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "b", "strong", "i", "em", "u", "ins", "s", "strike", "del",
        "a", "code", "pre", "tg-spoiler", "span"
    };

    // Self-closing or void tags to remove entirely
    private static readonly HashSet<string> VoidTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "br", "hr", "img", "input", "meta", "link"
    };

    // Tags that should be converted to supported equivalents
    private static readonly Dictionary<string, string> TagMappings = new(StringComparer.OrdinalIgnoreCase)
    {
        ["strong"] = "b",
        ["em"] = "i",
        ["ins"] = "u",
        ["strike"] = "s",
        ["del"] = "s"
    };

    // Regex patterns
    [GeneratedRegex(@"<(/?)(\w+)([^>]*)>", RegexOptions.Compiled)]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"href\s*=\s*[""']([^""']*)[""']", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex HrefPattern();

    [GeneratedRegex(@"<pre><code[^>]*>", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex PreCodePattern();

    [GeneratedRegex(@"</code></pre>", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex CloseCodePrePattern();

    /// <summary>
    /// Sanitize HTML for Telegram, removing unsupported tags and fixing common issues
    /// </summary>
    public static string Sanitize(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return string.Empty;

        var result = html;

        // Step 1: Fix common LLM output issues
        result = FixCommonIssues(result);

        // Step 2: Process tags - remove unsupported, keep content
        result = ProcessTags(result);

        // Step 3: Balance unclosed tags
        result = BalanceTags(result);

        // Step 4: Final cleanup
        result = CleanupWhitespace(result);

        return result;
    }

    private static string FixCommonIssues(string html)
    {
        var result = html;

        // Fix <pre><code> to just <pre> (Telegram doesn't like nested)
        result = PreCodePattern().Replace(result, "<pre>");
        result = CloseCodePrePattern().Replace(result, "</pre>");

        // Convert markdown-style ** to <b>
        result = Regex.Replace(result, @"\*\*([^*]+)\*\*", "<b>$1</b>");

        // Convert markdown-style * or _ to <i>
        result = Regex.Replace(result, @"(?<!\*)\*([^*]+)\*(?!\*)", "<i>$1</i>");

        // Remove HTML comments
        result = Regex.Replace(result, @"<!--.*?-->", "", RegexOptions.Singleline);

        // Replace <br>, <br/>, <br /> with newline
        result = Regex.Replace(result, @"<br\s*/?\s*>", "\n", RegexOptions.IgnoreCase);

        // Replace <p> and </p> with double newline
        result = Regex.Replace(result, @"<p[^>]*>", "\n", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"</p>", "\n", RegexOptions.IgnoreCase);

        // Replace headers with bold
        result = Regex.Replace(result, @"<h[1-6][^>]*>([^<]*)</h[1-6]>", "<b>$1</b>\n", RegexOptions.IgnoreCase);

        // Replace <li> with bullet
        result = Regex.Replace(result, @"<li[^>]*>", "• ", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"</li>", "\n", RegexOptions.IgnoreCase);

        // Remove <ul>, <ol> tags
        result = Regex.Replace(result, @"</?[uo]l[^>]*>", "", RegexOptions.IgnoreCase);

        // Remove <div>, <span> without special classes
        result = Regex.Replace(result, @"<div[^>]*>", "", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"</div>", "", RegexOptions.IgnoreCase);

        return result;
    }

    private static string ProcessTags(string html)
    {
        return TagPattern().Replace(html, match =>
        {
            var isClosing = match.Groups[1].Value == "/";
            var tagName = match.Groups[2].Value.ToLowerInvariant();
            var attributes = match.Groups[3].Value;

            // Skip void tags entirely
            if (VoidTags.Contains(tagName))
                return string.Empty;

            // Check if tag is supported
            if (!SupportedTags.Contains(tagName))
            {
                // Remove unsupported tag, keep content (return empty for the tag itself)
                return string.Empty;
            }

            // Normalize tag name
            if (TagMappings.TryGetValue(tagName, out var mapped))
                tagName = mapped;

            // For <a> tags, validate and clean href
            if (tagName == "a" && !isClosing)
            {
                var hrefMatch = HrefPattern().Match(attributes);
                if (hrefMatch.Success)
                {
                    var href = hrefMatch.Groups[1].Value;
                    // Only allow http/https links
                    if (href.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                        href.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        return $"<a href=\"{EscapeHtmlAttribute(href)}\">";
                    }
                }
                // Invalid link - remove tag but we need to track for closing
                return string.Empty;
            }

            // For <span>, only keep if it has tg-spoiler class
            if (tagName == "span" && !isClosing)
            {
                if (attributes.Contains("tg-spoiler", StringComparison.OrdinalIgnoreCase))
                {
                    return "<span class=\"tg-spoiler\">";
                }
                return string.Empty;
            }

            return isClosing ? $"</{tagName}>" : $"<{tagName}>";
        });
    }

    private static string BalanceTags(string html)
    {
        var openTags = new Stack<string>();
        var result = new StringBuilder();
        var pos = 0;

        while (pos < html.Length)
        {
            var tagStart = html.IndexOf('<', pos);
            if (tagStart < 0)
            {
                // No more tags, append rest and escape
                result.Append(EscapeContent(html[pos..]));
                break;
            }

            // Append content before tag (escaped)
            if (tagStart > pos)
            {
                result.Append(EscapeContent(html[pos..tagStart]));
            }

            var tagEnd = html.IndexOf('>', tagStart);
            if (tagEnd < 0)
            {
                // Unclosed tag bracket - escape and append rest
                result.Append(EscapeContent(html[tagStart..]));
                break;
            }

            var tag = html[(tagStart + 1)..tagEnd];
            var isClosing = tag.StartsWith('/');
            var tagName = isClosing ? tag[1..].Trim().ToLowerInvariant() : tag.Split(' ')[0].ToLowerInvariant();

            // Remove self-closing slash if present
            tagName = tagName.TrimEnd('/');

            if (string.IsNullOrEmpty(tagName) || !SupportedTags.Contains(tagName))
            {
                // Skip invalid tags
                pos = tagEnd + 1;
                continue;
            }

            if (isClosing)
            {
                // Try to match with open tag
                if (openTags.Count > 0 && openTags.Peek() == tagName)
                {
                    openTags.Pop();
                    result.Append($"</{tagName}>");
                }
                // If no matching open tag, skip this closing tag
            }
            else
            {
                openTags.Push(tagName);
                result.Append(html[tagStart..(tagEnd + 1)]);
            }

            pos = tagEnd + 1;
        }

        // Close any remaining open tags in reverse order
        while (openTags.Count > 0)
        {
            result.Append($"</{openTags.Pop()}>");
        }

        return result.ToString();
    }

    private static string CleanupWhitespace(string html)
    {
        // Collapse multiple newlines to max 2
        var result = Regex.Replace(html, @"\n{3,}", "\n\n");

        // Remove leading/trailing whitespace
        result = result.Trim();

        return result;
    }

    private static string EscapeContent(string content)
    {
        // Only escape if not already escaped
        var result = content;

        // First, decode any existing entities to avoid double-encoding
        result = result.Replace("&amp;", "&")
                       .Replace("&lt;", "<")
                       .Replace("&gt;", ">");

        // Now escape properly
        result = result.Replace("&", "&amp;")
                       .Replace("<", "&lt;")
                       .Replace(">", "&gt;");

        return result;
    }

    private static string EscapeHtmlAttribute(string value)
    {
        return value.Replace("\"", "&quot;")
                    .Replace("'", "&#39;")
                    .Replace("<", "&lt;")
                    .Replace(">", "&gt;");
    }
}
