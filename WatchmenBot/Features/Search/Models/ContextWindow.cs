using System.Text;

namespace WatchmenBot.Features.Search.Models;

/// <summary>
/// A window of messages around a center message found by search
/// </summary>
public class ContextWindow
{
    // Maximum length for a single message in context (prevent sticker descriptions from eating all budget)
    private const int MaxMessageLength = 400;

    /// <summary>
    /// The message ID that was originally found by search
    /// </summary>
    public long CenterMessageId { get; set; }

    /// <summary>
    /// All messages in the window (including center message)
    /// </summary>
    public List<ContextMessage> Messages { get; set; } = [];

    /// <summary>
    /// Format the window as readable text
    /// </summary>
    public string ToFormattedText()
    {
        var sb = new StringBuilder();
        foreach (var msg in Messages)
        {
            var isCenter = msg.MessageId == CenterMessageId;
            var marker = isCenter ? "→ " : "  ";
            var time = msg.DateUtc.ToString("HH:mm");

            // Truncate long messages (sticker descriptions, forwards with long text, etc.)
            var text = TruncateMessage(msg.Text, isCenter);

            if (msg.IsForwarded)
            {
                // Format forwarded messages with source attribution
                var sourceLabel = FormatForwardSource(msg.ForwardOriginType, msg.ForwardFromName);
                sb.AppendLine($"{marker}[{time}] 🔄 {msg.Author} переслал от {sourceLabel}: {text}");
            }
            else
            {
                sb.AppendLine($"{marker}[{time}] {msg.Author}: {text}");
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Truncate message text to prevent budget exhaustion.
    /// Center messages get more space, surrounding context is trimmed more aggressively.
    /// </summary>
    private static string TruncateMessage(string text, bool isCenter)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        // Center message (the one found by search) gets more space
        var maxLength = isCenter ? MaxMessageLength * 2 : MaxMessageLength;

        // Skip sticker file references entirely (they're noise)
        if (text.Contains("(stickers/") || text.Contains(".tgs)"))
        {
            // Extract just the emoji/description before the file reference
            var stickerIdx = text.IndexOf("(stickers/", StringComparison.Ordinal);
            if (stickerIdx > 0)
            {
                text = text[..stickerIdx].Trim();
                if (string.IsNullOrWhiteSpace(text))
                    return "[стикер]";
            }
        }

        if (text.Length <= maxLength)
            return text;

        // Truncate at word boundary if possible
        var truncateAt = text.LastIndexOf(' ', maxLength);
        if (truncateAt < maxLength / 2)
            truncateAt = maxLength;

        return text[..truncateAt] + "...";
    }

    /// <summary>
    /// Format forward source for display
    /// </summary>
    private static string FormatForwardSource(string? originType, string? fromName)
    {
        var source = fromName ?? "неизвестного источника";
        return originType switch
        {
            "channel" => $"канала «{source}»",
            "chat" => $"чата «{source}»",
            "user" => source,
            "hidden_user" => source,
            _ => source
        };
    }
}
