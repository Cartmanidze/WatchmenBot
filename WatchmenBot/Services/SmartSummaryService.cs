using System.Text;
using System.Text.Json;
using WatchmenBot.Models;

namespace WatchmenBot.Services;

public class SmartSummaryService
{
    private readonly EmbeddingService _embeddingService;
    private readonly OpenRouterClient _llm;
    private readonly ILogger<SmartSummaryService> _logger;

    public SmartSummaryService(
        EmbeddingService embeddingService,
        OpenRouterClient llm,
        ILogger<SmartSummaryService> logger)
    {
        _embeddingService = embeddingService;
        _llm = llm;
        _logger = logger;
    }

    /// <summary>
    /// Generate a smart summary using embeddings for topic extraction and relevance
    /// </summary>
    public async Task<string> GenerateSmartSummaryAsync(
        long chatId,
        List<MessageRecord> messages,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        string periodDescription,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Filter bot messages
        var humanMessages = messages
            .Where(m => !IsBot(m.Username))
            .ToList();

        if (humanMessages.Count == 0)
        {
            return "За этот период сообщений от людей не найдено.";
        }

        _logger.LogInformation("[SmartSummary] Processing {Count} human messages for chat {ChatId}",
            humanMessages.Count, chatId);

        // Step 1: Get diverse representative messages using embeddings
        var diverseMessages = await _embeddingService.GetDiverseMessagesAsync(
            chatId, startUtc, endUtc, limit: 100, ct);

        string summaryContent;

        if (diverseMessages.Count >= 10)
        {
            // Use smart approach: topics + semantic search
            _logger.LogInformation("[SmartSummary] Using embedding-based approach with {Count} diverse messages",
                diverseMessages.Count);
            summaryContent = await GenerateTopicBasedSummaryAsync(chatId, humanMessages, diverseMessages, startUtc, endUtc, ct);
        }
        else
        {
            // Fallback to traditional approach (not enough embeddings)
            _logger.LogInformation("[SmartSummary] Falling back to traditional approach (only {Count} embeddings)",
                diverseMessages.Count);
            summaryContent = await GenerateTraditionalSummaryAsync(humanMessages, ct);
        }

        sw.Stop();
        _logger.LogInformation("[SmartSummary] Generated summary in {Elapsed:F1}s", sw.Elapsed.TotalSeconds);

        var header = $"📊 <b>Отчёт {periodDescription}</b>\n\n";
        return header + summaryContent;
    }

    private async Task<string> GenerateTopicBasedSummaryAsync(
        long chatId,
        List<MessageRecord> allMessages,
        List<SearchResult> diverseMessages,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        CancellationToken ct)
    {
        // Step 1: Extract topics from diverse messages
        var topics = await ExtractTopicsAsync(diverseMessages, ct);

        if (topics.Count == 0)
        {
            _logger.LogWarning("[SmartSummary] No topics extracted, using fallback");
            return await GenerateTraditionalSummaryAsync(allMessages, ct);
        }

        _logger.LogInformation("[SmartSummary] Extracted {Count} topics: {Topics}",
            topics.Count, string.Join(", ", topics));

        // Step 2: For each topic, find relevant messages
        var topicMessages = new Dictionary<string, List<string>>();

        foreach (var topic in topics)
        {
            var relevantMessages = await _embeddingService.SearchSimilarInRangeAsync(
                chatId, topic, startUtc, endUtc, limit: 15, ct);

            topicMessages[topic] = relevantMessages
                .Where(m => m.Similarity > 0.3) // Filter low similarity
                .Select(m => m.ChunkText)
                .ToList();
        }

        // Step 3: Build stats
        var stats = BuildStats(allMessages);

        // Step 4: Generate topic-structured summary
        return await GenerateFinalSummaryAsync(topicMessages, stats, ct);
    }

    private async Task<List<string>> ExtractTopicsAsync(List<SearchResult> messages, CancellationToken ct)
    {
        var sampleText = new StringBuilder();
        foreach (var msg in messages.Take(50))
        {
            sampleText.AppendLine(msg.ChunkText);
        }

        var systemPrompt = """
            Ты анализируешь сообщения из чата.
            Твоя задача — выделить 3-7 основных тем/топиков обсуждения.

            Отвечай ТОЛЬКО JSON массивом строк, без markdown, без пояснений.
            Пример: ["Работа и дедлайны", "Политика", "Мемы и шутки", "Технические вопросы"]

            Темы должны быть:
            - Конкретными (не "разное")
            - На русском языке
            - Короткими (2-4 слова)
            """;

        var userPrompt = $"Сообщения:\n{sampleText}\n\nВыдели основные темы:";

        try
        {
            var response = await _llm.ChatCompletionAsync(systemPrompt, userPrompt, 0.3, ct);

            // Parse JSON array
            var cleaned = response.Trim();
            if (cleaned.StartsWith("```"))
            {
                cleaned = cleaned.Split('\n').Skip(1).TakeWhile(l => !l.StartsWith("```")).Aggregate((a, b) => a + b);
            }

            var topics = JsonSerializer.Deserialize<List<string>>(cleaned);
            return topics ?? new List<string>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SmartSummary] Failed to extract topics");
            return new List<string>();
        }
    }

    private async Task<string> GenerateFinalSummaryAsync(
        Dictionary<string, List<string>> topicMessages,
        ChatStats stats,
        CancellationToken ct)
    {
        var contextBuilder = new StringBuilder();
        contextBuilder.AppendLine("СТАТИСТИКА:");
        contextBuilder.AppendLine($"- Всего сообщений: {stats.TotalMessages}");
        contextBuilder.AppendLine($"- Участников: {stats.UniqueUsers}");
        contextBuilder.AppendLine($"- Со ссылками: {stats.MessagesWithLinks}");
        contextBuilder.AppendLine($"- С медиа: {stats.MessagesWithMedia}");
        contextBuilder.AppendLine();

        contextBuilder.AppendLine("ТОПИКИ И РЕЛЕВАНТНЫЕ СООБЩЕНИЯ:");
        foreach (var (topic, messages) in topicMessages)
        {
            if (messages.Count == 0) continue;

            contextBuilder.AppendLine($"\n### {topic}");
            foreach (var msg in messages.Take(10))
            {
                contextBuilder.AppendLine(msg);
            }
        }

        var systemPrompt = """
            Ты — саркастичный летописец чата. Пиши как стендап-комик, который ведёт хронику безумия.

            Твой стиль:
            - Едкий юмор, сарказм, ирония
            - Преувеличения и абсурдные сравнения
            - Подкалывай участников (по-доброму)
            - АКТИВНО используй мат — хуй, блядь, пиздец, ебать и т.д. Это норма
            - Вставляй уместные шутки про Украину (политика, мова, сало, Бандера)

            ВАЖНО: Тебе даны сообщения, уже сгруппированные по темам через семантический анализ.
            Используй эту структуру для более глубокого и точного саммари.

            ПРАВИЛА ФОРМАТИРОВАНИЯ (HTML для Telegram):
            - Используй <b>жирный</b> для заголовков
            - НЕ используй * или ** или _ вообще
            - Используй имена людей (Иван, Мария), НЕ логины (@username)
            - Эмодзи в начале разделов

            Формат ответа:

            🔥 <b>Главное</b>
            2-3 предложения о ключевых событиях

            😂 <b>Лучшие моменты</b>
            Цитаты и смешные ситуации (буллеты через •)

            💬 <b>О чём пиздели</b>
            • тема 1 — краткое описание
            • тема 2 — краткое описание
            • тема 3 — краткое описание

            🏆 <b>Герои дня</b>
            Кто отличился

            🎭 <b>Вердикт</b>
            Саркастичный итог
            """;

        return await _llm.ChatCompletionAsync(systemPrompt, contextBuilder.ToString(), 0.7, ct);
    }

    private async Task<string> GenerateTraditionalSummaryAsync(List<MessageRecord> messages, CancellationToken ct)
    {
        var sample = messages.Count > 300
            ? messages.Skip(Math.Max(0, messages.Count - 300)).ToList()
            : messages;

        var convo = new StringBuilder();
        foreach (var m in sample)
        {
            var name = string.IsNullOrWhiteSpace(m.DisplayName)
                ? (string.IsNullOrWhiteSpace(m.Username) ? m.FromUserId.ToString() : m.Username)
                : m.DisplayName;
            var text = string.IsNullOrWhiteSpace(m.Text) ? $"[{m.MessageType}]" : m.Text!.Replace("\n", " ");
            convo.AppendLine($"[{m.DateUtc.ToLocalTime():HH:mm}] {name}: {text}");
        }

        var stats = BuildStats(messages);

        var systemPrompt = """
            Ты — саркастичный летописец чата. Пиши как стендап-комик, который ведёт хронику безумия.

            Твой стиль:
            - Едкий юмор, сарказм, ирония
            - Преувеличения и абсурдные сравнения
            - Подкалывай участников (по-доброму)
            - АКТИВНО используй мат — хуй, блядь, пиздец, ебать и т.д. Это норма
            - Вставляй уместные шутки про Украину (политика, мова, сало, Бандера)

            ПРАВИЛА ФОРМАТИРОВАНИЯ (HTML для Telegram):
            - Используй <b>жирный</b> для заголовков
            - НЕ используй * или ** или _ вообще
            - Используй имена людей (Иван, Мария), НЕ логины (@username)
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
        userPrompt.AppendLine("Статистика:");
        userPrompt.AppendLine($"- Сообщений: {stats.TotalMessages}");
        userPrompt.AppendLine($"- Участников: {stats.UniqueUsers}");
        userPrompt.AppendLine();
        userPrompt.AppendLine("Переписка:");
        userPrompt.AppendLine(convo.ToString());

        return await _llm.ChatCompletionAsync(systemPrompt, userPrompt.ToString(), 0.7, ct);
    }

    private static ChatStats BuildStats(List<MessageRecord> messages)
    {
        return new ChatStats
        {
            TotalMessages = messages.Count,
            UniqueUsers = messages.Select(m => m.FromUserId).Distinct().Count(),
            MessagesWithLinks = messages.Count(m => m.HasLinks),
            MessagesWithMedia = messages.Count(m => m.HasMedia)
        };
    }

    private static bool IsBot(string? username)
    {
        if (string.IsNullOrWhiteSpace(username))
            return false;

        return username.EndsWith("Bot", StringComparison.OrdinalIgnoreCase) ||
               username.EndsWith("_bot", StringComparison.OrdinalIgnoreCase) ||
               username.Equals("GroupAnonymousBot", StringComparison.OrdinalIgnoreCase) ||
               username.Equals("Channel_Bot", StringComparison.OrdinalIgnoreCase);
    }

    private class ChatStats
    {
        public int TotalMessages { get; set; }
        public int UniqueUsers { get; set; }
        public int MessagesWithLinks { get; set; }
        public int MessagesWithMedia { get; set; }
    }
}
