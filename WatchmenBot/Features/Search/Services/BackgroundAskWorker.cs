using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using WatchmenBot.Features.Memory.Services;
using WatchmenBot.Features.Search.Models;
using WatchmenBot.Features.Admin.Services;
using WatchmenBot.Features.Webhook.Services;

namespace WatchmenBot.Features.Search.Services;

/// <summary>
/// Background worker for /ask and /smart commands.
/// Decoupled from webhook timeout - can run as long as needed.
/// </summary>
public partial class BackgroundAskWorker(
    AskQueueService queue,
    ITelegramBotClient bot,
    IServiceProvider serviceProvider,
    ILogger<BackgroundAskWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("[BackgroundAsk] Worker started");

        await foreach (var item in queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessAskRequestAsync(item, stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[BackgroundAsk] Failed to process /{Command} for chat {ChatId}",
                    item.Command, item.ChatId);

                try
                {
                    await bot.SendMessage(
                        chatId: item.ChatId,
                        text: "Произошла ошибка при обработке вопроса. Попробуйте позже.",
                        replyParameters: new ReplyParameters { MessageId = item.ReplyToMessageId },
                        cancellationToken: stoppingToken);
                }
                catch
                {
                    // Ignore send errors
                }
            }
        }

        logger.LogInformation("[BackgroundAsk] Worker stopped");
    }

    private async Task ProcessAskRequestAsync(AskQueueItem item, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        logger.LogInformation("[BackgroundAsk] Processing /{Command}: {Question} from @{User}",
            item.Command, item.Question.Length > 50 ? item.Question[..50] + "..." : item.Question,
            item.AskerUsername ?? item.AskerName);

        using var scope = serviceProvider.CreateScope();

        var memoryService = scope.ServiceProvider.GetRequiredService<LlmMemoryService>();
        var debugService = scope.ServiceProvider.GetRequiredService<DebugService>();
        var searchStrategy = scope.ServiceProvider.GetRequiredService<SearchStrategyService>();
        var answerGenerator = scope.ServiceProvider.GetRequiredService<AnswerGeneratorService>();
        var intentClassifier = scope.ServiceProvider.GetRequiredService<IntentClassifier>();
        var debugCollector = scope.ServiceProvider.GetRequiredService<DebugReportCollector>();
        var confidenceGate = scope.ServiceProvider.GetRequiredService<ConfidenceGateService>();

        // Initialize debug report
        var debugReport = new DebugReport
        {
            Command = item.Command,
            ChatId = item.ChatId,
            Query = item.Question
        };

        // Send typing action
        await bot.SendChatAction(item.ChatId, ChatAction.Typing, cancellationToken: ct);

        // OPTIMIZATION: Run Intent Classification and default RAG Fusion in parallel
        // Most queries use default search, so we precompute it while classifying intent
        var intentTask = intentClassifier.ClassifyAsync(
            item.Question, item.AskerName, item.AskerUsername, ct);

        // Start default RAG Fusion search in parallel (will be used for most queries)
        Task<SearchResponse>? defaultSearchTask = null;
        if (item.Command != "smart")
        {
            defaultSearchTask = searchStrategy.SearchContextOnlyAsync(item.ChatId, item.Question, ct);
        }

        // Wait for intent classification
        var classified = await intentTask;

        debugReport.IntentClassification = new IntentClassificationDebug
        {
            Intent = classified.Intent.ToString(),
            Confidence = classified.Confidence,
            Entities = classified.Entities.Select(e => $"{e.Type}: {e.Text}").ToList(),
            MentionedPeople = classified.MentionedPeople,
            TemporalText = classified.TemporalRef?.Text,
            TemporalDays = classified.TemporalRef?.RelativeDays,
            Reasoning = classified.Reasoning
        };

        // Execute search (uses precomputed default search or specialized search based on intent)
        var (memoryContext, searchResponse) = await ExecuteSearchWithPrecomputedAsync(
            item.Command, item.ChatId, item.AskerId, item.AskerName, item.AskerUsername,
            item.Question, classified, defaultSearchTask, memoryService, searchStrategy, ct);

        // Handle confidence gate
        var (context, confidenceWarning, contextTracker, shouldContinue) = await confidenceGate.ProcessSearchResultsAsync(
            item.Command, item.ChatId, item.ReplyToMessageId, searchResponse, debugReport, ct);

        if (!shouldContinue)
        {
            // Early return - already sent "not found" message
            return;
        }

        // Collect debug info
        var personalTarget = classified.IsPersonal
            ? (classified.Intent == QueryIntent.PersonalSelf ? "self" : classified.MentionedPeople.FirstOrDefault())
            : null;
        debugCollector.CollectSearchDebugInfo(debugReport, searchResponse.Results, contextTracker, personalTarget);
        debugCollector.CollectContextDebugInfo(debugReport, context, contextTracker);

        // Send typing action again (long operation)
        await bot.SendChatAction(item.ChatId, ChatAction.Typing, cancellationToken: ct);

        // Generate answer
        var answer = await answerGenerator.GenerateAnswerWithDebugAsync(
            item.Command, item.Question, context, memoryContext, item.AskerName, debugReport, ct);

        var rawResponse = (confidenceWarning ?? "") + answer;
        var response = TelegramHtmlSanitizer.Sanitize(rawResponse);

        // Send response
        try
        {
            await bot.SendMessage(
                chatId: item.ChatId,
                text: response,
                parseMode: ParseMode.Html,
                linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true },
                replyParameters: new ReplyParameters { MessageId = item.ReplyToMessageId },
                cancellationToken: ct);
        }
        catch (Telegram.Bot.Exceptions.ApiRequestException ex) when (ex.Message.Contains("can't parse entities"))
        {
            logger.LogWarning("[BackgroundAsk] HTML parsing failed, sending as plain text");
            var plainText = HtmlTagRegex().Replace(response, "");
            await bot.SendMessage(
                chatId: item.ChatId,
                text: plainText,
                linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true },
                replyParameters: new ReplyParameters { MessageId = item.ReplyToMessageId },
                cancellationToken: ct);
        }

        sw.Stop();

        logger.LogInformation("[BackgroundAsk] /{Command} answered in {Elapsed:F1}s (confidence: {Conf})",
            item.Command, sw.Elapsed.TotalSeconds, searchResponse.Confidence);

        // Store memory (fire and forget)
        StoreMemoryAsync(item.ChatId, item.AskerId, item.AskerName, item.AskerUsername,
            item.Question, answer, memoryService);

        // Send debug report
        await debugService.SendDebugReportAsync(debugReport, ct);
    }

    private async Task<(string? memoryContext, SearchResponse searchResponse)> ExecuteSearchWithPrecomputedAsync(
        string command, long chatId, long askerId, string askerName, string? askerUsername,
        string question, ClassifiedQuery classified, Task<SearchResponse>? precomputedDefaultSearch,
        LlmMemoryService memoryService, SearchStrategyService searchStrategy,
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
            logger.LogInformation("[BackgroundAsk] Intent requires specialized search: {Intent}", classified.Intent);
            searchTask = searchStrategy.SearchWithIntentAsync(
                chatId, classified, askerUsername ?? askerName, askerName, ct);
        }
        else
        {
            // Default case: use precomputed RAG Fusion search (saves ~20s)
            logger.LogInformation("[BackgroundAsk] Using precomputed default search (parallel optimization)");
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
        string question, string answer, LlmMemoryService memoryService)
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
                logger.LogWarning(ex, "[BackgroundAsk] Failed to store memory for user {UserId}", askerId);
            }
        });
    }

    [System.Text.RegularExpressions.GeneratedRegex("<[^>]+>")]
    private static partial System.Text.RegularExpressions.Regex HtmlTagRegex();
}
