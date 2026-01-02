namespace WatchmenBot.Features.Search.Models;

/// <summary>
/// Search response with confidence assessment
/// </summary>
public class SearchResponse
{
    public List<SearchResult> Results { get; set; } = [];

    /// <summary>
    /// Confidence in results: High, Medium, Low, None
    /// </summary>
    public SearchConfidence Confidence { get; set; }

    /// <summary>
    /// Explanation of why this confidence level
    /// </summary>
    public string? ConfidenceReason { get; set; }

    /// <summary>
    /// Best similarity score
    /// </summary>
    public double BestScore { get; set; }

    /// <summary>
    /// Difference between top-1 and top-5 (gap)
    /// </summary>
    public double ScoreGap { get; set; }

    /// <summary>
    /// Whether there are exact text matches (full-text)
    /// </summary>
    public bool HasFullTextMatch { get; set; }
}
