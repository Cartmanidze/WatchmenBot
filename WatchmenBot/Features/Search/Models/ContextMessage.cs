namespace WatchmenBot.Features.Search.Models;

/// <summary>
/// A message within a context window
/// </summary>
public class ContextMessage
{
    public long MessageId { get; set; }
    public long ChatId { get; set; }
    public long FromUserId { get; set; }
    public string Author { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public DateTime DateUtc { get; set; }
}
