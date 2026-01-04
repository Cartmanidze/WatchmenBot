namespace WatchmenBot.Features.Search.Models;

/// <summary>
/// Statistics about context embeddings for a chat
/// </summary>
public class ContextEmbeddingStats
{
    // Per-chat stats (used by GetStatsAsync)
    public int TotalWindows { get; set; }
    public DateTimeOffset? OldestWindow { get; set; }
    public DateTimeOffset? NewestWindow { get; set; }
    public double AvgWindowSize { get; set; }

    // Global indexing stats (used by GetIndexingStatsAsync)
    public long Indexed { get; set; }
    public long EstimatedTotal { get; set; }
    public long TotalMessages { get; set; }
}
