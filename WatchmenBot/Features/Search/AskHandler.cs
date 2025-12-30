using System.Text;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using WatchmenBot.Services;
using WatchmenBot.Services.Llm;

namespace WatchmenBot.Features.Search;

public class AskHandler
{
    private readonly ITelegramBotClient _bot;
    private readonly EmbeddingService _embeddingService;
    private readonly RagFusionService _ragFusionService;
    private readonly RerankService _rerankService;
    private readonly LlmMemoryService _memoryService;
    private readonly LlmRouter _llmRouter;
    private readonly MessageStore _messageStore;
    private readonly PromptSettingsStore _promptSettings;
    private readonly DebugService _debugService;
    private readonly ILogger<AskHandler> _logger;

    public AskHandler(
        ITelegramBotClient bot,
        EmbeddingService embeddingService,
        RagFusionService ragFusionService,
        RerankService rerankService,
        LlmMemoryService memoryService,
        LlmRouter llmRouter,
        MessageStore messageStore,
        PromptSettingsStore promptSettings,
        DebugService debugService,
        ILogger<AskHandler> logger)
    {
        _bot = bot;
        _embeddingService = embeddingService;
        _ragFusionService = ragFusionService;
        _rerankService = rerankService;
        _memoryService = memoryService;
        _llmRouter = llmRouter;
        _messageStore = messageStore;
        _promptSettings = promptSettings;
        _debugService = debugService;
        _logger = logger;
    }

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
        var question = ParseQuestion(message.Text);

        if (string.IsNullOrWhiteSpace(question))
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

            await _bot.SendMessage(
                chatId: chatId,
                text: helpText,
                parseMode: ParseMode.Html,
                replyParameters: new ReplyParameters { MessageId = message.MessageId },
                cancellationToken: ct);
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
            await _bot.SendChatAction(chatId, ChatAction.Typing, cancellationToken: ct);

            _logger.LogInformation("[{Command}] Question: {Question} in chat {ChatId}", command.ToUpper(), question, chatId);

            // Get asker's name for personal retrieval
            var askerName = GetDisplayName(message.From);
            var askerUsername = message.From?.Username;
            var askerId = message.From?.Id ?? 0;

            // Detect if this is a personal question (about self or @someone)
            var personalTarget = DetectPersonalQuestion(question, askerName, askerUsername);

            // === PARALLEL EXECUTION: Memory + Search ===
            // Start memory loading task (only for /ask, not /smart)
            Task<string?>? memoryTask = null;
            if (command == "ask" && askerId != 0)
            {
                memoryTask = _memoryService.BuildEnhancedContextAsync(chatId, askerId, askerName, question, ct);
            }
            else if (command != "smart" && askerId != 0)
            {
                memoryTask = _memoryService.BuildMemoryContextAsync(chatId, askerId, askerName, ct);
            }

            // Start search task (runs in parallel with memory loading)
            Task<SearchResponse> searchTask;
            Task<RagFusionResponse>? fusionTask = null;

            if (command == "smart")
            {
                // /smart — no RAG search needed
                _logger.LogInformation("[SMART] Direct query to Perplexity (no RAG)");
                searchTask = Task.FromResult(new SearchResponse
                {
                    Confidence = SearchConfidence.None,
                    ConfidenceReason = "Прямой запрос к Perplexity (без RAG)"
                });
            }
            else if (personalTarget == "self")
            {
                _logger.LogInformation("[ASK] Personal question detected: self ({Name}/{Username})", askerName, askerUsername);
                searchTask = _embeddingService.GetPersonalContextAsync(
                    chatId, askerUsername ?? askerName, askerName, question, days: 7, ct);
            }
            else if (personalTarget != null && personalTarget.StartsWith("@"))
            {
                var targetUsername = personalTarget.TrimStart('@');
                _logger.LogInformation("[ASK] Personal question detected: @{Target}", targetUsername);
                searchTask = _embeddingService.GetPersonalContextAsync(
                    chatId, targetUsername, null, question, days: 7, ct);
            }
            else
            {
                // RAG Fusion path - need participant names first, then parallel search
                var participantData = await _messageStore.GetUniqueDisplayNamesAsync(chatId);
                var participantNames = participantData
                    .Where(p => !string.IsNullOrWhiteSpace(p.DisplayName))
                    .Select(p => p.DisplayName)
                    .Take(50)
                    .ToList();

                fusionTask = _ragFusionService.SearchWithFusionAsync(
                    chatId, question, participantNames, variationCount: 3, resultsPerQuery: 15, ct);
                searchTask = fusionTask.ContinueWith(t => t.Result.ToSearchResponse(), ct);
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
                    _logger.LogDebug("[{Command}] Loaded memory for user {User}", command.ToUpper(), askerName);
                }
            }
            else
            {
                searchResponse = await searchTask;
            }

            // Store fusion-specific debug info if available
            if (fusionTask != null)
            {
                var fusionResponse = fusionTask.Result;
                debugReport.QueryVariations = fusionResponse.QueryVariations;
                debugReport.RagFusionTimeMs = fusionResponse.TotalTimeMs;

                _logger.LogInformation("[ASK] RAG Fusion returned {Count} results (rerank disabled)",
                    searchResponse.Results.Count);
            }

            // Handle confidence gate and build context
            var results = searchResponse.Results;
            string? context = null;
            string? confidenceWarning = null;
            var contextTracker = new Dictionary<long, (bool included, string reason)>();

            debugReport.PersonalTarget = personalTarget;
            debugReport.SearchConfidence = searchResponse.Confidence.ToString();
            debugReport.SearchConfidenceReason = searchResponse.ConfidenceReason;
            debugReport.BestScore = searchResponse.BestScore;
            debugReport.ScoreGap = searchResponse.ScoreGap;
            debugReport.HasFullTextMatch = searchResponse.HasFullTextMatch;

            if (command == "smart")
            {
                // /smart — без контекста, прямой запрос к Perplexity
                context = null;
                foreach (var r in results)
                    contextTracker[r.MessageId] = (false, "smart_no_context");
            }
            else // /ask
            {
                // /ask requires context from chat
                if (searchResponse.Confidence == SearchConfidence.None)
                {
                    foreach (var r in results)
                        contextTracker[r.MessageId] = (false, "confidence_none");

                    // Collect debug info before early return
                    debugReport.SearchResults = results.Select(r => new DebugSearchResult
                    {
                        Similarity = r.Similarity,
                        Distance = r.Distance,
                        MessageIds = new[] { r.MessageId },
                        Text = r.ChunkText,
                        Timestamp = ParseTimestamp(r.MetadataJson),
                        IsNewsDump = r.IsNewsDump,
                        IncludedInContext = false,
                        ExcludedReason = "confidence_none"
                    }).ToList();

                    await _bot.SendMessage(
                        chatId: chatId,
                        text: "🤷 В истории чата про это не нашёл. Попробуй уточнить вопрос или период.",
                        replyParameters: new ReplyParameters { MessageId = message.MessageId },
                        cancellationToken: ct);

                    await _debugService.SendDebugReportAsync(debugReport, ct);
                    return;
                }

                if (searchResponse.Confidence == SearchConfidence.Low)
                {
                    confidenceWarning = "⚠️ <i>Контекст слабый, ответ может быть неточным</i>\n\n";
                }

                (context, contextTracker) = await BuildContextWithWindowsAsync(chatId, results, ct);
            }

            // Collect debug info for search results WITH context tracking
            debugReport.SearchResults = results.Select(r => {
                var (included, reason) = contextTracker.TryGetValue(r.MessageId, out var info)
                    ? info
                    : (false, "not_tracked");
                return new DebugSearchResult
                {
                    Similarity = r.Similarity,
                    Distance = r.Distance,
                    MessageIds = new[] { r.MessageId },
                    Text = r.ChunkText,
                    Timestamp = ParseTimestamp(r.MetadataJson),
                    IsNewsDump = r.IsNewsDump,
                    IncludedInContext = included,
                    ExcludedReason = reason
                };
            }).ToList();

            // Collect debug info for context
            if (context != null)
            {
                debugReport.ContextSent = context;
                debugReport.ContextMessagesCount = contextTracker.Count(kv => kv.Value.included);
                debugReport.ContextTokensEstimate = EstimateTokens(context);
            }

            // Generate answer using LLM with command-specific prompt
            var answer = await GenerateAnswerWithDebugAsync(command, question, context, memoryContext, askerName, debugReport, ct);

            // Add confidence warning if needed (context shown only in debug mode for admins)
            var rawResponse = (confidenceWarning ?? "") + answer;

            // Sanitize HTML for Telegram
            var response = TelegramHtmlSanitizer.Sanitize(rawResponse);

            await _bot.SendMessage(
                chatId: chatId,
                text: response,
                parseMode: ParseMode.Html,
                linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true },
                replyParameters: new ReplyParameters { MessageId = message.MessageId },
                cancellationToken: ct);

            _logger.LogInformation("[{Command}] Answered question: {Question} (confidence: {Conf})",
                command.ToUpper(), question, searchResponse.Confidence);

            // Store memory and update profile (fire and forget)
            if (askerId != 0)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _memoryService.StoreMemoryAsync(chatId, askerId, question, answer, CancellationToken.None);
                        await _memoryService.UpdateProfileFromInteractionAsync(
                            chatId, askerId, askerName, askerUsername, question, answer, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[Memory] Failed to store memory for user {UserId}", askerId);
                    }
                });
            }

            // Send debug report to admin
            await _debugService.SendDebugReportAsync(debugReport, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{Command}] Failed for question: {Question}", command.ToUpper(), question);

            await _bot.SendMessage(
                chatId: chatId,
                text: "Произошла ошибка при обработке вопроса. Попробуйте позже.",
                replyParameters: new ReplyParameters { MessageId = message.MessageId },
                cancellationToken: ct);
        }
    }

    private static DateTimeOffset? ParseTimestamp(string? metadataJson)
    {
        if (string.IsNullOrEmpty(metadataJson))
            return null;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(metadataJson);
            if (doc.RootElement.TryGetProperty("DateUtc", out var dateEl))
                return dateEl.GetDateTimeOffset();
        }
        catch { }

        return null;
    }

    private static int EstimateTokens(string text)
    {
        // Rough estimate: ~4 chars per token for mixed content
        return text.Length / 4;
    }

    private static string ParseQuestion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var spaceIndex = text.IndexOf(' ');
        if (spaceIndex < 0)
            return string.Empty;

        return text[(spaceIndex + 1)..].Trim();
    }

    // Token budget for context (roughly 4 chars per token)
    private const int ContextTokenBudget = 4000;
    private const int CharsPerToken = 4;
    private const int ContextCharBudget = ContextTokenBudget * CharsPerToken; // ~16000 chars

    // Context window settings
    private const int ContextWindowSize = 1; // ±1 messages around each found message (reduced from ±2 to minimize noise)

    /// <summary>
    /// Build context with context windows around found messages
    /// </summary>
    private async Task<(string context, Dictionary<long, (bool included, string reason)> tracker)> BuildContextWithWindowsAsync(
        long chatId, List<SearchResult> results, CancellationToken ct)
    {
        _logger.LogDebug("[BuildContext] Processing {Count} search results with windows (±{Window})",
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
            else if (!sortedResults.Any(sr => sr.MessageId == r.MessageId))
                tracker[r.MessageId] = (false, "not_in_top10");
        }

        // Get context windows for top messages
        var messageIds = sortedResults.Select(r => r.MessageId).ToList();
        var windows = await _embeddingService.GetMergedContextWindowsAsync(chatId, messageIds, ContextWindowSize, ct);

        // Build context string with budget control
        var sb = new StringBuilder();
        sb.AppendLine("Контекст из чата (сообщения сгруппированы по диалогам):");
        sb.AppendLine();

        var usedChars = sb.Length;
        var includedWindows = 0;
        var seenMessageIds = new HashSet<long>();

        foreach (var window in windows)
        {
            // Estimate window size
            var windowText = window.ToFormattedText();
            var windowChars = windowText.Length + 50; // +50 for separators

            if (usedChars + windowChars > ContextCharBudget)
            {
                _logger.LogDebug("[BuildContext] Budget exceeded, stopping at {Windows} windows", includedWindows);
                break;
            }

            // Mark center message as included
            tracker[window.CenterMessageId] = (true, "ok");

            // Add window header
            sb.AppendLine($"--- Диалог #{includedWindows + 1} ---");
            sb.Append(windowText);
            sb.AppendLine();

            usedChars += windowChars;
            includedWindows++;

            // Track all messages in window
            foreach (var msg in window.Messages)
                seenMessageIds.Add(msg.MessageId);
        }

        // Mark remaining messages
        foreach (var r in sortedResults)
        {
            if (!tracker.ContainsKey(r.MessageId))
                tracker[r.MessageId] = (false, "budget_exceeded");
        }

        _logger.LogInformation(
            "[BuildContext] Built context: {Windows} windows, {Messages} total messages, {Chars}/{Budget} chars",
            includedWindows, seenMessageIds.Count, usedChars, ContextCharBudget);

        return (sb.ToString(), tracker);
    }

    private async Task<string> GenerateAnswerWithDebugAsync(
        string command, string question, string? context, string? memoryContext, string askerName, DebugReport debugReport, CancellationToken ct)
    {
        var settings = await _promptSettings.GetSettingsAsync(command);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // For /ask with context - use two-stage generation
        if (command == "ask" && !string.IsNullOrWhiteSpace(context))
        {
            return await GenerateTwoStageAnswerWithDebugAsync(question, context, memoryContext, askerName, settings, debugReport, ct);
        }

        // Build memory section if available
        var memorySection = !string.IsNullOrWhiteSpace(memoryContext)
            ? $"\n{memoryContext}\n"
            : "";

        // For /q or /ask without context - single stage
        var userPrompt = string.IsNullOrWhiteSpace(context)
            ? $"""
                Сегодняшняя дата: {DateTime.UtcNow:dd.MM.yyyy}
                {memorySection}
                Спрашивает: {askerName}
                Вопрос: {question}
                """
            : $"""
                Сегодняшняя дата: {DateTime.UtcNow:dd.MM.yyyy}
                {memorySection}
                Контекст из чата:
                {context}

                Спрашивает: {askerName}
                Вопрос: {question}
                """;

        var response = await _llmRouter.CompleteWithFallbackAsync(
            new LlmRequest
            {
                SystemPrompt = settings.SystemPrompt,
                UserPrompt = userPrompt,
                Temperature = 0.5
            },
            preferredTag: settings.LlmTag,
            ct: ct);

        sw.Stop();

        // Collect debug info
        debugReport.SystemPrompt = settings.SystemPrompt;
        debugReport.UserPrompt = userPrompt;
        debugReport.LlmProvider = response.Provider;
        debugReport.LlmModel = response.Model;
        debugReport.LlmTag = settings.LlmTag;
        debugReport.Temperature = 0.5;
        debugReport.LlmResponse = response.Content;
        debugReport.PromptTokens = response.PromptTokens;
        debugReport.CompletionTokens = response.CompletionTokens;
        debugReport.TotalTokens = response.TotalTokens;
        debugReport.LlmTimeMs = sw.ElapsedMilliseconds;

        _logger.LogInformation("[{Command}] LLM: provider={Provider}, model={Model}, tag={Tag}",
            command.ToUpper(), response.Provider, response.Model, settings.LlmTag ?? "default");

        return response.Content;
    }

    /// <summary>
    /// Two-stage generation for /ask: extract facts first, then add humor
    /// </summary>
    private async Task<string> GenerateTwoStageAnswerWithDebugAsync(
        string question, string context, string? memoryContext, string askerName, PromptSettings settings, DebugReport debugReport, CancellationToken ct)
    {
        debugReport.IsMultiStage = true;
        debugReport.StageCount = 2;
        var totalSw = System.Diagnostics.Stopwatch.StartNew();

        // STAGE 1: Extract STRUCTURED facts with low temperature (prevents hallucinations)
        var factsSystemPrompt = """
            Ты — аналитик чата. Извлекай факты СТРОГО из контекста.

            КРИТИЧЕСКИ ВАЖНО:
            1. Отвечай ТОЛЬКО JSON, без markdown, без пояснений
            2. Извлекай ТОЛЬКО факты, НАПРЯМУЮ связанные с вопросом
            3. ИГНОРИРУЙ сообщения, которые не относятся к теме вопроса
            4. Если в контексте несколько диалогов — используй только релевантные
            5. Если факт не подтверждён контекстом — НЕ добавляй его
            6. Имена пиши ТОЧНО как в контексте (Gleb Bezrukov, НЕ "Глеб Безухов"!)
            7. НЕ транслитерируй и НЕ "исправляй" имена — копируй дословно

            Формат ответа:
            {
              "facts": [
                {"who": "Имя ТОЧНО как в контексте", "said": "прямая цитата или пересказ", "relevance": "как связано с вопросом"}
              ],
              "answer": "краткий ответ на вопрос ТОЛЬКО из релевантных фактов",
              "roast_target": "кого подколоть (имя ТОЧНО как в контексте) или null",
              "best_quote": "самая смешная/глупая цитата ПО ТЕМЕ или null",
              "irrelevant_ignored": true/false
            }
            """;

        var factsPrompt = $"""
            ВОПРОС от {askerName}: {question}

            Контекст из чата (может содержать нерелевантные сообщения — игнорируй их):
            {context}

            Извлеки ТОЛЬКО факты, которые НАПРЯМУЮ отвечают на вопрос "{question}".
            Нерелевантные диалоги — пропускай.
            """;

        var stage1Sw = System.Diagnostics.Stopwatch.StartNew();
        var factsResponse = await _llmRouter.CompleteWithFallbackAsync(
            new LlmRequest
            {
                SystemPrompt = factsSystemPrompt,
                UserPrompt = factsPrompt,
                Temperature = 0.1 // Very low for accuracy
            },
            preferredTag: settings.LlmTag,
            ct: ct);
        stage1Sw.Stop();

        debugReport.Stages.Add(new DebugStage
        {
            StageNumber = 1,
            Name = "Facts (JSON)",
            Temperature = 0.1,
            SystemPrompt = factsSystemPrompt,
            UserPrompt = factsPrompt,
            Response = factsResponse.Content,
            Tokens = factsResponse.TotalTokens,
            TimeMs = stage1Sw.ElapsedMilliseconds
        });

        _logger.LogInformation("[ASK] Stage 1 (structured facts): {Length} chars", factsResponse.Content.Length);

        // Check if facts extraction returned empty - fallback to direct generation
        var factsEmpty = IsFactsEmpty(factsResponse.Content);
        if (factsEmpty)
        {
            _logger.LogWarning("[ASK] Facts extraction returned empty, falling back to direct generation");

            // Build memory section for fallback
            var fallbackMemory = !string.IsNullOrWhiteSpace(memoryContext)
                ? $"\n{memoryContext}\n"
                : "";

            // Fallback: direct single-stage generation with context
            var directPrompt = $"""
                Сегодняшняя дата: {DateTime.UtcNow:dd.MM.yyyy}
                {fallbackMemory}
                Контекст из чата:
                {context}

                Спрашивает: {askerName}
                Вопрос: {question}
                """;

            var directResponse = await _llmRouter.CompleteWithFallbackAsync(
                new LlmRequest
                {
                    SystemPrompt = settings.SystemPrompt,
                    UserPrompt = directPrompt,
                    Temperature = 0.6
                },
                preferredTag: settings.LlmTag,
                ct: ct);

            totalSw.Stop();

            // Update debug info for fallback
            debugReport.SystemPrompt = settings.SystemPrompt;
            debugReport.UserPrompt = directPrompt;
            debugReport.LlmProvider = directResponse.Provider;
            debugReport.LlmModel = directResponse.Model;
            debugReport.LlmTag = settings.LlmTag;
            debugReport.Temperature = 0.6;
            debugReport.LlmResponse = directResponse.Content;
            debugReport.PromptTokens = factsResponse.PromptTokens + directResponse.PromptTokens;
            debugReport.CompletionTokens = factsResponse.CompletionTokens + directResponse.CompletionTokens;
            debugReport.TotalTokens = factsResponse.TotalTokens + directResponse.TotalTokens;
            debugReport.LlmTimeMs = totalSw.ElapsedMilliseconds;

            debugReport.Stages.Add(new DebugStage
            {
                StageNumber = 2,
                Name = "Direct (fallback)",
                Temperature = 0.6,
                SystemPrompt = settings.SystemPrompt,
                UserPrompt = directPrompt,
                Response = directResponse.Content,
                Tokens = directResponse.TotalTokens,
                TimeMs = totalSw.ElapsedMilliseconds - stage1Sw.ElapsedMilliseconds
            });

            return directResponse.Content;
        }

        // Build memory section for personalization
        var memorySection = !string.IsNullOrWhiteSpace(memoryContext)
            ? $"\n{memoryContext}\n"
            : "";

        // STAGE 2: Add humor based on structured facts + memory
        var humorPrompt = $"""
            Спрашивает: {askerName}
            Вопрос: {question}

            === ИСТОЧНИК 1: ПАМЯТЬ О ПОЛЬЗОВАТЕЛЕ ===
            {(string.IsNullOrWhiteSpace(memoryContext) ? "(пусто)" : memoryContext)}

            === ИСТОЧНИК 2: КОНТЕКСТ ИЗ ЧАТА ===
            {factsResponse.Content}

            ПРАВИЛА ОТВЕТА:
            1. У тебя ДВА источника данных. Используй ОБА, выбирая что РЕЛЕВАНТНЕЕ вопросу
            2. Если в памяти есть прямой ответ на вопрос — используй его
            3. Если в контексте чата есть дополнительные детали — добавь их
            4. НЕ придумывай новых фактов — только из данных выше
            5. Если есть "roast_target" — подколи этого человека
            6. Если есть "best_quote" — вставь цитату дословно в <i>кавычках</i>
            7. Ответ должен быть дерзким и с матом
            8. Если оба источника пустые — честно скажи что ничего не нашёл
            9. ЗАПРЕЩЕНО раскрывать технические детали! НЕ пиши: "по памяти", "в контексте", "из данных", "источник", "JSON", "факты говорят". Просто ЗНАЙ и отвечай как будто сам помнишь
            10. Имена пиши ТОЧНО как в данных (НЕ транслитерируй, НЕ "исправляй"!)

            Формат: 2-4 предложения, HTML для <b> и <i>.
            """;

        var stage2Sw = System.Diagnostics.Stopwatch.StartNew();
        var finalResponse = await _llmRouter.CompleteWithFallbackAsync(
            new LlmRequest
            {
                SystemPrompt = settings.SystemPrompt,
                UserPrompt = humorPrompt,
                Temperature = 0.6 // Higher for creativity
            },
            preferredTag: settings.LlmTag,
            ct: ct);
        stage2Sw.Stop();

        debugReport.Stages.Add(new DebugStage
        {
            StageNumber = 2,
            Name = "Humor",
            Temperature = 0.6,
            SystemPrompt = settings.SystemPrompt,
            UserPrompt = humorPrompt,
            Response = finalResponse.Content,
            Tokens = finalResponse.TotalTokens,
            TimeMs = stage2Sw.ElapsedMilliseconds
        });

        totalSw.Stop();

        // Set final debug info
        debugReport.SystemPrompt = settings.SystemPrompt;
        debugReport.UserPrompt = humorPrompt;
        debugReport.LlmProvider = finalResponse.Provider;
        debugReport.LlmModel = finalResponse.Model;
        debugReport.LlmTag = settings.LlmTag;
        debugReport.Temperature = 0.6;
        debugReport.LlmResponse = finalResponse.Content;
        debugReport.PromptTokens = factsResponse.PromptTokens + finalResponse.PromptTokens;
        debugReport.CompletionTokens = factsResponse.CompletionTokens + finalResponse.CompletionTokens;
        debugReport.TotalTokens = factsResponse.TotalTokens + finalResponse.TotalTokens;
        debugReport.LlmTimeMs = totalSw.ElapsedMilliseconds;

        _logger.LogInformation("[ASK] Stage 2 (humor): provider={Provider}, model={Model}",
            finalResponse.Provider, finalResponse.Model);

        return finalResponse.Content;
    }

    private static string EscapeHtml(string text)
    {
        return text
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");
    }

    /// <summary>
    /// Check if facts extraction returned empty or unusable result
    /// </summary>
    private static bool IsFactsEmpty(string factsJson)
    {
        if (string.IsNullOrWhiteSpace(factsJson))
            return true;

        try
        {
            // Remove markdown code blocks if present
            var json = factsJson.Trim();
            if (json.StartsWith("```"))
            {
                var lines = json.Split('\n');
                json = string.Join("\n", lines.Skip(1).TakeWhile(l => !l.StartsWith("```")));
            }

            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Check if facts array exists and is not empty
            if (root.TryGetProperty("facts", out var factsArray))
            {
                if (factsArray.ValueKind == System.Text.Json.JsonValueKind.Array && factsArray.GetArrayLength() > 0)
                    return false; // Has facts
            }

            // Check if answer exists and is meaningful
            if (root.TryGetProperty("answer", out var answer))
            {
                var answerText = answer.GetString() ?? "";
                if (!string.IsNullOrWhiteSpace(answerText) &&
                    !answerText.Contains("не найден") &&
                    !answerText.Contains("нет данных") &&
                    !answerText.Contains("не упоминается"))
                    return false; // Has meaningful answer
            }

            return true; // Empty or unusable
        }
        catch
        {
            return true; // Parse error = treat as empty
        }
    }

    private static string GetDisplayName(User? user)
    {
        if (user == null)
            return "Аноним";

        if (!string.IsNullOrWhiteSpace(user.FirstName))
        {
            return string.IsNullOrWhiteSpace(user.LastName)
                ? user.FirstName
                : $"{user.FirstName} {user.LastName}";
        }

        return !string.IsNullOrWhiteSpace(user.Username)
            ? user.Username
            : user.Id.ToString();
    }

    /// <summary>
    /// Detect if question is about the asker or a specific @user
    /// Returns: null = general question, "self" = about asker, "@username" = about specific user
    /// </summary>
    private static string? DetectPersonalQuestion(string question, string askerName, string? askerUsername)
    {
        var q = question.ToLowerInvariant().Trim();

        // Self-referential questions: "я ..?", "кто я?", "какой я?"
        var selfPatterns = new[]
        {
            "я ", "кто я", "какой я", "какая я", "что я", "как я",
            "обо мне", "про меня", "меня ", "мне ", "мной "
        };

        if (selfPatterns.Any(p => q.Contains(p)))
        {
            return "self";
        }

        // Extract @username from question
        var usernameMatch = System.Text.RegularExpressions.Regex.Match(question, @"@(\w+)");
        if (usernameMatch.Success)
        {
            return usernameMatch.Value; // returns "@username"
        }

        return null;
    }

}
