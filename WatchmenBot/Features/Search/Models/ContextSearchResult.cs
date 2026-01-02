namespace WatchmenBot.Features.Search.Models;

/// <summary>
/// Search result from context embeddings
/// </summary>
public class ContextSearchResult
{
    public long Id { get; set; }
    public long ChatId { get; set; }
    public long CenterMessageId { get; set; }
    public long WindowStartId { get; set; }
    public long WindowEndId { get; set; }
    public long[] MessageIds { get; set; } = [];
    public string ContextText { get; set; } = string.Empty;
    public double Similarity { get; set; }
    public double Distance { get; set; }
}
