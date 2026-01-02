using System.Diagnostics;
using System.Text;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using WatchmenBot.Features.Search.Models;
using WatchmenBot.Infrastructure.Settings;
using WatchmenBot.Features.Messages.Services;
using WatchmenBot.Features.Admin.Services;
using WatchmenBot.Features.Webhook.Services;
using WatchmenBot.Features.Llm.Services;

namespace WatchmenBot.Features.Search;

public class FactCheckHandler(
    ITelegramBotClient bot,
    MessageStore messageStore,
    LlmRouter llmRouter,
    PromptSettingsStore promptSettings,
    DebugService debugService,
    ILogger<FactCheckHandler> logger)
{
    /// <summary>
    /// Handle /truth command - fact-check last N messages
    /// </summary>
    public async Task HandleAsync(Message message, CancellationToken ct)
    {
        var chatId = message.Chat.Id;

        // Parse optional count from command (default 5)
        var count = ParseCount(message.Text, defaultCount: 5, maxCount: 15);

        // Initialize debug report
        var debugReport = new DebugReport
        {
            Command = "truth",
            ChatId = chatId,
            Query = $"Fact-check last {count} messages"
        };

        try
        {
            await bot.SendChatAction(chatId, ChatAction.Typing, cancellationToken: ct);

            logger.LogInformation("[TRUTH] Checking last {Count} messages in chat {ChatId}", count, chatId);

            // Get latest messages
            var messages = await messageStore.GetLatestMessagesAsync(chatId, count);

            if (messages.Count == 0)
            {
                await bot.SendMessage(
                    chatId: chatId,
                    text: "Не нашёл сообщений для проверки.",
                    replyParameters: new ReplyParameters { MessageId = message.MessageId },
                    cancellationToken: ct);
                return;
            }

            // Build conversation context
            var rawContext = BuildContext(messages);

            // Expand abbreviations and names for better Perplexity understanding
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

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var response = await llmRouter.CompleteWithFallbackAsync(
                new LlmRequest
                {
                    SystemPrompt = settings.SystemPrompt,
                    UserPrompt = userPrompt,
                    Temperature = 0.5 // Balanced for accuracy + humor
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

            logger.LogDebug("[TRUTH] Used provider: {Provider}", response.Provider);

            // Sanitize HTML for Telegram
            var sanitizedResponse = TelegramHtmlSanitizer.Sanitize(response.Content);

            // Send response
            await bot.SendMessage(
                chatId: chatId,
                text: sanitizedResponse,
                parseMode: ParseMode.Html,
                linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true },
                replyParameters: new ReplyParameters { MessageId = message.MessageId },
                cancellationToken: ct);

            logger.LogInformation("[TRUTH] Completed fact-check for {Count} messages", messages.Count);

            // Send debug report to admin
            await debugService.SendDebugReportAsync(debugReport, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[TRUTH] Failed to fact-check in chat {ChatId}", chatId);

            await bot.SendMessage(
                chatId: chatId,
                text: "Произошла ошибка при проверке фактов. Попробуйте позже.",
                replyParameters: new ReplyParameters { MessageId = message.MessageId },
                cancellationToken: ct);
        }
    }

    private static int ParseCount(string? text, int defaultCount, int maxCount)
    {
        if (string.IsNullOrWhiteSpace(text))
            return defaultCount;

        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return defaultCount;

        if (int.TryParse(parts[1], out var count) && count > 0)
            return Math.Min(count, maxCount);

        return defaultCount;
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

    /// <summary>
    /// Expand abbreviations and names in context for better Perplexity fact-checking
    /// </summary>
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
                    Temperature = 0.1 // Very low for consistency
                },
                preferredTag: null, // Use default cheap provider
                ct: ct);

            sw.Stop();

            var expanded = response.Content.Trim();

            // Safety checks
            if (string.IsNullOrWhiteSpace(expanded) ||
                expanded.Length > context.Length * 3 || // Too much expansion
                expanded.Length < context.Length / 2)   // Lost content
            {
                logger.LogWarning("[FactCheck] Context expansion returned invalid result, using original");
                return (context, sw.ElapsedMilliseconds);
            }

            logger.LogInformation("[FactCheck] Expanded context: {Original} → {Expanded} chars ({Ms}ms)",
                context.Length, expanded.Length, sw.ElapsedMilliseconds);

            return (expanded, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            logger.LogWarning(ex, "[FactCheck] Context expansion failed, using original");
            return (context, sw.ElapsedMilliseconds);
        }
    }
}
