namespace WatchmenBot.Features.Search.Models;

/// <summary>
/// A message used for building context windows
/// </summary>
public class WindowMessage
{
    public long MessageId { get; set; }
    public long ChatId { get; set; }
    public long FromUserId { get; set; }
    public string Author { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public DateTime DateUtc { get; set; }
}
