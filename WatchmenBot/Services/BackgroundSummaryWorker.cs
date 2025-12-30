using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace WatchmenBot.Services;

/// <summary>
/// Фоновый воркер для обработки запросов на генерацию summary.
/// Не привязан к nginx timeout — работает сколько нужно.
/// </summary>
public class BackgroundSummaryWorker : BackgroundService
{
    private readonly SummaryQueueService _queue;
    private readonly ITelegramBotClient _bot;
    private readonly IServiceProvider _serviceProvider;
    private readonly LogCollector _logCollector;
    private readonly ILogger<BackgroundSummaryWorker> _logger;

    public BackgroundSummaryWorker(
        SummaryQueueService queue,
        ITelegramBotClient bot,
        IServiceProvider serviceProvider,
        LogCollector logCollector,
        ILogger<BackgroundSummaryWorker> logger)
    {
        _queue = queue;
        _bot = bot;
        _serviceProvider = serviceProvider;
        _logCollector = logCollector;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[BackgroundSummary] Worker started");

        await foreach (var item in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessSummaryRequestAsync(item, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[BackgroundSummary] Failed to process summary for chat {ChatId}", item.ChatId);

                // Отправляем сообщение об ошибке пользователю
                try
                {
                    await _bot.SendMessage(
                        chatId: item.ChatId,
                        text: "Произошла ошибка при генерации выжимки. Попробуйте позже.",
                        replyParameters: new ReplyParameters { MessageId = item.ReplyToMessageId },
                        cancellationToken: stoppingToken);
                }
                catch
                {
                    // Ignore send errors
                }
            }
        }

        _logger.LogInformation("[BackgroundSummary] Worker stopped");
    }

    private async Task ProcessSummaryRequestAsync(SummaryQueueItem item, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        _logger.LogInformation("[BackgroundSummary] Processing summary for chat {ChatId}, {Hours}h, requested by @{User}",
            item.ChatId, item.Hours, item.RequestedBy);

        using var scope = _serviceProvider.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<MessageStore>();
        var smartSummary = scope.ServiceProvider.GetRequiredService<SmartSummaryService>();

        var nowUtc = DateTimeOffset.UtcNow;
        var startUtc = nowUtc.AddHours(-item.Hours);

        var messages = await store.GetMessagesAsync(item.ChatId, startUtc, nowUtc);

        if (messages.Count == 0)
        {
            await _bot.SendMessage(
                chatId: item.ChatId,
                text: $"За последние {item.Hours} часов сообщений не найдено.",
                replyParameters: new ReplyParameters { MessageId = item.ReplyToMessageId },
                cancellationToken: ct);
            return;
        }

        // Генерируем summary (может занять 30-120 секунд)
        var periodText = GetPeriodText(item.Hours);
        var report = await smartSummary.GenerateSmartSummaryAsync(
            item.ChatId, messages, startUtc, nowUtc, periodText, ct);

        // Отправляем результат
        try
        {
            await _bot.SendMessage(
                chatId: item.ChatId,
                text: report,
                parseMode: ParseMode.Html,
                linkPreviewOptions: new Telegram.Bot.Types.LinkPreviewOptions { IsDisabled = true },
                replyParameters: new ReplyParameters { MessageId = item.ReplyToMessageId },
                cancellationToken: ct);
        }
        catch (Telegram.Bot.Exceptions.ApiRequestException ex) when (ex.Message.Contains("can't parse entities"))
        {
            _logger.LogWarning("[BackgroundSummary] HTML parsing failed, sending as plain text");
            var plainText = System.Text.RegularExpressions.Regex.Replace(report, "<[^>]+>", "");
            await _bot.SendMessage(
                chatId: item.ChatId,
                text: plainText,
                linkPreviewOptions: new Telegram.Bot.Types.LinkPreviewOptions { IsDisabled = true },
                replyParameters: new ReplyParameters { MessageId = item.ReplyToMessageId },
                cancellationToken: ct);
        }

        sw.Stop();
        _logCollector.IncrementSummaries();

        _logger.LogInformation("[BackgroundSummary] Summary sent to chat {ChatId} ({MessageCount} messages) in {Elapsed:F1}s",
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
}