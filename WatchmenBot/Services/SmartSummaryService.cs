using System.Text;
using System.Text.Json;
using WatchmenBot.Models;
using WatchmenBot.Services.Llm;

namespace WatchmenBot.Services;

public class SmartSummaryService
{
    private readonly EmbeddingService _embeddingService;
    private readonly LlmRouter _llmRouter;
    private readonly PromptSettingsStore _promptSettings;
    private readonly DebugService _debugService;
    private readonly ILogger<SmartSummaryService> _logger;

    public SmartSummaryService(
        EmbeddingService embeddingService,
        LlmRouter llmRouter,
        PromptSettingsStore promptSettings,
        DebugService debugService,
        ILogger<SmartSummaryService> logger)
    {
        _embeddingService = embeddingService;
        _llmRouter = llmRouter;
        _promptSettings = promptSettings;
        _debugService = debugService;
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

        // Initialize debug report
        var debugReport = new DebugReport
        {
            Command = "summary",
            ChatId = chatId,
            Query = periodDescription
        };

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

        // Collect debug info for search results
        debugReport.SearchResults = diverseMessages.Select(r => new DebugSearchResult
        {
            Similarity = r.Similarity,
            MessageIds = new[] { r.MessageId },
            Text = r.ChunkText,
            Timestamp = ParseTimestamp(r.MetadataJson)
        }).ToList();

        string summaryContent;

        if (diverseMessages.Count >= 10)
        {
            // Use smart approach: topics + semantic search
            _logger.LogInformation("[SmartSummary] Using embedding-based approach with {Count} diverse messages",
                diverseMessages.Count);
            summaryContent = await GenerateTopicBasedSummaryWithDebugAsync(chatId, humanMessages, diverseMessages, startUtc, endUtc, debugReport, ct);
        }
        else
        {
            // Fallback to traditional approach (not enough embeddings)
            _logger.LogInformation("[SmartSummary] Falling back to traditional approach (only {Count} embeddings)",
                diverseMessages.Count);
            summaryContent = await GenerateTraditionalSummaryWithDebugAsync(humanMessages, debugReport, ct);
        }

        sw.Stop();
        debugReport.LlmTimeMs = sw.ElapsedMilliseconds;

        _logger.LogInformation("[SmartSummary] Generated summary in {Elapsed:F1}s", sw.Elapsed.TotalSeconds);

        // Send debug report to admin
        await _debugService.SendDebugReportAsync(debugReport, ct);

        var header = $"📊 <b>Отчёт {periodDescription}</b>\n\n";
        return header + summaryContent;
    }

    private static DateTimeOffset? ParseTimestamp(string? metadataJson)
    {
        if (string.IsNullOrEmpty(metadataJson))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            if (doc.RootElement.TryGetProperty("DateUtc", out var dateEl))
                return dateEl.GetDateTimeOffset();
        }
        catch { }

        return null;
    }

    private async Task<string> GenerateTopicBasedSummaryWithDebugAsync(
        long chatId,
        List<MessageRecord> allMessages,
        List<SearchResult> diverseMessages,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        DebugReport debugReport,
        CancellationToken ct)
    {
        debugReport.IsMultiStage = true;

        // Step 1: Extract topics from diverse messages
        var topics = await ExtractTopicsAsync(diverseMessages, ct);

        if (topics.Count == 0)
        {
            _logger.LogWarning("[SmartSummary] No topics extracted, using fallback");
            return await GenerateTraditionalSummaryWithDebugAsync(allMessages, debugReport, ct);
        }

        _logger.LogInformation("[SmartSummary] Extracted {Count} topics: {Topics}",
            topics.Count, string.Join(", ", topics));

        // Step 2: For each topic, find relevant messages (increased limit to 25)
        var topicMessages = new Dictionary<string, List<MessageWithTime>>();

        foreach (var topic in topics)
        {
            var relevantMessages = await _embeddingService.SearchSimilarInRangeAsync(
                chatId, topic, startUtc, endUtc, limit: 25, ct);

            topicMessages[topic] = relevantMessages
                .Where(m => m.Similarity > 0.25) // Slightly lower threshold for more context
                .Select(ParseMessageWithTime)
                .OrderBy(m => m.Time) // Sort chronologically
                .ToList();
        }

        // Step 3: Build stats
        var stats = BuildStats(allMessages);

        // Step 4: Generate two-stage summary (facts first, then humor)
        return await GenerateTwoStageSummaryWithDebugAsync(topicMessages, stats, allMessages, debugReport, ct);
    }

    /// <summary>
    /// Parse SearchResult into MessageWithTime, extracting time from metadata
    /// </summary>
    private static MessageWithTime ParseMessageWithTime(SearchResult result)
    {
        DateTimeOffset time = DateTimeOffset.MinValue;

        if (!string.IsNullOrEmpty(result.MetadataJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(result.MetadataJson);
                if (doc.RootElement.TryGetProperty("DateUtc", out var dateEl))
                {
                    time = dateEl.GetDateTimeOffset();
                }
            }
            catch { /* ignore parsing errors */ }
        }

        return new MessageWithTime
        {
            Text = result.ChunkText,
            Time = time,
            Similarity = result.Similarity
        };
    }

    private class MessageWithTime
    {
        public string Text { get; set; } = string.Empty;
        public DateTimeOffset Time { get; set; }
        public double Similarity { get; set; }
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
            // Для извлечения топиков используем дефолтного провайдера (дешёвый)
            var response = await _llmRouter.CompleteAsync(new LlmRequest
            {
                SystemPrompt = systemPrompt,
                UserPrompt = userPrompt,
                Temperature = 0.3
            }, ct);

            // Parse JSON array
            var cleaned = response.Content.Trim();
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

    /// <summary>
    /// Two-stage generation: first extract facts accurately (low temp), then add humor (high temp)
    /// </summary>
    private async Task<string> GenerateTwoStageSummaryWithDebugAsync(
        Dictionary<string, List<MessageWithTime>> topicMessages,
        ChatStats stats,
        List<MessageRecord> allMessages,
        DebugReport debugReport,
        CancellationToken ct)
    {
        debugReport.StageCount = 2;

        // Build context with timestamps
        var contextBuilder = new StringBuilder();
        contextBuilder.AppendLine("СТАТИСТИКА:");
        contextBuilder.AppendLine($"- Всего сообщений: {stats.TotalMessages}");
        contextBuilder.AppendLine($"- Участников: {stats.UniqueUsers}");
        contextBuilder.AppendLine($"- Со ссылками: {stats.MessagesWithLinks}");
        contextBuilder.AppendLine($"- С медиа: {stats.MessagesWithMedia}");
        contextBuilder.AppendLine();

        // Add top active users
        var topUsers = allMessages
            .GroupBy(m => string.IsNullOrWhiteSpace(m.DisplayName) ? m.Username ?? m.FromUserId.ToString() : m.DisplayName)
            .OrderByDescending(g => g.Count())
            .Take(5)
            .Select(g => $"{g.Key}: {g.Count()} сообщений")
            .ToList();

        if (topUsers.Count > 0)
        {
            contextBuilder.AppendLine("САМЫЕ АКТИВНЫЕ:");
            foreach (var user in topUsers)
                contextBuilder.AppendLine($"• {user}");
            contextBuilder.AppendLine();
        }

        contextBuilder.AppendLine("ТОПИКИ И СООБЩЕНИЯ (хронологически):");
        foreach (var (topic, messages) in topicMessages)
        {
            if (messages.Count == 0) continue;

            contextBuilder.AppendLine($"\n### {topic}");
            foreach (var msg in messages.Take(20)) // Increased from 10 to 20
            {
                var timeStr = msg.Time != DateTimeOffset.MinValue
                    ? $"[{msg.Time.ToLocalTime():HH:mm}] "
                    : "";
                contextBuilder.AppendLine($"{timeStr}{msg.Text}");
            }
        }

        var context = contextBuilder.ToString();
        debugReport.ContextSent = context;
        debugReport.ContextMessagesCount = topicMessages.Values.Sum(m => m.Count);
        debugReport.ContextTokensEstimate = context.Length / 4;

        // STAGE 1: Extract facts with low temperature
        var factsSystemPrompt = """
            Ты — точный аналитик чата. Извлеки ФАКТЫ из переписки.

            ПРАВИЛА:
            - Перечисли ТОЛЬКО то, что реально обсуждалось
            - Укажи КТО именно что сказал/сделал (имена!)
            - Не выдумывай, не додумывай
            - Кратко, по пунктам
            - Отметь яркие цитаты (дословно)

            Формат:
            СОБЫТИЯ:
            • [событие 1]
            • [событие 2]

            ОБСУЖДЕНИЯ:
            • [тема]: кто что говорил

            ЦИТАТЫ:
            • "[цитата]" — Имя
            """;

        var stage1Sw = System.Diagnostics.Stopwatch.StartNew();
        var factsResponse = await _llmRouter.CompleteWithFallbackAsync(
            new LlmRequest
            {
                SystemPrompt = factsSystemPrompt,
                UserPrompt = context,
                Temperature = 0.3 // Low temp for accuracy
            },
            preferredTag: null, // Use default (cheaper) provider for facts
            ct: ct);
        stage1Sw.Stop();

        debugReport.Stages.Add(new DebugStage
        {
            StageNumber = 1,
            Name = "Facts",
            Temperature = 0.3,
            SystemPrompt = factsSystemPrompt,
            UserPrompt = context,
            Response = factsResponse.Content,
            Tokens = factsResponse.TotalTokens,
            TimeMs = stage1Sw.ElapsedMilliseconds
        });

        _logger.LogDebug("[SmartSummary] Stage 1 (facts) complete, {Length} chars", factsResponse.Content.Length);

        // STAGE 2: Add humor and format with higher temperature
        var settings = await _promptSettings.GetSettingsAsync("summary");

        var humorSystemPrompt = $"""
            {settings.SystemPrompt}

            ВАЖНО: Ниже — точные факты из чата. Твоя задача:
            1. НЕ менять факты, имена, события
            2. Добавить юмор и сарказм к подаче
            3. Структурировать по формату
            4. Подколоть участников (но факты оставить верными!)
            """;

        var humorUserPrompt = $"ФАКТЫ ИЗ ЧАТА:\n{factsResponse.Content}\n\nСТАТИСТИКА:\n- Сообщений: {stats.TotalMessages}\n- Участников: {stats.UniqueUsers}";

        var stage2Sw = System.Diagnostics.Stopwatch.StartNew();
        var finalResponse = await _llmRouter.CompleteWithFallbackAsync(
            new LlmRequest
            {
                SystemPrompt = humorSystemPrompt,
                UserPrompt = humorUserPrompt,
                Temperature = 0.6 // Slightly lower than before (was 0.7)
            },
            preferredTag: settings.LlmTag,
            ct: ct);
        stage2Sw.Stop();

        debugReport.Stages.Add(new DebugStage
        {
            StageNumber = 2,
            Name = "Humor",
            Temperature = 0.6,
            SystemPrompt = humorSystemPrompt,
            UserPrompt = humorUserPrompt,
            Response = finalResponse.Content,
            Tokens = finalResponse.TotalTokens,
            TimeMs = stage2Sw.ElapsedMilliseconds
        });

        // Set final debug info
        debugReport.SystemPrompt = humorSystemPrompt;
        debugReport.UserPrompt = humorUserPrompt;
        debugReport.LlmProvider = finalResponse.Provider;
        debugReport.LlmModel = finalResponse.Model;
        debugReport.LlmTag = settings.LlmTag;
        debugReport.Temperature = 0.6;
        debugReport.LlmResponse = finalResponse.Content;
        debugReport.PromptTokens = factsResponse.PromptTokens + finalResponse.PromptTokens;
        debugReport.CompletionTokens = factsResponse.CompletionTokens + finalResponse.CompletionTokens;
        debugReport.TotalTokens = factsResponse.TotalTokens + finalResponse.TotalTokens;

        _logger.LogDebug("[SmartSummary] Stage 2 (humor) complete. Provider: {Provider}", finalResponse.Provider);

        return finalResponse.Content;
    }

    private async Task<string> GenerateTraditionalSummaryWithDebugAsync(List<MessageRecord> messages, DebugReport debugReport, CancellationToken ct)
    {
        debugReport.IsMultiStage = true;
        debugReport.StageCount = 2;

        // Uniform sampling across the entire period instead of just taking last N
        var sample = SampleMessagesUniformly(messages, maxMessages: 400);

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

        // Add top active users
        var topUsers = messages
            .GroupBy(m => string.IsNullOrWhiteSpace(m.DisplayName) ? m.Username ?? m.FromUserId.ToString() : m.DisplayName)
            .OrderByDescending(g => g.Count())
            .Take(5)
            .Select(g => $"{g.Key}: {g.Count()}")
            .ToList();

        // Two-stage approach for traditional method too
        var factsSystemPrompt = """
            Ты — точный аналитик чата. Извлеки ФАКТЫ из переписки.

            ПРАВИЛА:
            - Перечисли ТОЛЬКО то, что реально обсуждалось
            - Укажи КТО именно что сказал/сделал (имена!)
            - Не выдумывай, не додумывай
            - Кратко, по пунктам
            - Отметь яркие цитаты (дословно)

            Формат:
            СОБЫТИЯ: • [список]
            ОБСУЖДЕНИЯ: • [тема]: кто что говорил
            ЦИТАТЫ: • "[цитата]" — Имя
            """;

        var contextPrompt = new StringBuilder();
        contextPrompt.AppendLine($"Статистика: {stats.TotalMessages} сообщений, {stats.UniqueUsers} участников");
        contextPrompt.AppendLine($"Активные: {string.Join(", ", topUsers)}");
        contextPrompt.AppendLine();
        contextPrompt.AppendLine("Переписка:");
        contextPrompt.AppendLine(convo.ToString());

        var context = contextPrompt.ToString();
        debugReport.ContextSent = context;
        debugReport.ContextMessagesCount = sample.Count;
        debugReport.ContextTokensEstimate = context.Length / 4;

        var stage1Sw = System.Diagnostics.Stopwatch.StartNew();
        var factsResponse = await _llmRouter.CompleteWithFallbackAsync(
            new LlmRequest
            {
                SystemPrompt = factsSystemPrompt,
                UserPrompt = context,
                Temperature = 0.3
            },
            preferredTag: null,
            ct: ct);
        stage1Sw.Stop();

        debugReport.Stages.Add(new DebugStage
        {
            StageNumber = 1,
            Name = "Facts",
            Temperature = 0.3,
            SystemPrompt = factsSystemPrompt,
            UserPrompt = context,
            Response = factsResponse.Content,
            Tokens = factsResponse.TotalTokens,
            TimeMs = stage1Sw.ElapsedMilliseconds
        });

        // Stage 2: Add humor
        var settings = await _promptSettings.GetSettingsAsync("summary");

        var humorSystemPrompt = $"""
            {settings.SystemPrompt}

            ВАЖНО: Ниже — точные факты из чата. НЕ меняй факты и имена, только добавь юмор!
            """;

        var humorUserPrompt = $"ФАКТЫ:\n{factsResponse.Content}\n\nСтатистика: {stats.TotalMessages} сообщений, {stats.UniqueUsers} участников";

        var stage2Sw = System.Diagnostics.Stopwatch.StartNew();
        var response = await _llmRouter.CompleteWithFallbackAsync(
            new LlmRequest
            {
                SystemPrompt = humorSystemPrompt,
                UserPrompt = humorUserPrompt,
                Temperature = 0.6
            },
            preferredTag: settings.LlmTag,
            ct: ct);
        stage2Sw.Stop();

        debugReport.Stages.Add(new DebugStage
        {
            StageNumber = 2,
            Name = "Humor",
            Temperature = 0.6,
            SystemPrompt = humorSystemPrompt,
            UserPrompt = humorUserPrompt,
            Response = response.Content,
            Tokens = response.TotalTokens,
            TimeMs = stage2Sw.ElapsedMilliseconds
        });

        // Set final debug info
        debugReport.SystemPrompt = humorSystemPrompt;
        debugReport.UserPrompt = humorUserPrompt;
        debugReport.LlmProvider = response.Provider;
        debugReport.LlmModel = response.Model;
        debugReport.LlmTag = settings.LlmTag;
        debugReport.Temperature = 0.6;
        debugReport.LlmResponse = response.Content;
        debugReport.PromptTokens = factsResponse.PromptTokens + response.PromptTokens;
        debugReport.CompletionTokens = factsResponse.CompletionTokens + response.CompletionTokens;
        debugReport.TotalTokens = factsResponse.TotalTokens + response.TotalTokens;

        return response.Content;
    }

    /// <summary>
    /// Sample messages uniformly across time period to capture beginning, middle and end
    /// </summary>
    private static List<MessageRecord> SampleMessagesUniformly(List<MessageRecord> messages, int maxMessages)
    {
        if (messages.Count <= maxMessages)
            return messages;

        var result = new List<MessageRecord>();
        var step = (double)messages.Count / maxMessages;

        for (var i = 0; i < maxMessages; i++)
        {
            var index = (int)(i * step);
            if (index < messages.Count)
                result.Add(messages[index]);
        }

        return result;
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
