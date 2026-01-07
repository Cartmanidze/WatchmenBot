using WatchmenBot.Features.Search.Models;
using WatchmenBot.Features.Admin.Services;

namespace WatchmenBot.Features.Search;

/// <summary>
/// Service for handling confidence gates and context building.
/// Now returns shouldContinue=true even for None confidence (fallback to Perplexity).
/// </summary>
public class ConfidenceGateService(
    ContextBuilderService contextBuilder,
    DebugReportCollector debugCollector)
{
    /// <summary>
    /// Process search results: check confidence, build context, determine if LLM should be called.
    /// Returns: (context, confidenceWarning, contextTracker, shouldContinue)
    /// shouldContinue is always true now (fallback to Perplexity when None)
    /// </summary>
    public async Task<(string? context, string? confidenceWarning, Dictionary<long, (bool included, string reason)> tracker, bool shouldContinue)>
        ProcessSearchResultsAsync(
            string command,
            long chatId,
            SearchResponse searchResponse,
            DebugReport debugReport,
            CancellationToken ct)
    {
        var results = searchResponse.Results;
        string? context;
        string? confidenceWarning = null;
        var contextTracker = new Dictionary<long, (bool included, string reason)>();

        // Collect debug info for search response
        debugCollector.CollectSearchResponseDebugInfo(debugReport, searchResponse);

        if (command == "smart")
        {
            // /smart — без контекста, прямой запрос к Perplexity
            context = null;
            foreach (var r in results)
                contextTracker[r.MessageId] = (false, "smart_no_context");

            return (context, confidenceWarning, contextTracker, true);
        }

        // /ask requires context from chat
        if (searchResponse.Confidence == SearchConfidence.None)
        {
            foreach (var r in results)
                contextTracker[r.MessageId] = (false, "confidence_none");

            // FALLBACK: When nothing found in chat, fall back to Perplexity (internet search)
            // Don't send "не нашёл" - instead continue with null context and warning
            confidenceWarning = "🔍 <i>В чате не нашёл, ищу в интернете...</i>\n\n";

            // Return shouldContinue = true to trigger Perplexity fallback
            return (null, confidenceWarning, contextTracker, true);
        }

        if (searchResponse.Confidence == SearchConfidence.Low)
        {
            confidenceWarning = "⚠️ <i>Контекст слабый, ответ может быть неточным</i>\n\n";
        }

        (context, contextTracker) = await contextBuilder.BuildContextWithWindowsAsync(chatId, results, ct);

        return (context, confidenceWarning, contextTracker, true);
    }
}
