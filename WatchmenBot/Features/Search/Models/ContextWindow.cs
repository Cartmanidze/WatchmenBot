using System.Text;

namespace WatchmenBot.Features.Search.Models;

/// <summary>
/// A window of messages around a center message found by search
/// </summary>
public class ContextWindow
{
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

            if (msg.IsForwarded)
            {
                // Format forwarded messages with source attribution
                var sourceLabel = FormatForwardSource(msg.ForwardOriginType, msg.ForwardFromName);
                sb.AppendLine($"{marker}[{time}] 🔄 {msg.Author} переслал от {sourceLabel}: {msg.Text}");
            }
            else
            {
                sb.AppendLine($"{marker}[{time}] {msg.Author}: {msg.Text}");
            }
        }
        return sb.ToString();
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
