using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using WatchmenBot.Features.Memory.Services;
using WatchmenBot.Features.Search.Models;
using WatchmenBot.Infrastructure.Settings;
using WatchmenBot.Features.Messages.Services;
using WatchmenBot.Features.Admin.Services;
using WatchmenBot.Features.Webhook.Services;

namespace WatchmenBot.Features.Search;

/// <summary>
/// Handler for /ask and /smart commands
/// Orchestrates search, context building, and answer generation
/// </summary>
public class AskHandler(
    ITelegramBotClient bot,
    LlmMemoryService memoryService,
    DebugService debugService,
    SearchStrategyService searchStrategy,
    AnswerGeneratorService answerGenerator,
    PersonalQuestionDetector personalDetector,
    DebugReportCollector debugCollector,
    ConfidenceGateService confidenceGate,
    ILogger<AskHandler> logger)
{
    /// <summary>
    /// Handle /ask command (дерзкий ответ с подъёбкой)
    /// </summary>
    public Task HandleAsync(Message message, CancellationToken ct)
        => HandleAsync(message, "ask", ct);

    /// <summary>
    /// Handle /smart command (серьёзный вопрос)
    /// </summary>
    public Task HandleQuestionAsync(Message message, CancellationToken ct)
        => HandleAsync(message, "smart", ct);

    private async Task HandleAsync(Message message, string command, CancellationToken ct)
    {
        var chatId = message.Chat.Id;
        var question = AskHandlerHelpers.ParseQuestion(message.Text);

        if (string.IsNullOrWhiteSpace(question))
        {
            await SendHelpTextAsync(chatId, command, message.MessageId, ct);
            return;
        }

        // Initialize debug report
        var debugReport = new DebugReport
        {
            Command = command,
            ChatId = chatId,
            Query = question
        };

        try
        {
            await bot.SendChatAction(chatId, ChatAction.Typing, cancellationToken: ct);

            logger.LogInformation("[{Command}] Question: {Question} in chat {ChatId}", command.ToUpper(), question, chatId);

            // Get asker's info for personal retrieval
            var askerName = AskHandlerHelpers.GetDisplayName(message.From);
            var askerUsername = message.From?.Username;
            var askerId = message.From?.Id ?? 0;

            // Detect if this is a personal question (about self or @someone)
            var personalTarget = personalDetector.DetectPersonalTarget(question, askerName, askerUsername);

            // === PARALLEL EXECUTION: Memory + Search ===
            var (memoryContext, searchResponse) = await ExecuteSearchAsync(
                command, chatId, askerId, askerName, askerUsername, question, personalTarget, ct);

            // Handle confidence gate and build context
            var (context, confidenceWarning, contextTracker, shouldContinue) = await confidenceGate.ProcessSearchResultsAsync(
                command, chatId, message, searchResponse, debugReport, ct);

            if (!shouldContinue)
            {
                // Early return - already sent message to user
                return;
            }

            // Collect debug info for search results WITH context tracking
            debugCollector.CollectSearchDebugInfo(debugReport, searchResponse.Results, contextTracker, personalTarget);

            // Collect debug info for context
            debugCollector.CollectContextDebugInfo(debugReport, context, contextTracker);

            // Generate answer using LLM with command-specific prompt
            var answer = await answerGenerator.GenerateAnswerWithDebugAsync(command, question, context, memoryContext, askerName, debugReport, ct);

            // Add confidence warning if needed (context shown only in debug mode for admins)
            var rawResponse = (confidenceWarning ?? "") + answer;

            // Sanitize HTML for Telegram
            var response = TelegramHtmlSanitizer.Sanitize(rawResponse);

            await bot.SendMessage(
                chatId: chatId,
                text: response,
                parseMode: ParseMode.Html,
                linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true },
                replyParameters: new ReplyParameters { MessageId = message.MessageId },
                cancellationToken: ct);

            logger.LogInformation("[{Command}] Answered question: {Question} (confidence: {Conf})",
                command.ToUpper(), question, searchResponse.Confidence);

            // Store memory and update profile (fire and forget)
            StoreMemoryAsync(chatId, askerId, askerName, askerUsername, question, answer);

            // Send debug report to admin
            await debugService.SendDebugReportAsync(debugReport, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[{Command}] Failed for question: {Question}", command.ToUpper(), question);

            await bot.SendMessage(
                chatId: chatId,
                text: "Произошла ошибка при обработке вопроса. Попробуйте позже.",
                replyParameters: new ReplyParameters { MessageId = message.MessageId },
                cancellationToken: ct);
        }
    }

    private async Task SendHelpTextAsync(long chatId, string command, int messageId, CancellationToken ct)
    {
        var helpText = command == "smart"
            ? """
                🌐 <b>Умный поиск в интернете</b>

                Задай любой вопрос — отвечу с актуальной инфой из сети:
                • <code>/smart сколько стоит биткоин?</code>
                • <code>/smart последние новости про SpaceX</code>
                • <code>/smart как приготовить борщ?</code>

                <i>Использует Perplexity для поиска</i>
                """
            : """
                🎭 <b>Вопрос по истории чата</b>

                Спроси про людей или события в чате:
                • <code>/ask что за тип этот Глеб?</code>
                • <code>/ask я гондон?</code>
                • <code>/ask о чём вчера спорили?</code>

                <i>Ищет в истории сообщений</i>
                """;

        await bot.SendMessage(
            chatId: chatId,
            text: helpText,
            parseMode: ParseMode.Html,
            replyParameters: new ReplyParameters { MessageId = messageId },
            cancellationToken: ct);
    }

    private async Task<(string? memoryContext, SearchResponse searchResponse)> ExecuteSearchAsync(
        string command, long chatId, long askerId, string askerName, string? askerUsername,
        string question, string? personalTarget, CancellationToken ct)
    {
        // Start memory loading task (only for /ask, not /smart)
        Task<string?>? memoryTask = null;
        if (command == "ask" && askerId != 0)
        {
            memoryTask = memoryService.BuildEnhancedContextAsync(chatId, askerId, askerName, question, ct);
        }
        else if (command != "smart" && askerId != 0)
        {
            memoryTask = memoryService.BuildMemoryContextAsync(chatId, askerId, askerName, ct);
        }

        // Start search task (runs in parallel with memory loading)
        Task<SearchResponse> searchTask;

        if (command == "smart")
        {
            // /smart — no RAG search needed
            logger.LogInformation("[SMART] Direct query to Perplexity (no RAG)");
            searchTask = Task.FromResult(new SearchResponse
            {
                Confidence = SearchConfidence.None,
                ConfidenceReason = "Прямой запрос к Perplexity (без RAG)"
            });
        }
        else if (personalTarget == "self")
        {
            logger.LogInformation("[ASK] Personal question detected: self ({Name}/{Username})", askerName, askerUsername);
            searchTask = searchStrategy.SearchPersonalWithHybridAsync(
                chatId, askerUsername ?? askerName, askerName, question, days: 7, ct);
        }
        else if (personalTarget != null && personalTarget.StartsWith("@"))
        {
            var targetUsername = personalTarget.TrimStart('@');
            logger.LogInformation("[ASK] Personal question detected: @{Target}", targetUsername);
            searchTask = searchStrategy.SearchPersonalWithHybridAsync(
                chatId, targetUsername, null, question, days: 7, ct);
        }
        else
        {
            // Context-only search: use sliding window embeddings (10 messages each)
            searchTask = searchStrategy.SearchContextOnlyAsync(chatId, question, ct);
        }

        // Await both tasks in parallel
        string? memoryContext = null;
        SearchResponse searchResponse;

        if (memoryTask != null)
        {
            await Task.WhenAll(memoryTask, searchTask);
            memoryContext = memoryTask.Result;
            searchResponse = searchTask.Result;

            if (memoryContext != null)
            {
                logger.LogDebug("[{Command}] Loaded memory for user {User}", command.ToUpper(), askerName);
            }
        }
        else
        {
            searchResponse = await searchTask;
        }

        return (memoryContext, searchResponse);
    }

    private void StoreMemoryAsync(long chatId, long askerId, string askerName, string? askerUsername, string question, string answer)
    {
        if (askerId == 0)
            return;

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
                logger.LogWarning(ex, "[Memory] Failed to store memory for user {UserId}", askerId);
            }
        });
    }
}
