namespace WatchmenBot.Features.Search.Models;

/// <summary>
/// Statistics about context embeddings for a chat
/// </summary>
public class ContextEmbeddingStats
{
    public int TotalWindows { get; set; }
    public DateTimeOffset? OldestWindow { get; set; }
    public DateTimeOffset? NewestWindow { get; set; }
    public double AvgWindowSize { get; set; }
}
