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
    private readonly LlmRouter _llmRouter;
    private readonly PromptSettingsStore _promptSettings;
    private readonly DebugService _debugService;
    private readonly ILogger<AskHandler> _logger;

    public AskHandler(
        ITelegramBotClient bot,
        EmbeddingService embeddingService,
        LlmRouter llmRouter,
        PromptSettingsStore promptSettings,
        DebugService debugService,
        ILogger<AskHandler> logger)
    {
        _bot = bot;
        _embeddingService = embeddingService;
        _llmRouter = llmRouter;
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

            // Detect if this is a personal question (about self or @someone)
            var personalTarget = DetectPersonalQuestion(question, askerName, askerUsername);

            // Rewrite query for better search (only for /ask, not /smart)
            string searchQuery = question;
            if (command == "ask")
            {
                var (rewritten, rewriteMs) = await RewriteQueryForSearchAsync(question, ct);
                searchQuery = rewritten;
                debugReport.RewrittenQuery = rewritten;
                debugReport.QueryRewriteTimeMs = rewriteMs;
            }

            // Choose search strategy based on command type
            SearchResponse searchResponse;

            if (command == "smart")
            {
                // /smart — чистый запрос к Perplexity, без поиска по чату
                _logger.LogInformation("[SMART] Direct query to Perplexity (no RAG)");
                searchResponse = new SearchResponse
                {
                    Confidence = SearchConfidence.None,
                    ConfidenceReason = "Прямой запрос к Perplexity (без RAG)"
                };
            }
            else if (personalTarget == "self")
            {
                // Personal question about self — use personal retrieval with vector search by question
                _logger.LogInformation("[ASK] Personal question detected: self ({Name}/{Username})", askerName, askerUsername);
                searchResponse = await _embeddingService.GetPersonalContextAsync(
                    chatId,
                    askerUsername ?? askerName,
                    askerName,
                    searchQuery,  // Use rewritten query for better matches!
                    days: 7,
                    ct);
            }
            else if (personalTarget != null && personalTarget.StartsWith("@"))
            {
                // Question about @someone — use personal retrieval with vector search
                var targetUsername = personalTarget.TrimStart('@');
                _logger.LogInformation("[ASK] Personal question detected: @{Target}", targetUsername);
                searchResponse = await _embeddingService.GetPersonalContextAsync(
                    chatId,
                    targetUsername,
                    null, // don't know display name
                    searchQuery,  // Use rewritten query for better matches!
                    days: 7,
                    ct);
            }
            else
            {
                // Regular semantic search for /ask with rewritten query
                searchResponse = await _embeddingService.SearchWithConfidenceAsync(chatId, searchQuery, limit: 20, ct);
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

                (context, contextTracker) = BuildContextWithTracking(results);
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
            var answer = await GenerateAnswerWithDebugAsync(command, question, context, askerName, debugReport, ct);

            // Format response with confidence warning if needed
            var rawResponse = (confidenceWarning ?? "") + FormatResponse(question, answer, results.Take(3).ToList());

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

    private (string context, Dictionary<long, (bool included, string reason)> tracker) BuildContextWithTracking(List<SearchResult> results)
    {
        _logger.LogDebug("[BuildContext] Processing {Count} search results with budget {Budget} tokens",
            results.Count, ContextTokenBudget);

        var tracker = new Dictionary<long, (bool included, string reason)>();
        var seenTexts = new HashSet<string>();
        var usedChars = 0;

        // Sort by similarity DESC - prioritize most relevant messages
        var sortedResults = results.OrderByDescending(r => r.Similarity).ToList();

        // First pass: filter and deduplicate, respecting budget
        var validMessages = new List<(long MessageId, string Text, DateTimeOffset Time, double Similarity)>();

        foreach (var r in sortedResults)
        {
            // Check for empty text
            if (string.IsNullOrWhiteSpace(r.ChunkText))
            {
                tracker[r.MessageId] = (false, "empty_text");
                _logger.LogDebug("[BuildContext] msg={Id} EXCLUDED: empty_text", r.MessageId);
                continue;
            }

            // Check for duplicate text
            var textKey = r.ChunkText.Trim().ToLowerInvariant();
            if (seenTexts.Contains(textKey))
            {
                tracker[r.MessageId] = (false, "duplicate_text");
                _logger.LogDebug("[BuildContext] msg={Id} EXCLUDED: duplicate_text", r.MessageId);
                continue;
            }

            // Check token budget
            var msgChars = r.ChunkText.Length + 30; // +30 for timestamp formatting
            if (usedChars + msgChars > ContextCharBudget)
            {
                tracker[r.MessageId] = (false, "budget_exceeded");
                _logger.LogDebug("[BuildContext] msg={Id} EXCLUDED: budget_exceeded (sim={Sim:F3})",
                    r.MessageId, r.Similarity);
                continue;
            }

            seenTexts.Add(textKey);
            usedChars += msgChars;

            // Parse timestamp
            DateTimeOffset time = DateTimeOffset.MinValue;
            if (!string.IsNullOrEmpty(r.MetadataJson))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(r.MetadataJson);
                    if (doc.RootElement.TryGetProperty("DateUtc", out var dateEl))
                    {
                        time = dateEl.GetDateTimeOffset();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[BuildContext] Failed to parse metadata: {Json}", r.MetadataJson);
                }
            }

            tracker[r.MessageId] = (true, "ok");
            validMessages.Add((r.MessageId, r.ChunkText, time, r.Similarity));
            _logger.LogDebug("[BuildContext] msg={Id} INCLUDED sim={Sim:F3} chars={Chars}",
                r.MessageId, r.Similarity, msgChars);
        }

        // Sort included messages chronologically for better context flow
        validMessages = validMessages.OrderBy(m => m.Time).ToList();

        _logger.LogInformation("[BuildContext] Built context: {Count}/{Total} messages, {Chars}/{Budget} chars, sim range: {MinSim:F3}-{MaxSim:F3}",
            validMessages.Count, results.Count,
            usedChars, ContextCharBudget,
            validMessages.Count > 0 ? validMessages.Min(m => m.Similarity) : 0,
            validMessages.Count > 0 ? validMessages.Max(m => m.Similarity) : 0);

        var sb = new StringBuilder();
        sb.AppendLine("Релевантные сообщения из чата (хронологически):");
        sb.AppendLine();

        foreach (var msg in validMessages)
        {
            var timeStr = msg.Time != DateTimeOffset.MinValue
                ? $"[{msg.Time.ToLocalTime():dd.MM HH:mm}] "
                : "";
            sb.AppendLine($"{timeStr}{msg.Text}");
        }

        return (sb.ToString(), tracker);
    }

    private async Task<string> GenerateAnswerWithDebugAsync(
        string command, string question, string? context, string askerName, DebugReport debugReport, CancellationToken ct)
    {
        var settings = await _promptSettings.GetSettingsAsync(command);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // For /ask with context - use two-stage generation
        if (command == "ask" && !string.IsNullOrWhiteSpace(context))
        {
            return await GenerateTwoStageAnswerWithDebugAsync(question, context, askerName, settings, debugReport, ct);
        }

        // For /q or /ask without context - single stage
        var userPrompt = string.IsNullOrWhiteSpace(context)
            ? $"""
                Сегодняшняя дата: {DateTime.UtcNow:dd.MM.yyyy}

                Спрашивает: {askerName}
                Вопрос: {question}
                """
            : $"""
                Сегодняшняя дата: {DateTime.UtcNow:dd.MM.yyyy}

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
        string question, string context, string askerName, PromptSettings settings, DebugReport debugReport, CancellationToken ct)
    {
        debugReport.IsMultiStage = true;
        debugReport.StageCount = 2;
        var totalSw = System.Diagnostics.Stopwatch.StartNew();

        // STAGE 1: Extract STRUCTURED facts with low temperature (prevents hallucinations)
        var factsSystemPrompt = """
            Ты — аналитик чата. Извлекай факты СТРОГО из контекста.

            ВАЖНО: Отвечай ТОЛЬКО JSON, без markdown, без пояснений.
            Если факт не подтверждён контекстом — НЕ добавляй его.

            Формат ответа:
            {
              "facts": [
                {"who": "Имя", "said": "прямая цитата или пересказ", "context": "что обсуждали"}
              ],
              "answer": "краткий ответ на вопрос из фактов",
              "roast_target": "кого подколоть (имя) или null",
              "best_quote": "самая смешная/глупая цитата или null"
            }
            """;

        var factsPrompt = $"""
            Контекст из чата:
            {context}

            Вопрос от {askerName}: {question}

            Извлеки факты ТОЛЬКО из контекста выше. Не додумывай.
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

        // STAGE 2: Add humor based ONLY on structured facts
        var humorPrompt = $"""
            Спрашивает: {askerName}
            Вопрос: {question}

            Структурированные факты из чата (JSON):
            {factsResponse.Content}

            ПРАВИЛА:
            1. Используй ТОЛЬКО факты из JSON выше
            2. НЕ придумывай новых фактов или цитат
            3. Если в JSON есть "roast_target" — подколи его
            4. ОБЯЗАТЕЛЬНО включи цитату: если есть "best_quote" — вставь её дословно в <i>кавычках</i>
            5. Ссылайся на конкретные высказывания из "facts" — кто что сказал
            6. Ответ должен быть дерзким и с матом

            Формат: 2-4 предложения с цитатой, HTML для <b> и <i>.
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

    private static string FormatResponse(string question, string answer, List<SearchResult> topSources)
    {
        // If no sources or low quality, just return answer
        if (topSources.Count == 0 || topSources[0].Similarity < 0.4)
            return answer;

        // Add source context footer for transparency
        var sb = new StringBuilder(answer);

        // Show top 2-3 sources with timestamps
        var sourcesToShow = topSources
            .Where(s => s.Similarity >= 0.35 && !string.IsNullOrWhiteSpace(s.ChunkText))
            .Take(3)
            .ToList();

        if (sourcesToShow.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.Append("<i>📎 Контекст: ");

            var sourceSnippets = sourcesToShow.Select(s =>
            {
                var text = TruncateText(s.ChunkText.Replace("\n", " ").Trim(), 60);
                return $"\"{text}\"";
            });

            sb.Append(string.Join(" · ", sourceSnippets));
            sb.Append("</i>");
        }

        return sb.ToString();
    }

    private static string TruncateText(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            return text;
        return text[..(maxLength - 3)] + "...";
    }

    private static string EscapeHtml(string text)
    {
        return text
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");
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
    /// Rewrite query for better search: expand abbreviations, add context, transliterate names
    /// </summary>
    private async Task<(string rewritten, long timeMs)> RewriteQueryForSearchAsync(string query, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var systemPrompt = """
                Ты — помощник для улучшения поисковых запросов.

                Твоя задача: переписать запрос пользователя так, чтобы он лучше находился в базе сообщений.

                Правила:
                1. Расшифруй аббревиатуры и сокращения (SGA → Shai Gilgeous-Alexander, МУ → Манчестер Юнайтед)
                2. Добавь альтернативные написания имён (Лука Дончич → Luka Dončić)
                3. Добавь контекст/категорию если очевидно (баскетболисты → NBA, футболисты → футбол)
                4. Сохрани оригинальные слова тоже
                5. Используй и русский, и английский варианты
                6. Максимум 3 строки, разделённые переносом

                Отвечай ТОЛЬКО переписанным запросом, без объяснений.
                Если запрос уже хороший — верни как есть.
                """;

            var response = await _llmRouter.CompleteWithFallbackAsync(
                new LlmRequest
                {
                    SystemPrompt = systemPrompt,
                    UserPrompt = query,
                    Temperature = 0.2 // Low for consistency
                },
                preferredTag: null, // Use default cheap provider
                ct: ct);

            sw.Stop();

            var rewritten = response.Content.Trim();

            // Safety: if LLM returned garbage or too long, use original
            if (string.IsNullOrWhiteSpace(rewritten) || rewritten.Length > 500 || rewritten.Length < query.Length / 2)
            {
                _logger.LogWarning("[QueryRewrite] LLM returned invalid response, using original query");
                return (query, sw.ElapsedMilliseconds);
            }

            _logger.LogInformation("[QueryRewrite] '{Original}' → '{Rewritten}' ({Ms}ms)",
                TruncateText(query, 50), TruncateText(rewritten, 100), sw.ElapsedMilliseconds);

            return (rewritten, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogWarning(ex, "[QueryRewrite] Failed, using original query");
            return (query, sw.ElapsedMilliseconds);
        }
    }
}
