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
    private readonly ContextEmbeddingService _contextEmbeddingService;
    private readonly LlmMemoryService _memoryService;
    private readonly LlmRouter _llmRouter;
    private readonly MessageStore _messageStore;
    private readonly PromptSettingsStore _promptSettings;
    private readonly DebugService _debugService;
    private readonly ILogger<AskHandler> _logger;

    public AskHandler(
        ITelegramBotClient bot,
        EmbeddingService embeddingService,
        ContextEmbeddingService contextEmbeddingService,
        LlmMemoryService memoryService,
        LlmRouter llmRouter,
        MessageStore messageStore,
        PromptSettingsStore promptSettings,
        DebugService debugService,
        ILogger<AskHandler> logger)
    {
        _bot = bot;
        _embeddingService = embeddingService;
        _contextEmbeddingService = contextEmbeddingService;
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
                searchTask = SearchPersonalWithHybridAsync(
                    chatId, askerUsername ?? askerName, askerName, question, days: 7, ct);
            }
            else if (personalTarget != null && personalTarget.StartsWith("@"))
            {
                var targetUsername = personalTarget.TrimStart('@');
                _logger.LogInformation("[ASK] Personal question detected: @{Target}", targetUsername);
                searchTask = SearchPersonalWithHybridAsync(
                    chatId, targetUsername, null, question, days: 7, ct);
            }
            else
            {
                // Context-only search: use sliding window embeddings (10 messages each)
                // No RAG Fusion - context windows already contain full conversation context
                searchTask = SearchContextOnlyAsync(chatId, question, ct);
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

        // For /ask with context - use one-stage generation (faster than two-stage)
        if (command == "ask" && !string.IsNullOrWhiteSpace(context))
        {
            return await GenerateOneStageAnswerWithDebugAsync(question, context, memoryContext, askerName, settings, debugReport, ct);
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
    /// One-stage generation for /ask: analyze context and generate response in single LLM call.
    /// Faster than two-stage (saves ~1-2 sec) while maintaining quality.
    /// </summary>
    private async Task<string> GenerateOneStageAnswerWithDebugAsync(
        string question, string context, string? memoryContext, string askerName, PromptSettings settings, DebugReport debugReport, CancellationToken ct)
    {
        debugReport.IsMultiStage = false;
        debugReport.StageCount = 1;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Build memory section if available
        var memorySection = !string.IsNullOrWhiteSpace(memoryContext)
            ? $"""

              === ПАМЯТЬ О ПОЛЬЗОВАТЕЛЕ ===
              {memoryContext}

              """
            : "";

        // One-stage prompt: analyze context internally, then respond with humor
        var userPrompt = $"""
            Сегодняшняя дата: {DateTime.UtcNow:dd.MM.yyyy}
            Спрашивает: {askerName}
            Вопрос: {question}
            {memorySection}
            === КОНТЕКСТ ИЗ ЧАТА ===
            {context}

            ИНСТРУКЦИИ:
            1. Проанализируй контекст и найди ТОЛЬКО релевантные сообщения
            2. ИГНОРИРУЙ нерелевантные диалоги — они добавлены для контекста
            3. Если в памяти есть прямой ответ — используй его
            4. Имена пиши ТОЧНО как в контексте (НЕ транслитерируй!)
            5. Если нашёл смешную/глупую цитату по теме — вставь в <i>кавычках</i>
            6. Ответ должен быть дерзким, с подъёбкой, можно с матом
            7. НЕ придумывай факты — только из контекста выше
            8. ЗАПРЕЩЕНО упоминать технические детали: "контекст", "память", "данные"
            9. Отвечай как будто сам всё помнишь

            Формат: 2-4 предложения, HTML для <b> и <i>.
            """;

        var response = await _llmRouter.CompleteWithFallbackAsync(
            new LlmRequest
            {
                SystemPrompt = settings.SystemPrompt,
                UserPrompt = userPrompt,
                Temperature = 0.5 // Balanced: accurate facts + some creativity
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

        debugReport.Stages.Add(new DebugStage
        {
            StageNumber = 1,
            Name = "OneStage",
            Temperature = 0.5,
            SystemPrompt = settings.SystemPrompt,
            UserPrompt = userPrompt,
            Response = response.Content,
            Tokens = response.TotalTokens,
            TimeMs = sw.ElapsedMilliseconds
        });

        _logger.LogInformation("[ASK] OneStage: provider={Provider}, model={Model}, {Tokens} tokens in {Ms}ms",
            response.Provider, response.Model, response.TotalTokens, sw.ElapsedMilliseconds);

        return response.Content;
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

    /// <summary>
    /// Hybrid search for personal questions:
    /// 1. Try finding user's messages via message_embeddings (precise targeting)
    /// 2. Expand with context windows via context_embeddings (full dialog context)
    /// 3. If no personal messages found, fallback to context-only search (user might have participated in dialogs)
    /// </summary>
    private async Task<SearchResponse> SearchPersonalWithHybridAsync(
        long chatId,
        string usernameOrName,
        string? displayName,
        string query,
        int days,
        CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Step 1: Try finding user's relevant messages using message_embeddings
        var personalTask = _embeddingService.GetPersonalContextAsync(
            chatId, usernameOrName, displayName, query, days, ct);

        // Step 2: Parallel search in context embeddings (user might be in dialogs)
        var contextTask = _contextEmbeddingService.SearchContextAsync(chatId, query, limit: 10, ct);

        await Task.WhenAll(personalTask, contextTask);

        var personalResponse = await personalTask;
        var contextWindows = await contextTask;

        sw.Stop();

        // Step 3: Merge results based on what we found
        var allResults = new List<SearchResult>();
        var personalCount = 0;
        var contextCount = 0;

        // Add personal message results if found
        if (personalResponse.Results.Count > 0)
        {
            allResults.AddRange(personalResponse.Results);
            personalCount = personalResponse.Results.Count;

            // Expand with context windows containing these messages
            var topMessageIds = personalResponse.Results
                .Take(5)
                .Select(r => r.MessageId)
                .ToList();

            var expandedWindows = await _contextEmbeddingService.GetContextWindowsByMessageIdsAsync(
                chatId, topMessageIds, limit: 5, ct);

            var expandedResults = expandedWindows.Select(cw => new SearchResult
            {
                ChatId = cw.ChatId,
                MessageId = cw.CenterMessageId,
                ChunkIndex = 0,
                ChunkText = cw.ContextText,
                MetadataJson = null,
                Similarity = 0.75, // Lower than direct hits but higher than generic context
                Distance = 0.25,
                IsNewsDump = false
            });

            allResults.AddRange(expandedResults);
            contextCount += expandedWindows.Count;
        }

        // Add context-only results (might include user's participation in dialogs)
        var contextResults = contextWindows.Select(cw => new SearchResult
        {
            ChatId = cw.ChatId,
            MessageId = cw.CenterMessageId,
            ChunkIndex = 0,
            ChunkText = cw.ContextText,
            MetadataJson = null,
            Similarity = cw.Similarity * 0.9, // Slightly lower priority than personal
            Distance = cw.Distance,
            IsNewsDump = false
        });

        allResults.AddRange(contextResults);
        contextCount += contextWindows.Count;

        // Deduplicate by message_id, keeping best similarity
        var mergedResults = allResults
            .GroupBy(r => r.MessageId)
            .Select(g => g.OrderByDescending(r => r.Similarity).First())
            .OrderByDescending(r => r.Similarity)
            .ToList();

        _logger.LogInformation(
            "[HybridPersonal] User: {User} | Found {Total} results in {Ms}ms ({Personal} personal + {Context} context)",
            usernameOrName, mergedResults.Count, sw.ElapsedMilliseconds, personalCount, contextCount);

        // Determine confidence based on combined results
        if (mergedResults.Count == 0)
        {
            return new SearchResponse
            {
                Confidence = SearchConfidence.None,
                ConfidenceReason = $"Пользователь {usernameOrName} не найден в истории"
            };
        }

        var bestSim = mergedResults[0].Similarity;
        var confidence = personalCount > 0 ? personalResponse.Confidence : (bestSim switch
        {
            > 0.5 => SearchConfidence.High,
            > 0.35 => SearchConfidence.Medium,
            > 0.25 => SearchConfidence.Low,
            _ => SearchConfidence.None
        });

        return new SearchResponse
        {
            Results = mergedResults,
            Confidence = confidence,
            ConfidenceReason = personalCount > 0
                ? $"[Hybrid: {personalCount} personal + {contextCount} context] {personalResponse.ConfidenceReason}"
                : $"[Context-only: {contextCount} windows] Найдено в диалогах (sim={bestSim:F3})",
            BestScore = bestSim,
            ScoreGap = personalResponse.ScoreGap,
            HasFullTextMatch = personalResponse.HasFullTextMatch
        };
    }

    /// <summary>
    /// Hybrid search for general questions:
    /// 1. Search in context_embeddings for full dialog context (primary)
    /// 2. Search in message_embeddings for precise message matches (secondary)
    /// 3. Merge and deduplicate results
    /// </summary>
    private async Task<SearchResponse> SearchContextOnlyAsync(
        long chatId, string query, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Parallel search in both embedding types
        var contextTask = _contextEmbeddingService.SearchContextAsync(chatId, query, limit: 10, ct);
        var messageTask = _embeddingService.SearchSimilarAsync(chatId, query, limit: 10, ct);

        await Task.WhenAll(contextTask, messageTask);

        var contextResults = await contextTask;
        var messageResults = await messageTask;

        sw.Stop();
        _logger.LogInformation(
            "[HybridSearch] Found {ContextCount} context windows + {MessageCount} messages in {Ms}ms",
            contextResults.Count, messageResults.Count, sw.ElapsedMilliseconds);

        // Convert context results (priority: 1.0x similarity)
        var contextSearchResults = contextResults.Select(cr => new SearchResult
        {
            ChatId = cr.ChatId,
            MessageId = cr.CenterMessageId,
            ChunkIndex = 0,
            ChunkText = cr.ContextText, // Full window with context
            MetadataJson = null,
            Similarity = cr.Similarity, // Keep original similarity (priority)
            Distance = cr.Distance,
            IsNewsDump = false
        }).ToList();

        // Convert message results (priority: 0.85x similarity - slightly lower than context)
        var messageSearchResults = messageResults.Select(mr => new SearchResult
        {
            ChatId = mr.ChatId,
            MessageId = mr.MessageId,
            ChunkIndex = mr.ChunkIndex,
            ChunkText = mr.ChunkText,
            MetadataJson = mr.MetadataJson,
            Similarity = mr.Similarity * 0.85, // Lower priority than full context
            Distance = mr.Distance,
            IsNewsDump = mr.IsNewsDump
        }).ToList();

        // Merge and deduplicate by message_id (keep best similarity)
        var allResults = contextSearchResults
            .Concat(messageSearchResults)
            .GroupBy(r => r.MessageId)
            .Select(g => g.OrderByDescending(r => r.Similarity).First())
            .OrderByDescending(r => r.Similarity)
            .ToList();

        _logger.LogInformation(
            "[HybridSearch] Merged {Total} results ({Context} context + {Message} messages)",
            allResults.Count, contextResults.Count, messageResults.Count);

        if (allResults.Count == 0)
        {
            return new SearchResponse
            {
                Confidence = SearchConfidence.None,
                ConfidenceReason = "No embeddings found"
            };
        }

        // Determine confidence based on best similarity
        var bestSim = allResults[0].Similarity;
        var confidence = bestSim switch
        {
            > 0.5 => SearchConfidence.High,
            > 0.35 => SearchConfidence.Medium,
            > 0.25 => SearchConfidence.Low,
            _ => SearchConfidence.None
        };

        var confidenceReason = confidence switch
        {
            SearchConfidence.High => $"[Hybrid: {contextResults.Count}+{messageResults.Count}] Strong match (sim={bestSim:F3})",
            SearchConfidence.Medium => $"[Hybrid: {contextResults.Count}+{messageResults.Count}] Moderate match (sim={bestSim:F3})",
            SearchConfidence.Low => $"[Hybrid: {contextResults.Count}+{messageResults.Count}] Weak match (sim={bestSim:F3})",
            _ => $"[Hybrid: {contextResults.Count}+{messageResults.Count}] Very weak match (sim={bestSim:F3})"
        };

        return new SearchResponse
        {
            Results = allResults,
            Confidence = confidence,
            ConfidenceReason = confidenceReason,
            BestScore = bestSim
        };
    }
}
