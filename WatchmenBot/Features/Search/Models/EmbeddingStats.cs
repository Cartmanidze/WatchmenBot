namespace WatchmenBot.Features.Search.Models;

/// <summary>
/// Statistics about embeddings for a chat
/// </summary>
public class EmbeddingStats
{
    public int TotalEmbeddings { get; set; }
    public DateTimeOffset? OldestEmbedding { get; set; }
    public DateTimeOffset? NewestEmbedding { get; set; }
}
