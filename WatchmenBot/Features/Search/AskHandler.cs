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
    private readonly ILogger<AskHandler> _logger;

    public AskHandler(
        ITelegramBotClient bot,
        EmbeddingService embeddingService,
        LlmRouter llmRouter,
        PromptSettingsStore promptSettings,
        ILogger<AskHandler> logger)
    {
        _bot = bot;
        _embeddingService = embeddingService;
        _llmRouter = llmRouter;
        _promptSettings = promptSettings;
        _logger = logger;
    }

    /// <summary>
    /// Handle /ask command (дерзкий ответ с подъёбкой)
    /// </summary>
    public Task HandleAsync(Message message, CancellationToken ct)
        => HandleAsync(message, "ask", ct);

    /// <summary>
    /// Handle /q command (серьёзный вопрос)
    /// </summary>
    public Task HandleQuestionAsync(Message message, CancellationToken ct)
        => HandleAsync(message, "q", ct);

    private async Task HandleAsync(Message message, string command, CancellationToken ct)
    {
        var chatId = message.Chat.Id;
        var question = ParseQuestion(message.Text);

        if (string.IsNullOrWhiteSpace(question))
        {
            var helpText = command == "q"
                ? """
                    🤔 <b>Задай любой серьёзный вопрос</b>

                    По чату:
                    • <code>/q о чём договорились по проекту?</code>
                    • <code>/q что решили насчёт дедлайна?</code>

                    Общие вопросы:
                    • <code>/q сколько стоит трактор в РФ?</code>
                    • <code>/q как работает async/await?</code>
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

        try
        {
            await _bot.SendChatAction(chatId, ChatAction.Typing, cancellationToken: ct);

            _logger.LogInformation("[{Command}] Question: {Question} in chat {ChatId}", command.ToUpper(), question, chatId);

            // Get relevant context from embeddings (increased limit for better context)
            var results = await _embeddingService.SearchSimilarAsync(chatId, question, limit: 20, ct);

            // For /ask - require context, for /q - context is optional
            if (results.Count == 0 && command == "ask")
            {
                await _bot.SendMessage(
                    chatId: chatId,
                    text: "Не нашёл релевантной информации в истории чата. Возможно, эмбеддинги ещё не созданы.",
                    replyParameters: new ReplyParameters { MessageId = message.MessageId },
                    cancellationToken: ct);
                return;
            }

            // Build context from search results (may be empty for /q)
            var context = results.Count > 0 ? BuildContext(results) : null;

            // Get asker's name
            var askerName = GetDisplayName(message.From);

            // Generate answer using LLM with command-specific prompt
            var answer = await GenerateAnswerAsync(command, question, context, askerName, ct);

            // Format response with sources
            var response = FormatResponse(question, answer, results.Take(3).ToList());

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

            _logger.LogInformation("[{Command}] Answered question: {Question}", command.ToUpper(), question);
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

    private async Task<string> GenerateAnswerAsync(string command, string question, string? context, string askerName, CancellationToken ct)
    {
        var settings = await _promptSettings.GetSettingsAsync(command);

        // For /ask with context - use two-stage generation
        if (command == "ask" && !string.IsNullOrWhiteSpace(context))
        {
            return await GenerateTwoStageAnswerAsync(question, context, askerName, settings, ct);
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

        _logger.LogInformation("[{Command}] LLM: provider={Provider}, model={Model}, tag={Tag}",
            command.ToUpper(), response.Provider, response.Model, settings.LlmTag ?? "default");

        return response.Content;
    }

    /// <summary>
    /// Two-stage generation for /ask: extract facts first, then add humor
    /// </summary>
    private async Task<string> GenerateTwoStageAnswerAsync(
        string question, string context, string askerName, PromptSettings settings, CancellationToken ct)
    {
        // STAGE 1: Extract facts with low temperature
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

        var factsResponse = await _llmRouter.CompleteWithFallbackAsync(
            new LlmRequest
            {
                SystemPrompt = "Ты — аналитик чата. Извлекай факты точно и кратко.",
                UserPrompt = factsPrompt,
                Temperature = 0.3 // Low for accuracy
            },
            preferredTag: settings.LlmTag, // Use same provider (Qwen) for both stages
            ct: ct);

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

        var finalResponse = await _llmRouter.CompleteWithFallbackAsync(
            new LlmRequest
            {
                SystemPrompt = settings.SystemPrompt,
                UserPrompt = humorPrompt,
                Temperature = 0.6 // Higher for creativity
            },
            preferredTag: settings.LlmTag,
            ct: ct);

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
}
