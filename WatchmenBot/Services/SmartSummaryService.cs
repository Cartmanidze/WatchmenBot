using System.Text;
using System.Text.Json;
using WatchmenBot.Models;
using WatchmenBot.Services.Llm;

namespace WatchmenBot.Services;

public class SmartSummaryService
{
    // Token budget for context (roughly 4 chars per token)
    private const int ContextTokenBudget = 6000;
    private const int CharsPerToken = 4;
    private const int ContextCharBudget = ContextTokenBudget * CharsPerToken; // ~24000 chars
    private const int MaxMessagesPerTopic = 12; // Reduced from 20
    private const int MaxTotalTopicMessages = 50; // Hard limit across all topics

    private readonly EmbeddingService _embeddingService;
    private readonly ContextEmbeddingService _contextEmbeddingService;
    private readonly LlmRouter _llmRouter;
    private readonly PromptSettingsStore _promptSettings;
    private readonly DebugService _debugService;
    private readonly ILogger<SmartSummaryService> _logger;

    public SmartSummaryService(
        EmbeddingService embeddingService,
        ContextEmbeddingService contextEmbeddingService,
        LlmRouter llmRouter,
        PromptSettingsStore promptSettings,
        DebugService debugService,
        ILogger<SmartSummaryService> logger)
    {
        _embeddingService = embeddingService;
        _contextEmbeddingService = contextEmbeddingService;
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

        // Sanitize HTML for Telegram before returning
        var header = $"📊 <b>Отчёт {periodDescription}</b>\n\n";
        return TelegramHtmlSanitizer.Sanitize(header + summaryContent);
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

        // Step 2: For each topic, find relevant messages using hybrid approach
        var topicMessages = new Dictionary<string, List<MessageWithTime>>();
        var seenTexts = new HashSet<string>(); // Global deduplication across topics

        foreach (var topic in topics)
        {
            // Hybrid search: parallel search in both message and context embeddings
            var messageTask = _embeddingService.SearchSimilarInRangeAsync(
                chatId, topic, startUtc, endUtc, limit: 15, ct);
            var contextTask = _contextEmbeddingService.SearchContextAsync(
                chatId, topic, limit: 5, ct);

            await Task.WhenAll(messageTask, contextTask);

            var messageResults = await messageTask;
            var contextResults = await contextTask;

            // Convert context results to SearchResult format
            var contextAsSearchResults = contextResults.Select(cr => new SearchResult
            {
                ChatId = cr.ChatId,
                MessageId = cr.CenterMessageId,
                ChunkIndex = 0,
                ChunkText = cr.ContextText, // Full window with context
                MetadataJson = null,
                Similarity = cr.Similarity,
                Distance = cr.Distance,
                IsNewsDump = false
            }).ToList();

            // Merge results (prioritize context windows for better coherence)
            var allResults = contextAsSearchResults
                .Concat(messageResults)
                .ToList();

            // Filter, deduplicate, and sort by similarity (most relevant first)
            var filtered = allResults
                .Where(m => m.Similarity > 0.3) // Higher threshold for better quality
                .Where(m => !string.IsNullOrWhiteSpace(m.ChunkText))
                .Where(m =>
                {
                    var key = m.ChunkText.Trim().ToLowerInvariant();
                    if (seenTexts.Contains(key)) return false;
                    seenTexts.Add(key);
                    return true;
                })
                .OrderByDescending(m => m.Similarity) // Prioritize by relevance
                .Take(MaxMessagesPerTopic) // Limit per topic
                .Select(ParseMessageWithTime)
                .OrderBy(m => m.Time) // Then sort chronologically for context
                .ToList();

            _logger.LogDebug("[SmartSummary] Topic '{Topic}': {Count} messages ({Context} context + {Message} individual)",
                topic, filtered.Count, contextResults.Count, messageResults.Count);

            topicMessages[topic] = filtered;
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

        // Build context with token budget
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

        // Build topic context with budget awareness
        contextBuilder.AppendLine("ТОПИКИ И СООБЩЕНИЯ:");
        var usedChars = contextBuilder.Length;
        var totalMessagesIncluded = 0;
        var messagesExcluded = 0;

        foreach (var (topic, messages) in topicMessages)
        {
            if (messages.Count == 0) continue;
            if (totalMessagesIncluded >= MaxTotalTopicMessages) break;

            var topicHeader = $"\n### {topic}\n";
            if (usedChars + topicHeader.Length > ContextCharBudget) break;

            contextBuilder.Append(topicHeader);
            usedChars += topicHeader.Length;

            foreach (var msg in messages)
            {
                if (totalMessagesIncluded >= MaxTotalTopicMessages)
                {
                    messagesExcluded++;
                    continue;
                }

                var timeStr = msg.Time != DateTimeOffset.MinValue
                    ? $"[{msg.Time.ToLocalTime():HH:mm}] "
                    : "";
                var line = $"{timeStr}{msg.Text}\n";

                if (usedChars + line.Length > ContextCharBudget)
                {
                    messagesExcluded++;
                    continue;
                }

                contextBuilder.Append(line);
                usedChars += line.Length;
                totalMessagesIncluded++;
            }
        }

        var context = contextBuilder.ToString();
        debugReport.ContextSent = context;
        debugReport.ContextMessagesCount = totalMessagesIncluded;
        debugReport.ContextTokensEstimate = usedChars / CharsPerToken;

        _logger.LogInformation("[SmartSummary] Context built: {Included} messages, {Chars}/{Budget} chars, {Excluded} excluded by budget",
            totalMessagesIncluded, usedChars, ContextCharBudget, messagesExcluded);

        // STAGE 1: Extract STRUCTURED facts with low temperature (prevents hallucinations)
        var factsSystemPrompt = """
            Ты — точный аналитик чата. Извлеки ФАКТЫ СТРОГО из переписки.

            ВАЖНО: Отвечай ТОЛЬКО JSON, без markdown, без пояснений.
            Если факт не подтверждён переписки — НЕ добавляй его.

            Формат ответа:
            {
              "events": [
                {"what": "описание события", "who": ["участники"], "time": "когда (если известно)"}
              ],
              "discussions": [
                {"topic": "тема", "participants": ["имена"], "summary": "краткое содержание"}
              ],
              "quotes": [
                {"text": "прямая цитата", "author": "имя", "context": "о чём"}
              ],
              "heroes": [
                {"name": "имя", "why": "чем отличился (смешно/глупо/круто)"}
              ]
            }

            Максимум 5 событий, 5 обсуждений, 5 цитат, 3 героя.
            """;

        var stage1Sw = System.Diagnostics.Stopwatch.StartNew();
        var factsResponse = await _llmRouter.CompleteWithFallbackAsync(
            new LlmRequest
            {
                SystemPrompt = factsSystemPrompt,
                UserPrompt = context,
                Temperature = 0.1 // Very low for accuracy
            },
            preferredTag: null, // Use default (cheaper) provider for facts
            ct: ct);
        stage1Sw.Stop();

        debugReport.Stages.Add(new DebugStage
        {
            StageNumber = 1,
            Name = "Facts (JSON)",
            Temperature = 0.1,
            SystemPrompt = factsSystemPrompt,
            UserPrompt = context,
            Response = factsResponse.Content,
            Tokens = factsResponse.TotalTokens,
            TimeMs = stage1Sw.ElapsedMilliseconds
        });

        _logger.LogDebug("[SmartSummary] Stage 1 (structured facts) complete, {Length} chars", factsResponse.Content.Length);

        // STAGE 2: Add humor based ONLY on structured facts
        var settings = await _promptSettings.GetSettingsAsync("summary");

        var humorSystemPrompt = $"""
            {settings.SystemPrompt}

            КРИТИЧЕСКИ ВАЖНО:
            1. Используй ТОЛЬКО факты из JSON ниже
            2. НЕ придумывай новых событий, имён, цитат
            3. Цитаты бери ДОСЛОВНО из поля "quotes"
            4. Героев дня бери из поля "heroes"
            5. Добавляй юмор и мат к СУЩЕСТВУЮЩИМ фактам
            """;

        var humorUserPrompt = $"""
            СТРУКТУРИРОВАННЫЕ ФАКТЫ (JSON):
            {factsResponse.Content}

            СТАТИСТИКА:
            - Сообщений: {stats.TotalMessages}
            - Участников: {stats.UniqueUsers}

            Сгенерируй саммари по формату из system prompt.
            Используй ТОЛЬКО данные из JSON выше!
            """;

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

        // Uniform sampling with reduced sample size for better focus
        var sample = SampleMessagesUniformly(messages, maxMessages: 200);

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

        // Two-stage approach with STRUCTURED JSON for facts (prevents hallucinations)
        var factsSystemPrompt = """
            Ты — точный аналитик чата. Извлеки ФАКТЫ СТРОГО из переписки.

            ВАЖНО: Отвечай ТОЛЬКО JSON, без markdown, без пояснений.
            Если факт не подтверждён перепиской — НЕ добавляй его.

            Формат ответа:
            {
              "events": [
                {"what": "описание события", "who": ["участники"]}
              ],
              "discussions": [
                {"topic": "тема", "participants": ["имена"], "summary": "краткое содержание"}
              ],
              "quotes": [
                {"text": "прямая цитата", "author": "имя"}
              ],
              "heroes": [
                {"name": "имя", "why": "чем отличился"}
              ]
            }

            Максимум 5 событий, 5 обсуждений, 5 цитат, 3 героя.
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
                Temperature = 0.1  // Very low for accuracy
            },
            preferredTag: null,
            ct: ct);
        stage1Sw.Stop();

        debugReport.Stages.Add(new DebugStage
        {
            StageNumber = 1,
            Name = "Facts (JSON)",
            Temperature = 0.1,
            SystemPrompt = factsSystemPrompt,
            UserPrompt = context,
            Response = factsResponse.Content,
            Tokens = factsResponse.TotalTokens,
            TimeMs = stage1Sw.ElapsedMilliseconds
        });

        // Stage 2: Add humor based ONLY on structured JSON facts
        var settings = await _promptSettings.GetSettingsAsync("summary");

        var humorSystemPrompt = $"""
            {settings.SystemPrompt}

            КРИТИЧЕСКИ ВАЖНО:
            1. Используй ТОЛЬКО факты из JSON ниже
            2. НЕ придумывай новых событий, имён, цитат
            3. Цитаты бери ДОСЛОВНО из поля "quotes"
            4. Героев дня бери из поля "heroes"
            5. Добавляй юмор к СУЩЕСТВУЮЩИМ фактам
            """;

        var humorUserPrompt = $"""
            СТРУКТУРИРОВАННЫЕ ФАКТЫ (JSON):
            {factsResponse.Content}

            Статистика: {stats.TotalMessages} сообщений, {stats.UniqueUsers} участников

            Используй ТОЛЬКО данные из JSON!
            """;

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
