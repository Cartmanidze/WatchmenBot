using System.Diagnostics;
using System.Text;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;
using WatchmenBot.Extensions;
using WatchmenBot.Features.Admin.Services;
using WatchmenBot.Features.Messages.Services;
using WatchmenBot.Features.Webhook.Services;
using WatchmenBot.Features.Llm.Services;
using WatchmenBot.Infrastructure.Settings;

namespace WatchmenBot.Features.Search.Services;

/// <summary>
/// Core processing service for /truth fact-checking command.
/// Extracted from BackgroundTruthWorker to enable direct testing and Hangfire integration.
/// </summary>
public class TruthProcessingService(
    MessageStore messageStore,
    LlmRouter llmRouter,
    PromptSettingsStore promptSettings,
    ChatStatusService chatStatusService,
    DebugService debugService,
    ITelegramBotClient bot,
    ILogger<TruthProcessingService> logger)
{
    /// <summary>
    /// Process fact-checking request and send response.
    /// </summary>
    public async Task<TruthProcessingResult> ProcessAsync(TruthQueueItem item, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        logger.LogInformation("[TruthProcessing] Processing fact-check for chat {ChatId}, {Count} messages, requested by @{User}",
            item.ChatId, item.MessageCount, item.RequestedBy);

        // Initialize debug report
        var debugReport = new DebugReport
        {
            Command = "truth",
            ChatId = item.ChatId,
            Query = $"Fact-check last {item.MessageCount} messages"
        };

        // Send typing action (safe: deactivates chat on 403)
        if (!await bot.TrySendChatActionAsync(chatStatusService, item.ChatId, ChatAction.Typing, logger, ct))
        {
            // Chat was deactivated - no point continuing
            sw.Stop();
            return new TruthProcessingResult
            {
                Success = false,
                MessageCount = 0,
                ElapsedSeconds = sw.Elapsed.TotalSeconds
            };
        }

        // Get latest messages
        var messages = await messageStore.GetLatestMessagesAsync(item.ChatId, item.MessageCount);

        if (messages.Count == 0)
        {
            try
            {
                await bot.SendMessageSafeAsync(
                    chatStatusService,
                    item.ChatId,
                    "Не нашёл сообщений для проверки.",
                    logger,
                    replyToMessageId: item.ReplyToMessageId,
                    ct: ct);
            }
            catch (ChatDeactivatedException)
            {
                // Chat deactivated - return without error
            }

            sw.Stop();
            return new TruthProcessingResult
            {
                Success = true,
                MessageCount = 0,
                ElapsedSeconds = sw.Elapsed.TotalSeconds
            };
        }

        // Build conversation context
        var rawContext = BuildContext(messages);

        // Expand abbreviations and names for better fact-checking
        var (context, rewriteMs) = await ExpandContextForFactCheckAsync(rawContext, ct);
        debugReport.RewrittenQuery = context != rawContext ? context : null;
        debugReport.QueryRewriteTimeMs = rewriteMs;

        // Collect debug info for context
        debugReport.ContextSent = context;
        debugReport.ContextMessagesCount = messages.Count;
        debugReport.ContextTokensEstimate = context.Length / 4;

        // Get prompt settings
        var settings = await promptSettings.GetSettingsAsync("truth");

        var userPrompt = $"""
            Сегодняшняя дата: {DateTime.UtcNow:dd.MM.yyyy}

            Последние {messages.Count} сообщений из чата:

            {context}

            Проанализируй и проверь факты.
            """;

        // Send typing action again (safe: deactivates chat on 403)
        if (!await bot.TrySendChatActionAsync(chatStatusService, item.ChatId, ChatAction.Typing, logger, ct))
        {
            // Chat was deactivated - no point continuing
            sw.Stop();
            return new TruthProcessingResult
            {
                Success = false,
                MessageCount = messages.Count,
                ElapsedSeconds = sw.Elapsed.TotalSeconds
            };
        }

        var llmSw = Stopwatch.StartNew();
        var response = await llmRouter.CompleteWithFallbackAsync(
            new LlmRequest
            {
                SystemPrompt = settings.SystemPrompt,
                UserPrompt = userPrompt,
                Temperature = 0.5
            },
            preferredTag: settings.LlmTag,
            ct: ct);
        llmSw.Stop();

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
        debugReport.LlmTimeMs = llmSw.ElapsedMilliseconds;

        // Sanitize HTML for Telegram
        var sanitizedResponse = TelegramHtmlSanitizer.Sanitize(response.Content);

        // Send response (safe: handles 403 and HTML fallback)
        try
        {
            await bot.SendHtmlMessageSafeAsync(
                chatStatusService,
                item.ChatId,
                sanitizedResponse,
                logger,
                replyToMessageId: item.ReplyToMessageId,
                ct: ct);
        }
        catch (ChatDeactivatedException)
        {
            // Chat was deactivated - job should not retry
            sw.Stop();
            return new TruthProcessingResult
            {
                Success = false,
                MessageCount = messages.Count,
                ElapsedSeconds = sw.Elapsed.TotalSeconds
            };
        }

        sw.Stop();
        logger.LogInformation("[TruthProcessing] Fact-check completed in {Elapsed:F1}s for {Count} messages",
            sw.Elapsed.TotalSeconds, messages.Count);

        // Send debug report
        await debugService.SendDebugReportAsync(debugReport, ct);

        return new TruthProcessingResult
        {
            Success = true,
            MessageCount = messages.Count,
            ElapsedSeconds = sw.Elapsed.TotalSeconds
        };
    }

    private static string BuildContext(List<WatchmenBot.Models.MessageRecord> messages)
    {
        var sb = new StringBuilder();
        foreach (var m in messages)
        {
            var name = string.IsNullOrWhiteSpace(m.DisplayName)
                ? (string.IsNullOrWhiteSpace(m.Username) ? m.FromUserId.ToString() : m.Username)
                : m.DisplayName;

            var text = string.IsNullOrWhiteSpace(m.Text) ? "[медиа]" : m.Text;
            sb.AppendLine($"[{m.DateUtc.ToLocalTime():HH:mm}] {name}: {text}");
        }
        return sb.ToString();
    }

    private async Task<(string expanded, long timeMs)> ExpandContextForFactCheckAsync(string context, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            const string systemPrompt = """
                Ты — помощник для подготовки текста к фактчеку.

                Твоя задача: расшифровать аббревиатуры и сокращения в сообщениях, чтобы поисковик лучше понял о чём речь.

                Правила:
                1. Расшифруй известные аббревиатуры: SGA → SGA (Shai Gilgeous-Alexander), МУ → МУ (Манчестер Юнайтед)
                2. Добавь контекст в скобках рядом с первым упоминанием
                3. НЕ меняй структуру сообщений — только добавляй пояснения
                4. НЕ меняй имена авторов сообщений
                5. Если нет аббревиатур — верни текст как есть
                6. Сохрани формат [время] автор: текст

                Пример:
                Вход: "[14:30] Вася: SGA лучше Эдварда"
                Выход: "[14:30] Вася: SGA (Shai Gilgeous-Alexander, NBA) лучше Эдварда (Anthony Edwards, NBA)"

                Отвечай ТОЛЬКО обработанным текстом, без объяснений.
                """;

            var response = await llmRouter.CompleteWithFallbackAsync(
                new LlmRequest
                {
                    SystemPrompt = systemPrompt,
                    UserPrompt = context,
                    Temperature = 0.1
                },
                preferredTag: null,
                ct: ct);

            sw.Stop();

            var expanded = response.Content.Trim();

            // Safety checks
            if (string.IsNullOrWhiteSpace(expanded) ||
                expanded.Length > context.Length * 3 ||
                expanded.Length < context.Length / 2)
            {
                logger.LogWarning("[TruthProcessing] Context expansion returned invalid result, using original");
                return (context, sw.ElapsedMilliseconds);
            }

            logger.LogInformation("[TruthProcessing] Expanded context: {Original} → {Expanded} chars ({Ms}ms)",
                context.Length, expanded.Length, sw.ElapsedMilliseconds);

            return (expanded, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            logger.LogWarning(ex, "[TruthProcessing] Context expansion failed, using original");
            return (context, sw.ElapsedMilliseconds);
        }
    }

}

/// <summary>
/// Result of processing /truth request.
/// </summary>
public class TruthProcessingResult
{
    public bool Success { get; init; }
    public int MessageCount { get; init; }
    public double ElapsedSeconds { get; init; }
}
