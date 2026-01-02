namespace WatchmenBot.Features.Summary.Models;

/// <summary>
/// Statistics about chat messages for a time period
/// </summary>
public class ChatStats
{
    public int TotalMessages { get; set; }
    public int UniqueUsers { get; set; }
    public int MessagesWithLinks { get; set; }
    public int MessagesWithMedia { get; set; }
}
