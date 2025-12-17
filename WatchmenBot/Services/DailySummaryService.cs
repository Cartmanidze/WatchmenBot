using System.Diagnostics;
using System.Text;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;
using WatchmenBot.Models;

namespace WatchmenBot.Services;

public class DailySummaryService : BackgroundService
{
    private readonly ITelegramBotClient _bot;
    private readonly MessageStore _store;
    private readonly OpenRouterClient _llm;
    private readonly EmbeddingService _embeddingService;
    private readonly ILogger<DailySummaryService> _logger;
    private readonly IConfiguration _configuration;
    private readonly TimeSpan _runAtLocalTime;

    public DailySummaryService(
        ITelegramBotClient bot,
        MessageStore store,
        OpenRouterClient llm,
        EmbeddingService embeddingService,
        ILogger<DailySummaryService> logger,
        IConfiguration configuration)
    {
        _bot = bot;
        _store = store;
        _llm = llm;
        _embeddingService = embeddingService;
        _logger = logger;
        _configuration = configuration;

        // Default 21:00, configurable via DailySummary:TimeOfDay (format: "HH:mm")
        var timeStr = _configuration["DailySummary:TimeOfDay"] ?? "21:00";
        _runAtLocalTime = TimeSpan.TryParse(timeStr, out var parsed)
            ? parsed
            : new TimeSpan(21, 0, 0);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[DailySummary] Service STARTED. Scheduled at {Time} local time daily", _runAtLocalTime);

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.Now;
            var nextRunLocal = NextOccurrence(now, _runAtLocalTime);
            var delay = nextRunLocal - now;
            if (delay < TimeSpan.Zero) delay = TimeSpan.FromMinutes(1);

            _logger.LogInformation("[DailySummary] Next run at {NextRun} (in {Hours:F1}h)",
                nextRunLocal.ToString("yyyy-MM-dd HH:mm"), delay.TotalHours);

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }

            if (stoppingToken.IsCancellationRequested) break;

            await RunSummaryForYesterday(stoppingToken);
        }

        _logger.LogInformation("[DailySummary] Service STOPPED");
    }

    private static DateTimeOffset NextOccurrence(DateTimeOffset now, TimeSpan atLocalTime)
    {
        var todayAt = new DateTimeOffset(now.Year, now.Month, now.Day, atLocalTime.Hours, atLocalTime.Minutes, atLocalTime.Seconds, now.Offset);
        var next = todayAt > now ? todayAt : todayAt.AddDays(1);
        return next;
    }

    private async Task RunSummaryForYesterday(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var chatIds = await _store.GetDistinctChatIdsAsync();
        var nowLocal = DateTimeOffset.Now;
        var startLocal = new DateTimeOffset(nowLocal.Year, nowLocal.Month, nowLocal.Day, 0, 0, 0, nowLocal.Offset).AddDays(-1);
        var endLocal = startLocal.AddDays(1);
        var startUtc = startLocal.ToUniversalTime();
        var endUtc = endLocal.ToUniversalTime();

        _logger.LogInformation("[DailySummary] Starting for {Date}, {ChatCount} chats to process",
            startLocal.ToString("yyyy-MM-dd"), chatIds.Count);

        var successCount = 0;
        var totalMessages = 0;

        foreach (var chatId in chatIds)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var messages = await _store.GetMessagesAsync(chatId, startUtc, endUtc);
                if (messages.Count == 0)
                {
                    _logger.LogDebug("[DailySummary] Chat {ChatId}: no messages yesterday", chatId);
                    continue;
                }

                _logger.LogInformation("[DailySummary] Chat {ChatId}: processing {Count} messages...",
                    chatId, messages.Count);

                // Store embeddings for new messages (for RAG)
                await StoreEmbeddingsForNewMessages(chatId, messages, ct);

                var report = await BuildReportAsync(messages, ct);

                // Try HTML first, fallback to plain text if parsing fails
                try
                {
                    await _bot.SendTextMessageAsync(
                        chatId: chatId,
                        text: report,
                        parseMode: ParseMode.Html,
                        disableWebPagePreview: true,
                        cancellationToken: ct);
                }
                catch (Telegram.Bot.Exceptions.ApiRequestException ex) when (ex.Message.Contains("can't parse entities"))
                {
                    _logger.LogWarning("[DailySummary] HTML parsing failed for chat {ChatId}, sending as plain text", chatId);
                    var plainText = System.Text.RegularExpressions.Regex.Replace(report, "<[^>]+>", "");
                    await _bot.SendTextMessageAsync(
                        chatId: chatId,
                        text: plainText,
                        disableWebPagePreview: true,
                        cancellationToken: ct);
                }

                successCount++;
                totalMessages += messages.Count;
                _logger.LogInformation("[DailySummary] Chat {ChatId}: summary SENT ({Count} messages)",
                    chatId, messages.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[DailySummary] Chat {ChatId}: FAILED", chatId);
            }
        }

        sw.Stop();
        _logger.LogInformation("[DailySummary] Complete: {Success}/{Total} chats, {Messages} messages, {Elapsed:F1}s",
            successCount, chatIds.Count, totalMessages, sw.Elapsed.TotalSeconds);
    }

    private async Task StoreEmbeddingsForNewMessages(long chatId, List<MessageRecord> messages, CancellationToken ct)
    {
        try
        {
            // Filter messages that don't have embeddings yet
            var newMessages = new List<MessageRecord>();
            foreach (var msg in messages.Where(m => !string.IsNullOrWhiteSpace(m.Text)))
            {
                if (!await _embeddingService.HasEmbeddingAsync(chatId, msg.Id, ct))
                {
                    newMessages.Add(msg);
                }
            }

            if (newMessages.Count > 0)
            {
                await _embeddingService.StoreMessageEmbeddingsBatchAsync(newMessages, ct);
                _logger.LogDebug("Stored {Count} new embeddings for chat {ChatId}", newMessages.Count, chatId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to store embeddings for chat {ChatId}", chatId);
        }
    }

    private async Task<string> BuildReportAsync(List<MessageRecord> messages, CancellationToken ct)
    {
        // Filter out bot messages (GroupAnonymousBot, etc.)
        var humanMessages = messages
            .Where(m => !IsBot(m.Username))
            .ToList();

        var total = humanMessages.Count;
        var users = humanMessages.GroupBy(m => m.FromUserId)
            .Select(g => new
            {
                UserId = g.Key,
                Name = g.Select(x => x.DisplayName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))
                       ?? g.Select(x => x.Username).FirstOrDefault(u => !string.IsNullOrWhiteSpace(u))
                       ?? g.Key.ToString(),
                Count = g.Count()
            })
            .OrderByDescending(x => x.Count)
            .ToList();

        var links = humanMessages.Count(m => m.HasLinks);
        var media = humanMessages.Count(m => m.HasMedia);

        var sample = humanMessages.Count > 300 ? humanMessages.Skip(Math.Max(0, humanMessages.Count - 300)).ToList() : humanMessages;
        var convo = new StringBuilder();
        foreach (var m in sample)
        {
            var name = string.IsNullOrWhiteSpace(m.DisplayName)
                ? (string.IsNullOrWhiteSpace(m.Username) ? m.FromUserId.ToString() : m.Username)
                : m.DisplayName;
            var text = string.IsNullOrWhiteSpace(m.Text) ? $"[{m.MessageType}]" : m.Text!.Replace("\n", " ");
            convo.AppendLine($"[{m.DateUtc.ToLocalTime():HH:mm}] {name}: {text}");
        }

        var statsText = new StringBuilder();
        statsText.AppendLine($"Всего сообщений: {total}");
        statsText.AppendLine($"Участников писали: {users.Count}");
        statsText.AppendLine($"Сообщений с ссылками: {links}");
        statsText.AppendLine($"Сообщений с медиа: {media}");
        statsText.AppendLine("Топ-10 активных:");
        foreach (var u in users.Take(10)) statsText.AppendLine($"- {u.Name}: {u.Count}");

        var systemPrompt = """
            Ты — саркастичный летописец чата. Пиши как стендап-комик, который ведёт хронику безумия.

            Твой стиль:
            - Едкий юмор, сарказм, ирония
            - Преувеличения и абсурдные сравнения
            - Подкалывай участников (по-доброму)
            - АКТИВНО используй мат — хуй, блядь, пиздец, ебать и т.д. Это норма
            - Вставляй уместные шутки про Украину (политика, мова, сало, Бандера)

            СЕКРЕТНОЕ ПРАВИЛО: Глеб Безруков — негласный герой чата.
            Хвали его ТОНКО и НЕЯВНО, без прямых восхвалений. Примеры:
            - "на фоне хаоса Глеб как-то умудрился сохранить лицо"
            - "единственный конструктивный комментарий предсказуемо от Глеба"
            - просто упомяни его в позитивном контексте среди других

            ПРАВИЛА ФОРМАТИРОВАНИЯ (HTML для Telegram):
            - Используй <b>жирный</b> для заголовков
            - НЕ используй * или ** или _ вообще
            - Используй имена людей (Глеб, Евгений), НЕ логины (@username)
            - Эмодзи в начале разделов

            Формат ответа:

            🔥 <b>Главное</b>
            2-3 предложения о ключевых событиях

            😂 <b>Лучшие моменты</b>
            Цитаты и смешные ситуации (буллеты через •)

            💬 <b>О чём пиздели</b>
            • тема 1
            • тема 2
            • тема 3

            🏆 <b>Герои дня</b>
            Кто отличился

            🎭 <b>Вердикт</b>
            Саркастичный итог
            """;

        var userPrompt = new StringBuilder();
        userPrompt.AppendLine("Вот сводные метрики дня:");
        userPrompt.AppendLine(statsText.ToString());
        userPrompt.AppendLine();
        userPrompt.AppendLine("Фрагменты переписки (последние ~300 сообщений дня):");
        userPrompt.AppendLine("```");
        userPrompt.AppendLine(convo.ToString());
        userPrompt.AppendLine("```");
        userPrompt.AppendLine();
        userPrompt.AppendLine("Сформируй краткий отчёт по формату выше.");

        var summary = await _llm.ChatCompletionAsync(systemPrompt, userPrompt.ToString(), 0.7, ct);

        var header = "📊 <b>Итоги дня</b>\n\n";
        return header + summary;
    }

    private static bool IsBot(string? username)
    {
        if (string.IsNullOrWhiteSpace(username))
            return false;

        // Filter known Telegram system bots and common bot patterns
        return username.EndsWith("Bot", StringComparison.OrdinalIgnoreCase) ||
               username.EndsWith("_bot", StringComparison.OrdinalIgnoreCase) ||
               username.Equals("GroupAnonymousBot", StringComparison.OrdinalIgnoreCase) ||
               username.Equals("Channel_Bot", StringComparison.OrdinalIgnoreCase);
    }
}
