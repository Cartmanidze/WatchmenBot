using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using WatchmenBot.Features.Messages.Services;
using WatchmenBot.Features.Admin.Services;

namespace WatchmenBot.Features.Summary.Services;

/// <summary>
/// Background worker for summary generation.
/// Uses PostgreSQL-backed queue for reliable, persistent processing.
/// </summary>
public partial class BackgroundSummaryWorker(
    SummaryQueueService queue,
    ITelegramBotClient bot,
    IServiceProvider serviceProvider,
    LogCollector logCollector,
    ILogger<BackgroundSummaryWorker> logger)
    : BackgroundService
{
    private static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(6);
    private DateTime _lastCleanup = DateTime.UtcNow;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("[BackgroundSummary] Worker started (PostgreSQL-backed queue)");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Get pending requests from DB
                var items = await queue.GetPendingAsync(limit: 5);

                if (items.Count == 0)
                {
                    // No work - wait longer before checking again
                    await Task.Delay(IdleInterval, stoppingToken);
                    await PeriodicCleanupAsync();
                    continue;
                }

                // Process each request
                foreach (var item in items)
                {
                    if (stoppingToken.IsCancellationRequested) break;

                    try
                    {
                        await queue.MarkAsStartedAsync(item.Id);
                        await ProcessSummaryRequestAsync(item, stoppingToken);
                        await queue.MarkAsCompletedAsync(item.Id);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "[BackgroundSummary] Failed to process summary for chat {ChatId}", item.ChatId);

                        await queue.MarkAsFailedAsync(item.Id, ex.Message);

                        try
                        {
                            await bot.SendMessage(
                                chatId: item.ChatId,
                                text: "Произошла ошибка при генерации выжимки. Попробуйте позже.",
                                replyParameters: new ReplyParameters { MessageId = item.ReplyToMessageId },
                                cancellationToken: stoppingToken);
                        }
                        catch (Exception sendEx)
                        {
                            logger.LogWarning(sendEx, "[BackgroundSummary] Failed to send error notification to chat {ChatId}", item.ChatId);
                        }
                    }
                }

                // Short delay between batches
                await Task.Delay(PollingInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[BackgroundSummary] Error in worker loop");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }

        logger.LogInformation("[BackgroundSummary] Worker stopped");
    }

    private async Task PeriodicCleanupAsync()
    {
        if (DateTime.UtcNow - _lastCleanup > CleanupInterval)
        {
            await queue.CleanupOldAsync(daysToKeep: 7);
            _lastCleanup = DateTime.UtcNow;
        }
    }

    private async Task ProcessSummaryRequestAsync(SummaryQueueItem item, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        logger.LogInformation("[BackgroundSummary] Processing summary for chat {ChatId}, {Hours}h, requested by @{User}",
            item.ChatId, item.Hours, item.RequestedBy);

        using var scope = serviceProvider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<MessageStore>();
        var smartSummary = scope.ServiceProvider.GetRequiredService<SmartSummaryService>();

        var nowUtc = DateTimeOffset.UtcNow;
        var startUtc = nowUtc.AddHours(-item.Hours);

        var messages = await store.GetMessagesAsync(item.ChatId, startUtc, nowUtc);

        if (messages.Count == 0)
        {
            await bot.SendMessage(
                chatId: item.ChatId,
                text: $"За последние {item.Hours} часов сообщений не найдено.",
                replyParameters: new ReplyParameters { MessageId = item.ReplyToMessageId },
                cancellationToken: ct);
            return;
        }

        // Generate summary (may take 30-120 seconds)
        var periodText = GetPeriodText(item.Hours);
        var report = await smartSummary.GenerateSmartSummaryAsync(
            item.ChatId, messages, startUtc, nowUtc, periodText, ct);

        // Send result
        try
        {
            await bot.SendMessage(
                chatId: item.ChatId,
                text: report,
                parseMode: ParseMode.Html,
                linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true },
                replyParameters: new ReplyParameters { MessageId = item.ReplyToMessageId },
                cancellationToken: ct);
        }
        catch (Telegram.Bot.Exceptions.ApiRequestException ex) when (ex.Message.Contains("can't parse entities"))
        {
            logger.LogWarning("[BackgroundSummary] HTML parsing failed, sending as plain text");
            var plainText = MyRegex().Replace(report, "");
            await bot.SendMessage(
                chatId: item.ChatId,
                text: plainText,
                linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true },
                replyParameters: new ReplyParameters { MessageId = item.ReplyToMessageId },
                cancellationToken: ct);
        }

        sw.Stop();
        logCollector.IncrementSummaries();

        logger.LogInformation("[BackgroundSummary] Summary sent to chat {ChatId} ({MessageCount} messages) in {Elapsed:F1}s",
            item.ChatId, messages.Count, sw.Elapsed.TotalSeconds);
    }

    private static string GetPeriodText(int hours)
    {
        return hours switch
        {
            24 => "за сутки",
            _ when hours < 24 => $"за {hours} час{GetHourSuffix(hours)}",
            _ => $"за {hours / 24} дн{GetDaySuffix(hours / 24)}"
        };
    }

    private static string GetHourSuffix(int hours)
    {
        if (hours % 100 >= 11 && hours % 100 <= 14) return "ов";
        return (hours % 10) switch
        {
            1 => "",
            2 or 3 or 4 => "а",
            _ => "ов"
        };
    }

    private static string GetDaySuffix(int days)
    {
        if (days % 100 >= 11 && days % 100 <= 14) return "ей";
        return (days % 10) switch
        {
            1 => "ь",
            2 or 3 or 4 => "я",
            _ => "ей"
        };
    }

    [System.Text.RegularExpressions.GeneratedRegex("<[^>]+>")]
    private static partial System.Text.RegularExpressions.Regex MyRegex();
}
