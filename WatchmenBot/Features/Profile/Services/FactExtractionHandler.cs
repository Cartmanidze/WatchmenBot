using System.Diagnostics;
using System.Text.Json;
using Dapper;
using WatchmenBot.Infrastructure.Database;
using WatchmenBot.Features.Llm.Services;

namespace WatchmenBot.Features.Profile.Services;

/// <summary>
/// Handler for extracting facts from queued messages.
/// Processes message queue, groups by user, extracts facts via LLM.
/// </summary>
public class FactExtractionHandler(
    ProfileQueueService queueService,
    IDbConnectionFactory connectionFactory,
    LlmRouter llmRouter,
    ProfileOptions options,
    ILogger<FactExtractionHandler> logger)
    : IProfileHandler
{
    public string Name => "facts";

    public bool IsEnabled => true; // Always enabled for now

    public async Task<ProfileStats> GetStatsAsync(CancellationToken ct = default)
    {
        // Get pending messages from queue (with high limit to get count)
        var pending = await queueService.GetPendingMessagesAsync(10000);
        var pendingCount = pending.Count;
        var totalProcessed = await GetTotalFactsCountAsync();

        return new ProfileStats(
            TotalItems: pendingCount + totalProcessed,
            ProcessedItems: totalProcessed,
            PendingItems: pendingCount);
    }

    public async Task<ProfileResult> ProcessAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            // Get pending messages from queue
            var messages = await queueService.GetPendingMessagesAsync(options.MaxMessagesPerBatch);

            if (messages.Count == 0)
            {
                return new ProfileResult(
                    ProcessedCount: 0,
                    ElapsedTime: sw.Elapsed,
                    HasMoreWork: false);
            }

            logger.LogInformation("[FactHandler] Processing {Count} messages from queue", messages.Count);

            // Group by user
            var byUser = messages
                .GroupBy(m => (m.ChatId, m.UserId))
                .ToList();

            var processedIds = new List<int>();
            var factsExtracted = 0;

            foreach (var userGroup in byUser)
            {
                var chatId = userGroup.Key.ChatId;
                var userId = userGroup.Key.UserId;
                var userMessages = userGroup.ToList();

                // Skip if not enough messages
                if (userMessages.Count < options.MinMessagesForFactExtraction)
                {
                    logger.LogDebug("[FactHandler] User {UserId} has only {Count} messages, need {Min}",
                        userId, userMessages.Count, options.MinMessagesForFactExtraction);
                    continue;
                }

                try
                {
                    var displayName = userMessages.FirstOrDefault(m => !string.IsNullOrEmpty(m.DisplayName))?.DisplayName
                                      ?? $"User_{userId}";

                    var facts = await ExtractFactsAsync(displayName, userMessages, ct);

                    if (facts.Count > 0)
                    {
                        await SaveFactsAsync(chatId, userId, facts, userMessages.Select(m => m.MessageId).ToArray());
                        factsExtracted += facts.Count;
                        logger.LogInformation("[FactHandler] Extracted {Count} facts for {User} in chat {Chat}",
                            facts.Count, displayName, chatId);
                    }

                    processedIds.AddRange(userMessages.Select(m => m.Id));
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "[FactHandler] Failed to extract facts for user {UserId}", userId);
                    // Mark as processed anyway to avoid getting stuck
                    processedIds.AddRange(userMessages.Select(m => m.Id));
                }

                // Small delay between LLM requests
                if (options.LlmRequestDelaySeconds > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(options.LlmRequestDelaySeconds), ct);
                }
            }

            // Mark as processed
            await queueService.MarkAsProcessedAsync(processedIds);

            // Cleanup old messages periodically
            await queueService.CleanupOldMessagesAsync();

            sw.Stop();

            // Has more work if we got a full batch
            var hasMore = messages.Count >= options.MaxMessagesPerBatch;

            return new ProfileResult(
                ProcessedCount: factsExtracted,
                ElapsedTime: sw.Elapsed,
                HasMoreWork: hasMore);
        }
        catch (Exception ex)
        {
            sw.Stop();
            logger.LogError(ex, "[FactHandler] Error in ProcessAsync");
            throw;
        }
    }

    private async Task<List<ExtractedFact>> ExtractFactsAsync(
        string displayName,
        List<QueuedMessage> messages,
        CancellationToken ct)
    {
        var messagesText = string.Join("\n", messages.Select(m => $"[{m.CreatedAt:HH:mm}] {m.Text}"));

        var prompt = $"""
            Проанализируй сообщения пользователя {displayName} и извлеки факты о нём.

            Сообщения:
            {messagesText}

            Верни JSON в формате:
            - facts: массив объектов с полями type, text, confidence
            - type: likes, dislikes, said, does, knows, opinion
            - confidence: 0.9 = явно сказал, 0.7 = можно предположить, 0.5 = слабая связь

            Пример: facts: [type: "likes", text: "любит футбол", confidence: 0.9]

            ПРАВИЛА:
            - Имена пиши ТОЧНО как в тексте (не транслитерируй, не "исправляй")
            - Если фактов нет — верни пустой массив facts
            - НЕ выдумывай факты, только из текста
            """;

        var llmResponse = await llmRouter.CompleteWithFallbackAsync(
            new LlmRequest { SystemPrompt = "", UserPrompt = prompt, Temperature = 0.1 },
            preferredTag: null, ct: ct);
        var response = llmResponse.Content;

        try
        {
            var json = ExtractJson(response);
            var result = JsonSerializer.Deserialize<FactsResponse>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            return result?.Facts ?? [];
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[FactHandler] Failed to parse facts JSON: {Response}", response);
            return [];
        }
    }

    private static string ExtractJson(string response)
    {
        var start = response.IndexOf('{');
        var end = response.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            return response.Substring(start, end - start + 1);
        }
        return response;
    }

    private async Task SaveFactsAsync(
        long chatId,
        long userId,
        List<ExtractedFact> facts,
        long[] sourceMessageIds)
    {
        using var connection = await connectionFactory.CreateConnectionAsync();

        foreach (var fact in facts)
        {
            await connection.ExecuteAsync("""
                INSERT INTO user_facts (chat_id, user_id, fact_type, fact_text, confidence, source_message_ids)
                VALUES (@ChatId, @UserId, @Type, @Text, @Confidence, @SourceIds)
                ON CONFLICT (chat_id, user_id, fact_text) DO UPDATE SET
                    confidence = GREATEST(user_facts.confidence, EXCLUDED.confidence),
                    source_message_ids = user_facts.source_message_ids || EXCLUDED.source_message_ids,
                    created_at = NOW()
                """,
                new
                {
                    ChatId = chatId,
                    UserId = userId,
                    Type = fact.Type,
                    Text = fact.Text,
                    Confidence = fact.Confidence,
                    SourceIds = sourceMessageIds
                });
        }
    }

    private async Task<long> GetTotalFactsCountAsync()
    {
        using var connection = await connectionFactory.CreateConnectionAsync();
        var count = await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM user_facts");
        return count;
    }

    private class FactsResponse
    {
        public List<ExtractedFact> Facts { get; set; } = [];
    }
}

public class ExtractedFact
{
    public string Type { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public double Confidence { get; set; } = 0.7;
}
