namespace WatchmenBot.Features.Summary.Models;

/// <summary>
/// A message with its timestamp and similarity score for summary processing
/// </summary>
public class MessageWithTime
{
    public string Text { get; set; } = string.Empty;
    public DateTimeOffset Time { get; set; }
    public double Similarity { get; set; }
}
