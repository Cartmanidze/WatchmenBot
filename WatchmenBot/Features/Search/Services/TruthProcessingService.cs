using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
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
public partial class TruthProcessingService(
    MessageStore messageStore,
    LlmRouter llmRouter,
    PromptSettingsStore promptSettings,
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

        // Send typing action
        await bot.SendChatAction(item.ChatId, ChatAction.Typing, cancellationToken: ct);

        // Get latest messages
        var messages = await messageStore.GetLatestMessagesAsync(item.ChatId, item.MessageCount);

        if (messages.Count == 0)
        {
            await bot.SendMessage(
                chatId: item.ChatId,
                text: "Не нашёл сообщений для проверки.",
                replyParameters: new ReplyParameters { MessageId = item.ReplyToMessageId },
                cancellationToken: ct);

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

        // Send typing action again (long operation)
        await bot.SendChatAction(item.ChatId, ChatAction.Typing, cancellationToken: ct);

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

        // Send response
        await SendTruthResponseAsync(item.ChatId, item.ReplyToMessageId, sanitizedResponse, ct);

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

    private async Task SendTruthResponseAsync(long chatId, int replyToMessageId, string response, CancellationToken ct)
    {
        try
        {
            await bot.SendMessage(
                chatId: chatId,
                text: response,
                parseMode: ParseMode.Html,
                linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true },
                replyParameters: new ReplyParameters { MessageId = replyToMessageId },
                cancellationToken: ct);
        }
        catch (Telegram.Bot.Exceptions.ApiRequestException ex) when (ex.Message.Contains("can't parse entities"))
        {
            logger.LogWarning("[TruthProcessing] HTML parsing failed, sending as plain text");
            var plainText = HtmlTagRegex().Replace(response, "");
            await bot.SendMessage(
                chatId: chatId,
                text: plainText,
                linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true },
                replyParameters: new ReplyParameters { MessageId = replyToMessageId },
                cancellationToken: ct);
        }
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

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex HtmlTagRegex();
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
