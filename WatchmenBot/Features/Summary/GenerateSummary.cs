using System.Text;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using WatchmenBot.Models;
using WatchmenBot.Services;

namespace WatchmenBot.Features.Summary;

public class GenerateSummaryRequest
{
    public required Message Message { get; init; }
    public int Hours { get; init; } = 24;
}

public class GenerateSummaryResponse
{
    public bool IsSuccess { get; init; }
    public string? ErrorMessage { get; init; }
    public int MessageCount { get; init; }

    public static GenerateSummaryResponse Success(int messageCount) => new()
    {
        IsSuccess = true,
        MessageCount = messageCount
    };

    public static GenerateSummaryResponse Error(string errorMessage) => new()
    {
        IsSuccess = false,
        ErrorMessage = errorMessage
    };
}

public class GenerateSummaryHandler
{
    private readonly ITelegramBotClient _bot;
    private readonly MessageStore _store;
    private readonly OpenRouterClient _llm;
    private readonly ILogger<GenerateSummaryHandler> _logger;

    public GenerateSummaryHandler(
        ITelegramBotClient bot,
        MessageStore store,
        OpenRouterClient llm,
        ILogger<GenerateSummaryHandler> logger)
    {
        _bot = bot;
        _store = store;
        _llm = llm;
        _logger = logger;
    }

    public async Task<GenerateSummaryResponse> HandleAsync(GenerateSummaryRequest request, CancellationToken ct)
    {
        var message = request.Message;
        var chatId = message.Chat.Id;
        var hours = request.Hours;

        try
        {
            // Send "typing" indicator
            await _bot.SendChatActionAsync(chatId, ChatAction.Typing, cancellationToken: ct);

            var nowUtc = DateTimeOffset.UtcNow;
            var startUtc = nowUtc.AddHours(-hours);

            _logger.LogInformation("Generating summary for chat {ChatId}, last {Hours} hours", chatId, hours);

            var messages = await _store.GetMessagesAsync(chatId, startUtc, nowUtc);

            if (messages.Count == 0)
            {
                await _bot.SendTextMessageAsync(
                    chatId: chatId,
                    text: $"За последние {hours} часов сообщений не найдено.",
                    replyToMessageId: message.MessageId,
                    cancellationToken: ct);

                return GenerateSummaryResponse.Success(0);
            }

            // Build and send the report
            var report = await BuildReportAsync(messages, hours, ct);

            // Try HTML first, fallback to plain text if parsing fails
            try
            {
                await _bot.SendTextMessageAsync(
                    chatId: chatId,
                    text: report,
                    parseMode: ParseMode.Html,
                    disableWebPagePreview: true,
                    replyToMessageId: message.MessageId,
                    cancellationToken: ct);
            }
            catch (Telegram.Bot.Exceptions.ApiRequestException ex) when (ex.Message.Contains("can't parse entities"))
            {
                _logger.LogWarning("HTML parsing failed, sending as plain text");
                // Strip HTML tags for plain text
                var plainText = System.Text.RegularExpressions.Regex.Replace(report, "<[^>]+>", "");
                await _bot.SendTextMessageAsync(
                    chatId: chatId,
                    text: plainText,
                    disableWebPagePreview: true,
                    replyToMessageId: message.MessageId,
                    cancellationToken: ct);
            }

            _logger.LogInformation("Sent summary to chat {ChatId} ({MessageCount} messages)", chatId, messages.Count);

            return GenerateSummaryResponse.Success(messages.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate summary for chat {ChatId}", chatId);

            try
            {
                await _bot.SendTextMessageAsync(
                    chatId: chatId,
                    text: "Произошла ошибка при генерации выжимки. Попробуйте позже.",
                    replyToMessageId: message.MessageId,
                    cancellationToken: ct);
            }
            catch
            {
                // Ignore send errors
            }

            return GenerateSummaryResponse.Error(ex.Message);
        }
    }

    private async Task<string> BuildReportAsync(List<MessageRecord> messages, int hours, CancellationToken ct)
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
        userPrompt.AppendLine($"Вот сводные метрики за последние {hours} часов:");
        userPrompt.AppendLine(statsText.ToString());
        userPrompt.AppendLine();
        userPrompt.AppendLine("Фрагменты переписки (последние ~300 сообщений):");
        userPrompt.AppendLine("```");
        userPrompt.AppendLine(convo.ToString());
        userPrompt.AppendLine("```");
        userPrompt.AppendLine();
        userPrompt.AppendLine("Сформируй краткий отчёт по формату выше.");

        var summary = await _llm.ChatCompletionAsync(systemPrompt, userPrompt.ToString(), 0.7, ct);

        var periodText = hours switch
        {
            24 => "за сутки",
            _ when hours < 24 => $"за {hours} час{GetHourSuffix(hours)}",
            _ => $"за {hours / 24} дн{GetDaySuffix(hours / 24)}"
        };

        var header = $"📊 <b>Отчёт {periodText}</b>\n\n";
        return header + summary;
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

    /// <summary>
    /// Parse the /summary command to extract hours parameter
    /// </summary>
    public static int ParseHoursFromCommand(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 24;

        // /summary 48  or /summary 48h or /summary 2d
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return 24;

        var param = parts[1].ToLowerInvariant().Trim();

        // Days: "2d" or "2д"
        if (param.EndsWith("d") || param.EndsWith("д"))
        {
            if (int.TryParse(param.TrimEnd('d', 'д'), out var days) && days > 0 && days <= 30)
                return days * 24;
        }
        // Hours: "48h" or "48ч" or just "48"
        else if (param.EndsWith("h") || param.EndsWith("ч"))
        {
            if (int.TryParse(param.TrimEnd('h', 'ч'), out var h) && h > 0 && h <= 720)
                return h;
        }
        else if (int.TryParse(param, out var hours) && hours > 0 && hours <= 720)
        {
            return hours;
        }

        return 24;
    }
}
