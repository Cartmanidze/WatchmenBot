using System.Diagnostics;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;
using WatchmenBot.Extensions;
using WatchmenBot.Features.Memory.Services;
using WatchmenBot.Features.Search.Models;
using WatchmenBot.Features.Admin.Services;
using WatchmenBot.Features.Webhook.Services;
using WatchmenBot.Features.Search;

namespace WatchmenBot.Features.Search.Services;

/// <summary>
/// Core processing service for /ask and /smart commands.
/// Extracted from BackgroundAskWorker to enable direct testing without queue.
/// </summary>
public class AskProcessingService(
    ITelegramBotClient bot,
    LlmMemoryService memoryService,
    DebugService debugService,
    ChatStatusService chatStatusService,
    SearchStrategyService searchStrategy,
    AnswerGeneratorService answerGenerator,
    IntentClassifier intentClassifier,
    NicknameResolverService nicknameResolver,
    DebugReportCollector debugCollector,
    ConfidenceGateService confidenceGate,
    ILogger<AskProcessingService> logger)
{
    /// <summary>
    /// Process /ask or /smart request and send response.
    /// </summary>
    /// <param name="item">Request from queue</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Processing result with timing and confidence</returns>
    public async Task<AskProcessingResult> ProcessAsync(AskQueueItem item, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        logger.LogInformation("[AskProcessing] Processing /{Command}: {Question} from @{User}",
            item.Command, item.Question.Length > 50 ? item.Question[..50] + "..." : item.Question,
            item.AskerUsername ?? item.AskerName);

        // Initialize debug report
        var debugReport = new DebugReport
        {
            Command = item.Command,
            ChatId = item.ChatId,
            Query = item.Question
        };

        // Send typing action (safe: deactivates chat on 403)
        if (!await bot.TrySendChatActionAsync(chatStatusService, item.ChatId, ChatAction.Typing, logger, ct))
        {
            // Chat was deactivated - no point continuing
            sw.Stop();
            return new AskProcessingResult
            {
                Success = false,
                Confidence = SearchConfidence.None,
                ElapsedSeconds = sw.Elapsed.TotalSeconds,
                ResponseSent = false
            };
        }

        var normalizedQuestion = QueryNormalizer.Normalize(item.Question);
        if (string.IsNullOrWhiteSpace(normalizedQuestion))
        {
            // Query was only invisible characters, emoji, or whitespace - cannot search
            logger.LogWarning("[AskProcessing] Query normalized to empty: '{Original}'",
                item.Question.Length > 60 ? item.Question[..60] + "..." : item.Question);

            try
            {
                await bot.SendMessageSafeAsync(
                    chatStatusService,
                    item.ChatId,
                    "❌ Не удалось распознать вопрос. Попробуйте переформулировать.",
                    logger,
                    replyToMessageId: item.ReplyToMessageId,
                    ct: ct);
            }
            catch (ChatDeactivatedException)
            {
                // Chat deactivated - return without error
            }

            sw.Stop();
            return new AskProcessingResult
            {
                Success = false,
                Confidence = SearchConfidence.None,
                ElapsedSeconds = sw.Elapsed.TotalSeconds,
                ResponseSent = true
            };
        }

        if (normalizedQuestion != item.Question)
        {
            logger.LogDebug("[AskProcessing] Query normalized: '{Original}' → '{Normalized}'",
                item.Question.Length > 60 ? item.Question[..60] + "..." : item.Question,
                normalizedQuestion.Length > 60 ? normalizedQuestion[..60] + "..." : normalizedQuestion);
        }

        // OPTIMIZATION: Run Intent Classification and default RAG Fusion in parallel
        // Most queries use default search, so we precompute it while classifying intent
        var intentTask = intentClassifier.ClassifyAsync(
            normalizedQuestion, item.AskerName, item.AskerUsername, ct);

        // Start default RAG Fusion search in parallel (will be used for most queries)
        Task<SearchResponse>? defaultSearchTask = null;
        if (item.Command != "smart")
        {
            defaultSearchTask = searchStrategy.SearchContextOnlyAsync(item.ChatId, normalizedQuestion, ct);
        }

        // Wait for intent classification
        var classified = await intentTask;

        // Resolve nicknames to actual usernames (if personal question or people mentioned)
        var resolvedPeople = new List<string>();
        string? expandedQuestion = null;

        if (classified.MentionedPeople.Count > 0 || classified.IsPersonal)
        {
            var resolutionTasks = classified.MentionedPeople
                .Select(nick => nicknameResolver.ResolveNicknameAsync(item.ChatId, nick, ct))
                .ToList();

            var resolutions = await Task.WhenAll(resolutionTasks);

            foreach (var resolution in resolutions)
            {
                if (resolution.ResolvedName != null && resolution.Confidence > 0.5)
                {
                    resolvedPeople.Add(resolution.ResolvedName);
                    logger.LogInformation("[AskProcessing] Resolved nickname '{Nick}' → '{Name}' (conf: {Conf:F2})",
                        resolution.OriginalNick, resolution.ResolvedName, resolution.Confidence);

                    // Expand the question for better search
                    expandedQuestion ??= normalizedQuestion;
                    expandedQuestion = expandedQuestion.Replace(
                        resolution.OriginalNick,
                        resolution.ResolvedName,
                        StringComparison.OrdinalIgnoreCase);
                }
                else
                {
                    // Keep original if not resolved
                    resolvedPeople.Add(resolution.OriginalNick);
                }
            }

            // Update classified with resolved names
            if (resolvedPeople.Count > 0)
            {
                classified.MentionedPeople = resolvedPeople;
            }
        }

        debugReport.IntentClassification = new IntentClassificationDebug
        {
            Intent = classified.Intent.ToString(),
            Confidence = classified.Confidence,
            Entities = classified.Entities.Select(e => $"{e.Type}: {e.Text}").ToList(),
            MentionedPeople = classified.MentionedPeople,
            TemporalText = classified.TemporalRef?.Text,
            TemporalDays = classified.TemporalRef?.RelativeDays,
            Reasoning = classified.Reasoning + (expandedQuestion != null ? $" [Expanded: {expandedQuestion}]" : "")
        };

        // Use expanded question for search if nicknames were resolved
        var searchQuestion = expandedQuestion ?? normalizedQuestion;

        // Execute search (uses precomputed default search or specialized search based on intent)
        var (memoryContext, searchResponse) = await ExecuteSearchWithPrecomputedAsync(
            item.Command, item.ChatId, item.AskerId, item.AskerName, item.AskerUsername,
            searchQuestion, classified, defaultSearchTask, ct);

        // Handle confidence gate (now always continues - fallback to Perplexity when confidence=None)
        var (context, confidenceWarning, contextTracker, shouldContinue) = await confidenceGate.ProcessSearchResultsAsync(
            item.Command, item.ChatId, searchResponse, debugReport, ct);

        if (!shouldContinue)
        {
            // Early return - already sent "not found" message
            sw.Stop();
            return new AskProcessingResult
            {
                Success = false,
                Confidence = searchResponse.Confidence,
                ElapsedSeconds = sw.Elapsed.TotalSeconds,
                ResponseSent = true // Confidence gate already sent response
            };
        }

        // Collect debug info
        var personalTarget = classified.IsPersonal
            ? (classified.Intent == QueryIntent.PersonalSelf ? "self" : classified.MentionedPeople.FirstOrDefault())
            : null;
        debugCollector.CollectSearchDebugInfo(debugReport, searchResponse.Results, contextTracker, personalTarget);
        debugCollector.CollectContextDebugInfo(debugReport, context, contextTracker);

        // Send typing action again (safe: deactivates chat on 403)
        if (!await bot.TrySendChatActionAsync(chatStatusService, item.ChatId, ChatAction.Typing, logger, ct))
        {
            // Chat was deactivated - no point continuing
            sw.Stop();
            return new AskProcessingResult
            {
                Success = false,
                Confidence = searchResponse.Confidence,
                ElapsedSeconds = sw.Elapsed.TotalSeconds,
                ResponseSent = false
            };
        }

        // Generate answer (with chat mode support)
        var answer = await answerGenerator.GenerateAnswerWithDebugAsync(
            item.Command, item.Question, context, memoryContext, item.AskerName, item.ChatId, debugReport, ct);

        var rawResponse = (confidenceWarning ?? "") + answer;
        var response = TelegramHtmlSanitizer.Sanitize(rawResponse);

        // Send response (safe: handles 403 and HTML fallback)
        try
        {
            await bot.SendHtmlMessageSafeAsync(
                chatStatusService,
                item.ChatId,
                response,
                logger,
                replyToMessageId: item.ReplyToMessageId,
                ct: ct);
        }
        catch (ChatDeactivatedException)
        {
            // Chat was deactivated - job should not retry
            sw.Stop();
            return new AskProcessingResult
            {
                Success = false,
                Confidence = searchResponse.Confidence,
                ElapsedSeconds = sw.Elapsed.TotalSeconds,
                ResponseSent = false
            };
        }

        sw.Stop();

        logger.LogInformation("[AskProcessing] /{Command} answered in {Elapsed:F1}s (confidence: {Conf})",
            item.Command, sw.Elapsed.TotalSeconds, searchResponse.Confidence);

        // Store memory (fire and forget)
        StoreMemoryAsync(item.ChatId, item.AskerId, item.AskerName, item.AskerUsername,
            item.Question, answer);

        // Send debug report
        await debugService.SendDebugReportAsync(debugReport, ct);

        return new AskProcessingResult
        {
            Success = true,
            Confidence = searchResponse.Confidence,
            ElapsedSeconds = sw.Elapsed.TotalSeconds,
            ResponseSent = true,
            Answer = answer
        };
    }

    private async Task<(string? memoryContext, SearchResponse searchResponse)> ExecuteSearchWithPrecomputedAsync(
        string command, long chatId, long askerId, string askerName, string? askerUsername,
        string question, ClassifiedQuery classified, Task<SearchResponse>? precomputedDefaultSearch,
        CancellationToken ct)
    {
        // Start memory context building in parallel
        Task<string?>? memoryTask = null;
        if (command == "ask" && askerId != 0)
        {
            memoryTask = memoryService.BuildEnhancedContextAsync(chatId, askerId, askerName, question, ct);
        }
        else if (command != "smart" && askerId != 0)
        {
            memoryTask = memoryService.BuildMemoryContextAsync(chatId, askerId, askerName, ct);
        }

        // Determine search strategy based on intent
        Task<SearchResponse> searchTask;
        if (command == "smart")
        {
            searchTask = Task.FromResult(new SearchResponse
            {
                Confidence = SearchConfidence.None,
                ConfidenceReason = "Direct query to Perplexity (no RAG)"
            });
        }
        else if (NeedsSpecializedSearch(classified))
        {
            // Personal, temporal, comparison queries need specialized search
            // The precomputed default search will be discarded (but ran in parallel with intent)
            logger.LogInformation("[AskProcessing] Intent requires specialized search: {Intent}", classified.Intent);
            searchTask = searchStrategy.SearchWithIntentAsync(
                chatId, classified, askerUsername ?? askerName, askerName, ct);
        }
        else
        {
            // Default case: use precomputed RAG Fusion search (saves ~20s)
            logger.LogInformation("[AskProcessing] Using precomputed default search (parallel optimization)");
            searchTask = precomputedDefaultSearch ?? searchStrategy.SearchContextOnlyAsync(chatId, question, ct);
        }

        string? memoryContext = null;
        SearchResponse searchResponse;

        if (memoryTask != null)
        {
            await Task.WhenAll(memoryTask, searchTask);
            memoryContext = memoryTask.Result;
            searchResponse = searchTask.Result;
        }
        else
        {
            searchResponse = await searchTask;
        }

        return (memoryContext, searchResponse);
    }

    /// <summary>
    /// Check if intent requires specialized search (not default RAG Fusion)
    /// </summary>
    private static bool NeedsSpecializedSearch(ClassifiedQuery classified)
    {
        return classified.Intent switch
        {
            QueryIntent.PersonalSelf => true,
            QueryIntent.PersonalOther when classified.MentionedPeople.Count > 0 => true,
            QueryIntent.Temporal when classified.HasTemporal => true,
            QueryIntent.Comparison when classified.Entities.Count >= 2 => true,
            QueryIntent.MultiEntity when classified.MentionedPeople.Count >= 2 => true,
            _ => false
        };
    }

    private void StoreMemoryAsync(long chatId, long askerId, string askerName, string? askerUsername,
        string question, string answer)
    {
        if (askerId == 0) return;

        _ = Task.Run(async () =>
        {
            try
            {
                await memoryService.StoreMemoryAsync(chatId, askerId, question, answer, CancellationToken.None);
                await memoryService.UpdateProfileFromInteractionAsync(
                    chatId, askerId, askerName, askerUsername, question, answer, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[AskProcessing] Failed to store memory for user {UserId}", askerId);
            }
        });
    }

}

/// <summary>
/// Result of processing /ask or /smart request
/// </summary>
public class AskProcessingResult
{
    public bool Success { get; init; }
    public SearchConfidence Confidence { get; init; }
    public double ElapsedSeconds { get; init; }
    public bool ResponseSent { get; init; }
    public string? Answer { get; init; }
}
