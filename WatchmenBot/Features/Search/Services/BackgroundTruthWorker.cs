using System.Diagnostics;
using System.Text;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using WatchmenBot.Features.Messages.Services;
using WatchmenBot.Features.Admin.Services;
using WatchmenBot.Features.Webhook.Services;
using WatchmenBot.Features.Llm.Services;
using WatchmenBot.Infrastructure.Database;
using WatchmenBot.Infrastructure.Queue;
using WatchmenBot.Infrastructure.Settings;

namespace WatchmenBot.Features.Search.Services;

/// <summary>
/// Background worker for /truth fact-checking.
/// Uses PostgreSQL LISTEN/NOTIFY for instant notifications with polling fallback.
/// </summary>
public partial class BackgroundTruthWorker(
    TruthQueueService queue,
    PostgresNotificationService notifications,
    QueueMetrics queueMetrics,
    ITelegramBotClient bot,
    IServiceProvider serviceProvider,
    ILogger<BackgroundTruthWorker> logger)
    : BackgroundService
{
    private const string QueueName = "truth";
    private static readonly TimeSpan NotificationTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan StaleRecoveryInterval = TimeSpan.FromMinutes(2);
    private DateTime _lastCleanup = DateTime.UtcNow;
    private DateTime _lastStaleRecovery = DateTime.UtcNow;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("[BackgroundTruth] Worker started (atomic pick + LISTEN/NOTIFY)");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Periodically recover stale tasks (crashed workers)
                await PeriodicStaleRecoveryAsync();

                // Atomic pick: SELECT + UPDATE in one query, no race conditions
                var item = await queue.PickNextAsync();

                if (item == null)
                {
                    // Queue empty — wait for notification OR timeout before next check
                    queueMetrics.UpdatePendingCount(QueueName, 0);
                    await WaitForNotificationOrTimeoutAsync(stoppingToken);
                    await PeriodicCleanupAsync();
                    continue;
                }

                // Task already marked as started by PickNextAsync
                var startTime = DateTimeOffset.UtcNow;
                queueMetrics.RecordTaskPicked(QueueName);

                try
                {
                    await ProcessTruthRequestAsync(item, stoppingToken);
                    await queue.MarkAsCompletedAsync(item.Id);

                    var duration = DateTimeOffset.UtcNow - startTime;
                    var waitTime = startTime - item.RequestedAt;
                    queueMetrics.RecordTaskCompleted(QueueName, duration, waitTime);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "[BackgroundTruth] Failed to process fact-check for chat {ChatId}", item.ChatId);

                    await queue.MarkAsFailedAsync(item.Id, ex.Message);
                    queueMetrics.RecordTaskFailed(QueueName, item.AttemptCount, ex.GetType().Name);

                    try
                    {
                        await bot.SendMessage(
                            chatId: item.ChatId,
                            text: "Произошла ошибка при проверке фактов. Попробуйте позже.",
                            replyParameters: new ReplyParameters { MessageId = item.ReplyToMessageId },
                            cancellationToken: stoppingToken);
                    }
                    catch (Exception sendEx)
                    {
                        logger.LogWarning(sendEx, "[BackgroundTruth] Failed to send error notification to chat {ChatId}", item.ChatId);
                    }
                }
                // Continue immediately to drain queue (no wait between items)
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[BackgroundTruth] Error in worker loop");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }

        logger.LogInformation("[BackgroundTruth] Worker stopped");
    }

    /// <summary>
    /// Wait for a notification or timeout (whichever comes first).
    /// </summary>
    private async Task WaitForNotificationOrTimeoutAsync(CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(NotificationTimeout);

        try
        {
            await notifications.TruthQueueNotifications.WaitToReadAsync(timeoutCts.Token);

            while (notifications.TruthQueueNotifications.TryRead(out var itemId))
            {
                logger.LogDebug("[BackgroundTruth] Received notification for item {ItemId}", itemId);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timeout - this is normal, we'll poll the DB
        }
    }

    private async Task PeriodicCleanupAsync()
    {
        if (DateTime.UtcNow - _lastCleanup > CleanupInterval)
        {
            await queue.CleanupOldAsync(daysToKeep: 7);
            _lastCleanup = DateTime.UtcNow;
        }
    }

    private async Task PeriodicStaleRecoveryAsync()
    {
        if (DateTime.UtcNow - _lastStaleRecovery > StaleRecoveryInterval)
        {
            await queue.RecoverStaleTasksAsync();
            _lastStaleRecovery = DateTime.UtcNow;
        }
    }

    private async Task ProcessTruthRequestAsync(TruthQueueItem item, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        logger.LogInformation("[BackgroundTruth] Processing fact-check for chat {ChatId}, {Count} messages, requested by @{User}",
            item.ChatId, item.MessageCount, item.RequestedBy);

        using var scope = serviceProvider.CreateScope();
        var messageStore = scope.ServiceProvider.GetRequiredService<MessageStore>();
        var llmRouter = scope.ServiceProvider.GetRequiredService<LlmRouter>();
        var promptSettings = scope.ServiceProvider.GetRequiredService<PromptSettingsStore>();
        var debugService = scope.ServiceProvider.GetRequiredService<DebugService>();

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
            return;
        }

        // Build conversation context
        var rawContext = BuildContext(messages);

        // Expand abbreviations and names for better fact-checking
        var (context, rewriteMs) = await ExpandContextForFactCheckAsync(rawContext, llmRouter, ct);
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
        try
        {
            await bot.SendMessage(
                chatId: item.ChatId,
                text: sanitizedResponse,
                parseMode: ParseMode.Html,
                linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true },
                replyParameters: new ReplyParameters { MessageId = item.ReplyToMessageId },
                cancellationToken: ct);
        }
        catch (Telegram.Bot.Exceptions.ApiRequestException ex) when (ex.Message.Contains("can't parse entities"))
        {
            logger.LogWarning("[BackgroundTruth] HTML parsing failed, sending as plain text");
            var plainText = HtmlTagRegex().Replace(sanitizedResponse, "");
            await bot.SendMessage(
                chatId: item.ChatId,
                text: plainText,
                linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true },
                replyParameters: new ReplyParameters { MessageId = item.ReplyToMessageId },
                cancellationToken: ct);
        }

        sw.Stop();
        logger.LogInformation("[BackgroundTruth] Fact-check completed in {Elapsed:F1}s for {Count} messages",
            sw.Elapsed.TotalSeconds, messages.Count);

        // Send debug report
        await debugService.SendDebugReportAsync(debugReport, ct);
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

    private async Task<(string expanded, long timeMs)> ExpandContextForFactCheckAsync(
        string context, LlmRouter llmRouter, CancellationToken ct)
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
                logger.LogWarning("[BackgroundTruth] Context expansion returned invalid result, using original");
                return (context, sw.ElapsedMilliseconds);
            }

            logger.LogInformation("[BackgroundTruth] Expanded context: {Original} → {Expanded} chars ({Ms}ms)",
                context.Length, expanded.Length, sw.ElapsedMilliseconds);

            return (expanded, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            logger.LogWarning(ex, "[BackgroundTruth] Context expansion failed, using original");
            return (context, sw.ElapsedMilliseconds);
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex("<[^>]+>")]
    private static partial System.Text.RegularExpressions.Regex HtmlTagRegex();
}
