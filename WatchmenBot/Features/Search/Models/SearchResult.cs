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

    /// <summary>
    /// Flag: this result comes from a Q→A bridge embedding (generated question pointing to this message).
    /// When true, the ChunkText may contain the original message but the similarity was boosted
    /// by matching against a generated question. Used in deduplication to prefer original embeddings.
    /// Note: ChunkIndex &lt; 0 is also used to indicate question embeddings in the database.
    /// </summary>
    public bool IsQuestionEmbedding { get; set; }
}
