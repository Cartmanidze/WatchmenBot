using WatchmenBot.Services;

namespace WatchmenBot.Features.Search;

/// <summary>
/// Service for collecting debug information for AskHandler
/// </summary>
public class DebugReportCollector
{
    /// <summary>
    /// Collect debug info for search results with context tracking
    /// </summary>
    public void CollectSearchDebugInfo(
        DebugReport debugReport,
        List<SearchResult> results,
        Dictionary<long, (bool included, string reason)> contextTracker,
        string? personalTarget)
    {
        debugReport.PersonalTarget = personalTarget;
        debugReport.SearchResults = results.Select(r =>
        {
            var (included, reason) = contextTracker.TryGetValue(r.MessageId, out var info)
                ? info
                : (false, "not_tracked");

            return new DebugSearchResult
            {
                Similarity = r.Similarity,
                Distance = r.Distance,
                MessageIds = new[] { r.MessageId },
                Text = r.ChunkText,
                Timestamp = AskHandlerHelpers.ParseTimestamp(r.MetadataJson),
                IsNewsDump = r.IsNewsDump,
                IncludedInContext = included,
                ExcludedReason = reason
            };
        }).ToList();
    }

    /// <summary>
    /// Collect debug info for context
    /// </summary>
    public void CollectContextDebugInfo(
        DebugReport debugReport,
        string? context,
        Dictionary<long, (bool included, string reason)> contextTracker)
    {
        if (context != null)
        {
            debugReport.ContextSent = context;
            debugReport.ContextMessagesCount = contextTracker.Count(kv => kv.Value.included);
            debugReport.ContextTokensEstimate = AskHandlerHelpers.EstimateTokens(context);
        }
    }

    /// <summary>
    /// Collect debug info for search response (confidence, scores, etc)
    /// </summary>
    public void CollectSearchResponseDebugInfo(
        DebugReport debugReport,
        SearchResponse searchResponse)
    {
        debugReport.SearchConfidence = searchResponse.Confidence.ToString();
        debugReport.SearchConfidenceReason = searchResponse.ConfidenceReason;
        debugReport.BestScore = searchResponse.BestScore;
        debugReport.ScoreGap = searchResponse.ScoreGap;
        debugReport.HasFullTextMatch = searchResponse.HasFullTextMatch;
    }
}
