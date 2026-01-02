using WatchmenBot.Features.Search.Models;
using WatchmenBot.Features.Search.Services;
using WatchmenBot.Features.Summary.Models;
using WatchmenBot.Models;
using WatchmenBot.Features.Admin.Services;
using WatchmenBot.Features.Webhook.Services;

namespace WatchmenBot.Features.Summary.Services;

/// <summary>
/// Orchestrates smart summary generation using topic extraction and two-stage LLM generation.
/// Delegates context building, topic extraction, and stage execution to specialized services.
/// Enhanced with thread detection, event extraction, and quote mining.
/// </summary>
public class SmartSummaryService(
    EmbeddingService embeddingService,
    ContextEmbeddingService contextEmbeddingService,
    TopicExtractor topicExtractor,
    SummaryContextBuilder contextBuilder,
    SummaryStageExecutor stageExecutor,
    ThreadDetector threadDetector,
    EventDetector eventDetector,
    QuoteMiner quoteMiner,
    DebugService debugService,
    ILogger<SmartSummaryService> logger)
{
    private const int MaxMessagesPerTopic = 12;

    /// <summary>
    /// Minimum messages to use enhanced summary (with thread detection, events, quotes)
    /// </summary>
    private const int EnhancedSummaryThreshold = 50;

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

        logger.LogInformation("[SmartSummary] Processing {Count} human messages for chat {ChatId}",
            humanMessages.Count, chatId);

        // Get diverse representative messages using embeddings
        var diverseMessages = await embeddingService.GetDiverseMessagesAsync(
            chatId, startUtc, endUtc, limit: 100, ct);

        // Collect debug info
        debugReport.SearchResults = diverseMessages.Select(r => new DebugSearchResult
        {
            Similarity = r.Similarity,
            MessageIds = [r.MessageId],
            Text = r.ChunkText,
            Timestamp = MetadataParser.TryParseTimestamp(r.MetadataJson)
        }).ToList();

        string summaryContent;

        // Choose generation strategy based on message count and embedding availability
        if (humanMessages.Count >= EnhancedSummaryThreshold && diverseMessages.Count >= 10)
        {
            logger.LogInformation("[SmartSummary] Using ENHANCED approach with {Count} messages, {Diverse} diverse",
                humanMessages.Count, diverseMessages.Count);
            summaryContent = await GenerateEnhancedSummaryAsync(chatId, humanMessages, diverseMessages, startUtc, endUtc, debugReport, ct);
        }
        else if (diverseMessages.Count >= 10)
        {
            logger.LogInformation("[SmartSummary] Using embedding-based approach with {Count} diverse messages",
                diverseMessages.Count);
            summaryContent = await GenerateTopicBasedSummaryAsync(chatId, humanMessages, diverseMessages, startUtc, endUtc, debugReport, ct);
        }
        else
        {
            logger.LogInformation("[SmartSummary] Falling back to traditional approach (only {Count} embeddings)",
                diverseMessages.Count);
            summaryContent = await GenerateTraditionalSummaryAsync(humanMessages, debugReport, ct);
        }

        sw.Stop();
        debugReport.LlmTimeMs = sw.ElapsedMilliseconds;

        logger.LogInformation("[SmartSummary] Generated summary in {Elapsed:F1}s", sw.Elapsed.TotalSeconds);

        await debugService.SendDebugReportAsync(debugReport, ct);

        var header = $"📊 <b>Отчёт {periodDescription}</b>\n\n";
        return TelegramHtmlSanitizer.Sanitize(header + summaryContent);
    }

    private async Task<string> GenerateTopicBasedSummaryAsync(
        long chatId,
        List<MessageRecord> allMessages,
        List<SearchResult> diverseMessages,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        DebugReport debugReport,
        CancellationToken ct)
    {
        debugReport.IsMultiStage = true;

        // Step 1: Extract topics
        var topics = await topicExtractor.ExtractTopicsAsync(diverseMessages, ct: ct);

        if (topics.Count == 0)
        {
            logger.LogWarning("[SmartSummary] No topics extracted, using fallback");
            return await GenerateTraditionalSummaryAsync(allMessages, debugReport, ct);
        }

        // Step 2: For each topic, find relevant messages
        var topicMessages = await GatherTopicMessagesAsync(chatId, topics, startUtc, endUtc, ct);

        // Step 3: Build stats
        var stats = contextBuilder.BuildStats(allMessages);
        var topUsers = contextBuilder.GetTopActiveUsers(allMessages);

        // Step 4: Build context and execute two-stage generation
        var (context, messagesIncluded, tokensEstimate) = contextBuilder.BuildTopicContext(topicMessages, stats, topUsers);

        debugReport.ContextSent = context;
        debugReport.ContextMessagesCount = messagesIncluded;
        debugReport.ContextTokensEstimate = tokensEstimate;

        var result = await stageExecutor.ExecuteTwoStageAsync(context, stats, debugReport, ct);

        return result.FinalContent;
    }

    /// <summary>
    /// Generate enhanced summary with thread detection, event extraction, and quote mining
    /// </summary>
    private async Task<string> GenerateEnhancedSummaryAsync(
        long chatId,
        List<MessageRecord> allMessages,
        List<SearchResult> diverseMessages,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        DebugReport debugReport,
        CancellationToken ct)
    {
        debugReport.IsMultiStage = true;
        debugReport.StageCount = 3; // Topic extraction + Events/Quotes + Final

        // Step 1: Thread detection (no LLM, fast)
        var timezoneOffset = TimeSpan.FromHours(3); // MSK
        var segments = threadDetector.SegmentByActivity(allMessages, timezoneOffset);
        var threads = threadDetector.DetectReplyChains(allMessages);
        var timeline = threadDetector.BuildTimeline(segments, threads);
        var hotMoments = threadDetector.DetectHotMoments(allMessages);

        logger.LogDebug("[SmartSummary] Thread detection: {Segments} segments, {Threads} threads, {Hot} hot moments",
            segments.Count, threads.Count, hotMoments.Count);

        // Step 2: Extract topics (for gathering relevant messages)
        var topicsTask = topicExtractor.ExtractTopicsAsync(diverseMessages, ct: ct);

        // Step 3: Build context for event/quote extraction (in parallel with topics)
        var stats = contextBuilder.BuildStats(allMessages);
        var participants = contextBuilder.BuildParticipantActivity(allMessages, segments);

        // Await topics
        var topics = await topicsTask;

        if (topics.Count == 0)
        {
            logger.LogWarning("[SmartSummary] No topics extracted in enhanced mode, using fallback");
            return await GenerateTraditionalSummaryAsync(allMessages, debugReport, ct);
        }

        // Step 4: Gather topic messages
        var topicMessages = await GatherTopicMessagesAsync(chatId, topics, startUtc, endUtc, ct);

        // Step 5: Build context for LLM extraction
        var (context, messagesIncluded, tokensEstimate) = contextBuilder.BuildEnhancedContext(
            timeline, topicMessages, stats, participants);

        debugReport.ContextSent = context;
        debugReport.ContextMessagesCount = messagesIncluded;
        debugReport.ContextTokensEstimate = tokensEstimate;

        // Step 6: Run EventDetector and QuoteMiner in PARALLEL
        var eventsTask = eventDetector.ExtractEventsAsync(context, ct);
        var quotesTask = quoteMiner.MineQuotesAsync(context, ct);

        await Task.WhenAll(eventsTask, quotesTask);

        var extractedEvents = await eventsTask;
        var minedQuotes = await quotesTask;

        logger.LogDebug("[SmartSummary] Extracted: {Events} events, {Decisions} decisions, {Quotes} quotes",
            extractedEvents.Events.Count, extractedEvents.Decisions.Count, minedQuotes.BestQuotes.Count);

        // Step 7: Build enhanced facts from all sources
        var enhancedFacts = BuildEnhancedFacts(
            timeline, segments, extractedEvents, minedQuotes, hotMoments);

        // Step 8: Execute enhanced two-stage generation
        var result = await stageExecutor.ExecuteEnhancedTwoStageAsync(context, stats, enhancedFacts, debugReport, ct);

        return result.FinalContent;
    }

    /// <summary>
    /// Build enhanced facts from thread detection, events, and quotes
    /// </summary>
    private static EnhancedExtractedFacts BuildEnhancedFacts(
        List<TimelineEntry> timeline,
        List<TimeSegment> segments,
        ExtractedEvents events,
        MinedQuotes quotes,
        List<HotMoment> hotMoments)
    {
        var facts = new EnhancedExtractedFacts();

        // Timeline from thread detector
        foreach (var entry in timeline.Take(5))
        {
            var segment = segments.FirstOrDefault(s => s.Period == entry.TimeRange);
            facts.Timeline.Add(new TimelineFact
            {
                Period = entry.TimeRange,
                Label = entry.Title,
                Topics = segment?.DetectedTopics ?? [],
                MessageCount = entry.MessageCount
            });
        }

        // Events from EventDetector
        facts.Events.AddRange(events.Events.Take(5));
        facts.Decisions.AddRange(events.Decisions.Take(5));
        facts.OpenQuestions.AddRange(events.OpenQuestions.Take(3));

        // Quotes from QuoteMiner
        facts.Quotes.AddRange(quotes.BestQuotes.Take(3));

        // Hot moments: merge from thread detector (message bursts) and quote miner (LLM-detected)
        var allHotMoments = hotMoments.Concat(quotes.HotMoments)
            .GroupBy(h => h.Time ?? h.Description)
            .Select(g => g.First())
            .Take(3)
            .ToList();
        facts.HotMoments.AddRange(allHotMoments);

        return facts;
    }

    private async Task<Dictionary<string, List<MessageWithTime>>> GatherTopicMessagesAsync(
        long chatId,
        List<string> topics,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        CancellationToken ct)
    {
        var topicMessages = new Dictionary<string, List<MessageWithTime>>();
        var seenTexts = new HashSet<string>();

        foreach (var topic in topics)
        {
            // Hybrid search: parallel search in both message and context embeddings
            // IMPORTANT: Both searches MUST filter by time range to prevent old messages leaking in
            var messageTask = embeddingService.SearchSimilarInRangeAsync(
                chatId, topic, startUtc, endUtc, limit: 15, ct);
            var contextTask = contextEmbeddingService.SearchContextInRangeAsync(
                chatId, topic, startUtc, endUtc, limit: 5, ct);

            await Task.WhenAll(messageTask, contextTask);

            var messageResults = await messageTask;
            var contextResults = await contextTask;

            // Convert context results to SearchResult format
            var contextAsSearchResults = contextResults.Select(cr => new SearchResult
            {
                ChatId = cr.ChatId,
                MessageId = cr.CenterMessageId,
                ChunkIndex = 0,
                ChunkText = cr.ContextText,
                MetadataJson = null,
                Similarity = cr.Similarity,
                Distance = cr.Distance,
                IsNewsDump = false
            }).ToList();

            // Merge and filter
            var allResults = contextAsSearchResults.Concat(messageResults).ToList();

            var filtered = allResults
                .Where(m => m.Similarity > 0.3)
                .Where(m => !string.IsNullOrWhiteSpace(m.ChunkText))
                .Where(m =>
                {
                    var key = m.ChunkText.Trim().ToLowerInvariant();
                    return seenTexts.Add(key);
                })
                .OrderByDescending(m => m.Similarity)
                .Take(MaxMessagesPerTopic)
                .Select(ParseMessageWithTime)
                .OrderBy(m => m.Time)
                .ToList();

            logger.LogDebug("[SmartSummary] Topic '{Topic}': {Count} messages ({Context} context + {Message} individual)",
                topic, filtered.Count, contextResults.Count, messageResults.Count);

            topicMessages[topic] = filtered;
        }

        return topicMessages;
    }

    private async Task<string> GenerateTraditionalSummaryAsync(
        List<MessageRecord> messages,
        DebugReport debugReport,
        CancellationToken ct)
    {
        debugReport.IsMultiStage = true;
        debugReport.StageCount = 2;

        var stats = contextBuilder.BuildStats(messages);
        var topUsers = contextBuilder.GetTopActiveUsers(messages);

        var (context, messagesIncluded, tokensEstimate) = contextBuilder.BuildTraditionalContext(
            messages, stats, topUsers);

        debugReport.ContextSent = context;
        debugReport.ContextMessagesCount = messagesIncluded;
        debugReport.ContextTokensEstimate = tokensEstimate;

        var result = await stageExecutor.ExecuteTwoStageAsync(context, stats, debugReport, ct);

        return result.FinalContent;
    }

    private static MessageWithTime ParseMessageWithTime(SearchResult result)
    {
        var time = MetadataParser.ParseTimestamp(result.MetadataJson);

        return new MessageWithTime
        {
            Text = result.ChunkText,
            Time = time,
            Similarity = result.Similarity
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
}
