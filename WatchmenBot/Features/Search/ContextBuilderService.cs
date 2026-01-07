using System.Text;
using WatchmenBot.Features.Search.Models;
using WatchmenBot.Features.Search.Services;

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

        // Log top 10 candidates
        logger.LogInformation("[BuildContext] Top 10 candidates:");
        for (var i = 0; i < sortedResults.Count; i++)
        {
            var r = sortedResults[i];
            var textPreview = r.ChunkText.Length > 60 ? r.ChunkText[..60] + "..." : r.ChunkText;
            logger.LogInformation("  [{Index}] id={Id} sim={Sim:F3} isCtxWin={IsCtx} text=\"{Text}\"",
                i + 1, r.MessageId, r.Similarity, r.IsContextWindow, textPreview.Replace("\n", " "));
        }

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

        logger.LogInformation("[BuildContext] Split: {ContextCount} pre-formatted context windows + {MessageCount} messages to expand",
            contextWindowResults.Count, messageResults.Count);

        // Get context windows only for message results that need expansion
        var expandedWindows = messageResults.Count > 0
            ? await embeddingService.GetMergedContextWindowsAsync(
                chatId, messageResults.Select(r => r.MessageId).ToList(), ContextWindowSize, ct)
            : [];

        logger.LogInformation("[BuildContext] Expansion result: requested {Requested} → got {Expanded} windows",
            messageResults.Count, expandedWindows.Count);

        // Build context string with budget control
        var sb = new StringBuilder();
        sb.AppendLine("Контекст из чата (сообщения сгруппированы по диалогам):");
        sb.AppendLine();

        var usedChars = sb.Length;
        var includedWindows = 0;
        var seenMessageIds = new HashSet<long>();

        // Process all results by similarity (mix of context windows and expanded messages)
        // Track source type for detailed logging
        var allWindowData = new List<(double similarity, long messageId, string text, string source)>();

        // Add pre-formatted context windows
        foreach (var cwr in contextWindowResults)
        {
            allWindowData.Add((cwr.Similarity, cwr.MessageId, cwr.ChunkText, "preformatted"));
        }

        // Add expanded message windows
        var expandedMessageIds = new HashSet<long>(expandedWindows.Select(w => w.CenterMessageId));
        foreach (var window in expandedWindows)
        {
            var matchingResult = messageResults.FirstOrDefault(r => r.MessageId == window.CenterMessageId);
            var similarity = matchingResult?.Similarity ?? 0.0;
            var formattedText = window.ToFormattedText();
            allWindowData.Add((similarity, window.CenterMessageId, formattedText, $"expanded({window.Messages.Count}msg)"));

            logger.LogInformation("[BuildContext] Expanded id={Id}: {MsgCount} messages, {Chars} chars",
                window.CenterMessageId, window.Messages.Count, formattedText.Length);
        }

        // FALLBACK: Add messages that couldn't be expanded (e.g., not in messages table)
        // Use their ChunkText directly instead of expanded window
        var fallbackCount = 0;
        foreach (var msg in messageResults)
        {
            if (!expandedMessageIds.Contains(msg.MessageId))
            {
                fallbackCount++;
                logger.LogWarning("[BuildContext] ⚠️ FALLBACK: Message {Id} NOT FOUND in messages table! " +
                    "Using raw ChunkText (sim={Sim:F3}, {Chars} chars): \"{Preview}\"",
                    msg.MessageId, msg.Similarity, msg.ChunkText.Length,
                    msg.ChunkText.Length > 80 ? msg.ChunkText[..80] + "..." : msg.ChunkText);
                allWindowData.Add((msg.Similarity, msg.MessageId, msg.ChunkText, "fallback"));
            }
        }

        if (fallbackCount > 0)
        {
            logger.LogWarning("[BuildContext] {FallbackCount} messages used fallback (not in messages table)",
                fallbackCount);
        }

        // Log all window data before processing
        logger.LogInformation("[BuildContext] All windows to process ({Count} total):", allWindowData.Count);
        foreach (var (sim, msgId, text, source) in allWindowData.OrderByDescending(w => w.similarity))
        {
            logger.LogInformation("  id={Id} sim={Sim:F3} source={Source} chars={Chars}",
                msgId, sim, source, text.Length);
        }

        // Sort by similarity and process with budget tracking
        logger.LogInformation("[BuildContext] Processing windows (budget={Budget} chars):", ContextCharBudget);

        foreach (var (sim, messageId, windowText, source) in allWindowData.OrderByDescending(w => w.similarity))
        {
            var windowChars = windowText.Length + 50; // +50 for separators

            if (usedChars + windowChars > ContextCharBudget)
            {
                logger.LogInformation("[BuildContext] ❌ BUDGET EXCEEDED at window #{Num}: " +
                    "id={Id} needs {Need} chars, have {Have}/{Budget}",
                    includedWindows + 1, messageId, windowChars, ContextCharBudget - usedChars, ContextCharBudget);
                tracker[messageId] = (false, "budget_exceeded");
                continue; // Continue to mark all remaining as budget_exceeded
            }

            // Mark message as included
            tracker[messageId] = (true, "ok");

            // Add window header
            sb.AppendLine($"--- Диалог #{includedWindows + 1} ---");
            sb.Append(windowText);
            sb.AppendLine();

            logger.LogInformation("[BuildContext] ✅ Added #{Num}: id={Id} sim={Sim:F3} source={Source} " +
                "+{Added} chars → {Used}/{Budget}",
                includedWindows + 1, messageId, sim, source, windowChars, usedChars + windowChars, ContextCharBudget);

            usedChars += windowChars;
            includedWindows++;
            seenMessageIds.Add(messageId);
        }

        // Mark remaining messages that weren't processed
        foreach (var r in sortedResults)
        {
            if (!tracker.ContainsKey(r.MessageId))
                tracker[r.MessageId] = (false, "budget_exceeded");
        }

        // Final summary
        var includedIds = tracker.Where(kv => kv.Value.included).Select(kv => kv.Key).ToList();
        var excludedByBudget = tracker.Where(kv => !kv.Value.included && kv.Value.reason == "budget_exceeded").Select(kv => kv.Key).ToList();

        logger.LogInformation(
            "[BuildContext] SUMMARY: {Windows} windows included, {Chars}/{Budget} chars used ({Percent}%)",
            includedWindows, usedChars, ContextCharBudget, usedChars * 100 / ContextCharBudget);

        if (includedIds.Count > 0)
            logger.LogInformation("[BuildContext] Included message ids: [{Ids}]", string.Join(", ", includedIds));

        if (excludedByBudget.Count > 0)
            logger.LogInformation("[BuildContext] Excluded by budget: [{Ids}]", string.Join(", ", excludedByBudget));

        if (fallbackCount > 0)
            logger.LogWarning("[BuildContext] ⚠️ {FallbackCount} messages used fallback - may indicate missing data in messages table",
                fallbackCount);

        return (sb.ToString(), tracker);
    }
}
