using System.Text;
using WatchmenBot.Features.Search.Models;
using WatchmenBot.Services;

namespace WatchmenBot.Features.Search;

/// <summary>
/// Service for building formatted context from search results
/// Handles context windows, budget management, and deduplication
/// </summary>
public class ContextBuilderService(
    EmbeddingService embeddingService,
    ILogger<ContextBuilderService> logger)
{
    // Token budget for context (roughly 4 chars per token)
    private const int ContextTokenBudget = 4000;
    private const int CharsPerToken = 4;
    private const int ContextCharBudget = ContextTokenBudget * CharsPerToken; // ~16000 chars

    // Context window settings
    private const int ContextWindowSize = 1; // ±1 messages around each found message

    /// <summary>
    /// Build context with context windows around found messages
    /// </summary>
    public async Task<(string context, Dictionary<long, (bool included, string reason)> tracker)> BuildContextWithWindowsAsync(
        long chatId, List<SearchResult> results, CancellationToken ct)
    {
        logger.LogDebug("[BuildContext] Processing {Count} search results with windows (±{Window})",
            results.Count, ContextWindowSize);

        var tracker = new Dictionary<long, (bool included, string reason)>();

        if (results.Count == 0)
            return ("", tracker);

        // Sort by similarity and take top messages
        var sortedResults = results
            .OrderByDescending(r => r.Similarity)
            .Where(r => !string.IsNullOrWhiteSpace(r.ChunkText))
            .Take(10) // Top 10 for context windows
            .ToList();

        foreach (var r in results)
        {
            if (string.IsNullOrWhiteSpace(r.ChunkText))
                tracker[r.MessageId] = (false, "empty_text");
            else if (sortedResults.All(sr => sr.MessageId != r.MessageId))
                tracker[r.MessageId] = (false, "not_in_top10");
        }

        // Separate results: context windows (already formatted) vs messages (need expansion)
        var contextWindowResults = sortedResults.Where(r => r.IsContextWindow).ToList();
        var messageResults = sortedResults.Where(r => !r.IsContextWindow).ToList();

        logger.LogDebug("[BuildContext] Split results: {ContextCount} context windows + {MessageCount} messages to expand",
            contextWindowResults.Count, messageResults.Count);

        // Get context windows only for message results that need expansion
        var expandedWindows = messageResults.Count > 0
            ? await embeddingService.GetMergedContextWindowsAsync(
                chatId, messageResults.Select(r => r.MessageId).ToList(), ContextWindowSize, ct)
            : [];

        // Build context string with budget control
        var sb = new StringBuilder();
        sb.AppendLine("Контекст из чата (сообщения сгруппированы по диалогам):");
        sb.AppendLine();

        var usedChars = sb.Length;
        var includedWindows = 0;
        var seenMessageIds = new HashSet<long>();

        // Process all results by similarity (mix of context windows and expanded messages)
        var allWindowData = new List<(double similarity, long messageId, string text, bool isPreformatted)>();

        // Add pre-formatted context windows
        foreach (var cwr in contextWindowResults)
        {
            allWindowData.Add((cwr.Similarity, cwr.MessageId, cwr.ChunkText, true));
        }

        // Add expanded message windows
        foreach (var window in expandedWindows)
        {
            var matchingResult = messageResults.FirstOrDefault(r => r.MessageId == window.CenterMessageId);
            var similarity = matchingResult?.Similarity ?? 0.0;
            allWindowData.Add((similarity, window.CenterMessageId, window.ToFormattedText(), false));
        }

        // Sort by similarity and process
        foreach (var (_, messageId, windowText, _) in allWindowData.OrderByDescending(w => w.similarity))
        {
            var windowChars = windowText.Length + 50; // +50 for separators

            if (usedChars + windowChars > ContextCharBudget)
            {
                logger.LogDebug("[BuildContext] Budget exceeded, stopping at {Windows} windows", includedWindows);
                break;
            }

            // Mark message as included
            tracker[messageId] = (true, "ok");

            // Add window header
            sb.AppendLine($"--- Диалог #{includedWindows + 1} ---");
            sb.Append(windowText);
            sb.AppendLine();

            usedChars += windowChars;
            includedWindows++;
            seenMessageIds.Add(messageId);
        }

        // Mark remaining messages
        foreach (var r in sortedResults)
        {
            if (!tracker.ContainsKey(r.MessageId))
                tracker[r.MessageId] = (false, "budget_exceeded");
        }

        logger.LogInformation(
            "[BuildContext] Built context: {Windows} windows ({Direct} direct + {Expanded} expanded), {Chars}/{Budget} chars",
            includedWindows, contextWindowResults.Count, expandedWindows.Count, usedChars, ContextCharBudget);

        return (sb.ToString(), tracker);
    }
}
