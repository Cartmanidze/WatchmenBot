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
                    🤔 <b>Задай любой серьёзный вопрос</b>

                    По чату:
                    • <code>/smart о чём договорились по проекту?</code>
                    • <code>/smart что решили насчёт дедлайна?</code>

                    Общие вопросы:
                    • <code>/smart сколько стоит трактор в РФ?</code>
                    • <code>/smart как работает async/await?</code>
                    """
                : """
                    🎭 <b>Спроси меня про кого-то из чата!</b>

                    Примеры:
                    • <code>/ask что за тип этот Глеб?</code>
                    • <code>/ask кто тут самый активный?</code>
                    • <code>/ask что думает Женя о работе?</code>
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

            // Choose search strategy based on question type
            SearchResponse searchResponse;

            if (personalTarget == "self" && command == "ask")
            {
                // Personal question about self — use personal retrieval
                _logger.LogInformation("[ASK] Personal question detected: self ({Name}/{Username})", askerName, askerUsername);
                searchResponse = await _embeddingService.GetPersonalContextAsync(
                    chatId,
                    askerUsername ?? askerName,
                    askerName,
                    days: 7,
                    ct);
            }
            else if (personalTarget != null && personalTarget.StartsWith("@") && command == "ask")
            {
                // Question about @someone — use personal retrieval for that user
                var targetUsername = personalTarget.TrimStart('@');
                _logger.LogInformation("[ASK] Personal question detected: @{Target}", targetUsername);
                searchResponse = await _embeddingService.GetPersonalContextAsync(
                    chatId,
                    targetUsername,
                    null, // don't know display name
                    days: 7,
                    ct);
            }
            else
            {
                // Regular semantic search
                searchResponse = await _embeddingService.SearchWithConfidenceAsync(chatId, question, limit: 20, ct);
            }

            // Collect debug info for search results
            debugReport.PersonalTarget = personalTarget;
            debugReport.SearchResults = searchResponse.Results.Select(r => new DebugSearchResult
            {
                Similarity = r.Similarity,
                Distance = r.Distance,
                MessageIds = new[] { r.MessageId },
                Text = r.ChunkText,
                Timestamp = ParseTimestamp(r.MetadataJson),
                IsNewsDump = r.IsNewsDump
            }).ToList();
            debugReport.SearchConfidence = searchResponse.Confidence.ToString();
            debugReport.SearchConfidenceReason = searchResponse.ConfidenceReason;
            debugReport.BestScore = searchResponse.BestScore;
            debugReport.ScoreGap = searchResponse.ScoreGap;
            debugReport.HasFullTextMatch = searchResponse.HasFullTextMatch;

            // Handle confidence gate
            var results = searchResponse.Results;
            string? context = null;
            string? confidenceWarning = null;

            if (command == "ask")
            {
                // /ask requires context
                if (searchResponse.Confidence == SearchConfidence.None)
                {
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

                context = BuildContext(results);
            }
            else // /smart
            {
                // /smart - context is optional, but warn if weak
                if (results.Count > 0 && searchResponse.Confidence != SearchConfidence.None)
                {
                    context = BuildContext(results);
                    if (searchResponse.Confidence == SearchConfidence.Low)
                    {
                        confidenceWarning = "⚠️ <i>Контекст из чата слабый</i>\n\n";
                    }
                }
            }

            // Collect debug info for context
            if (context != null)
            {
                debugReport.ContextSent = context;
                debugReport.ContextMessagesCount = results.Count;
                debugReport.ContextTokensEstimate = EstimateTokens(context);
            }

            // Generate answer using LLM with command-specific prompt
            var answer = await GenerateAnswerWithDebugAsync(command, question, context, askerName, debugReport, ct);

            // Format response with confidence warning if needed
            var response = (confidenceWarning ?? "") + FormatResponse(question, answer, results.Take(3).ToList());

            try
            {
                await _bot.SendMessage(
                    chatId: chatId,
                    text: response,
                    parseMode: ParseMode.Html,
                    linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true },
                    replyParameters: new ReplyParameters { MessageId = message.MessageId },
                    cancellationToken: ct);
            }
            catch (Telegram.Bot.Exceptions.ApiRequestException)
            {
                // Fallback to plain text
                var plainText = System.Text.RegularExpressions.Regex.Replace(response, "<[^>]+>", "");
                await _bot.SendMessage(
                    chatId: chatId,
                    text: plainText,
                    replyParameters: new ReplyParameters { MessageId = message.MessageId },
                    cancellationToken: ct);
            }

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

    private string BuildContext(List<SearchResult> results)
    {
        _logger.LogDebug("[BuildContext] Processing {Count} search results", results.Count);

        // Parse metadata to get timestamps and sort chronologically
        var messagesWithTime = results
            .Select((r, index) => {
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
                        _logger.LogDebug("[BuildContext] #{Index} sim={Similarity:F3} time={Time} text={Text}",
                            index, r.Similarity, time, TruncateText(r.ChunkText, 80));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[BuildContext] Failed to parse metadata: {Json}", r.MetadataJson);
                    }
                }
                else
                {
                    _logger.LogDebug("[BuildContext] #{Index} sim={Similarity:F3} NO METADATA text={Text}",
                        index, r.Similarity, TruncateText(r.ChunkText, 80));
                }
                return (Text: r.ChunkText, Time: time, Similarity: r.Similarity);
            })
            .OrderBy(m => m.Time) // Chronological order
            .ToList();

        _logger.LogInformation("[BuildContext] Built context: {Count} messages, time range: {From} - {To}",
            messagesWithTime.Count,
            messagesWithTime.FirstOrDefault().Time,
            messagesWithTime.LastOrDefault().Time);

        var sb = new StringBuilder();
        sb.AppendLine("Релевантные сообщения из чата (хронологически):");
        sb.AppendLine();

        foreach (var msg in messagesWithTime)
        {
            var timeStr = msg.Time != DateTimeOffset.MinValue
                ? $"[{msg.Time.ToLocalTime():dd.MM HH:mm}] "
                : "";
            sb.AppendLine($"{timeStr}{msg.Text}");
        }

        return sb.ToString();
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

        // STAGE 1: Extract facts with low temperature
        var factsSystemPrompt = "Ты — аналитик чата. Извлекай факты точно и кратко.";
        var factsPrompt = $"""
            Сегодняшняя дата: {DateTime.UtcNow:dd.MM.yyyy}

            Контекст из чата:
            {context}

            Спрашивает: {askerName}
            Вопрос: {question}

            ЗАДАЧА: Кратко ответь на вопрос на основе контекста.
            - Кто связан с этой темой? (имена)
            - Что конкретно они говорили/делали?
            - Есть ли смешные или глупые цитаты?

            Формат: просто факты, 2-4 предложения.
            """;

        var stage1Sw = System.Diagnostics.Stopwatch.StartNew();
        var factsResponse = await _llmRouter.CompleteWithFallbackAsync(
            new LlmRequest
            {
                SystemPrompt = factsSystemPrompt,
                UserPrompt = factsPrompt,
                Temperature = 0.3 // Low for accuracy
            },
            preferredTag: settings.LlmTag,
            ct: ct);
        stage1Sw.Stop();

        debugReport.Stages.Add(new DebugStage
        {
            StageNumber = 1,
            Name = "Facts",
            Temperature = 0.3,
            SystemPrompt = factsSystemPrompt,
            UserPrompt = factsPrompt,
            Response = factsResponse.Content,
            Tokens = factsResponse.TotalTokens,
            TimeMs = stage1Sw.ElapsedMilliseconds
        });

        _logger.LogInformation("[ASK] Stage 1 (facts): {Length} chars", factsResponse.Content.Length);

        // STAGE 2: Add humor with higher temperature
        var humorPrompt = $"""
            Спрашивает: {askerName}
            Вопрос: {question}

            Факты из чата:
            {factsResponse.Content}

            Теперь ответь дерзко и с подъёбкой на основе этих фактов.
            Подколи того, кто связан с темой (не спрашивающего, если вопрос не про него).
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
        // Просто возвращаем ответ без формального оформления
        return answer;
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
}
