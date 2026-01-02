namespace WatchmenBot.Features.Search.Models;

/// <summary>
/// A single search result from vector/text search
/// </summary>
public class SearchResult
{
    public long ChatId { get; set; }
    public long MessageId { get; set; }
    public int ChunkIndex { get; set; }
    public string ChunkText { get; set; } = string.Empty;
    public string? MetadataJson { get; set; }
    public double Similarity { get; set; }
    public double Distance { get; set; }

    /// <summary>
    /// Flag: message looks like a news dump (many links, emojis, long text)
    /// </summary>
    public bool IsNewsDump { get; set; }

    /// <summary>
    /// Flag: ChunkText already contains full context window (from context_embeddings)
    /// </summary>
    public bool IsContextWindow { get; set; }
}
