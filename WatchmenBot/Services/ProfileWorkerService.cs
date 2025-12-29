using System.Text.Json;
using Dapper;
using WatchmenBot.Infrastructure.Database;
using WatchmenBot.Services.Llm;

namespace WatchmenBot.Services;

/// <summary>
/// Фоновый воркер для обработки очереди сообщений.
/// Каждые 15 минут берёт сообщения из очереди, группирует по пользователю,
/// извлекает факты через LLM и сохраняет в user_facts.
/// </summary>
public class ProfileWorkerService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ProfileWorkerService> _logger;
    private readonly IConfiguration _configuration;

    private readonly TimeSpan _processingInterval;
    private readonly int _minMessagesForExtraction;
    private readonly int _maxMessagesPerBatch;

    public ProfileWorkerService(
        IServiceProvider serviceProvider,
        ILogger<ProfileWorkerService> logger,
        IConfiguration configuration)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _configuration = configuration;

        _processingInterval = TimeSpan.FromMinutes(
            configuration.GetValue<int>("ProfileService:QueueProcessingIntervalMinutes", 15));
        _minMessagesForExtraction = configuration.GetValue<int>("ProfileService:MinMessagesForFactExtraction", 3);
        _maxMessagesPerBatch = configuration.GetValue<int>("ProfileService:MaxMessagesPerBatch", 50);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ProfileWorkerService started. Processing interval: {Interval} minutes",
            _processingInterval.TotalMinutes);

        // Начальная задержка, чтобы дать приложению запуститься
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessQueueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in ProfileWorkerService");
            }

            await Task.Delay(_processingInterval, stoppingToken);
        }

        _logger.LogInformation("ProfileWorkerService stopped");
    }

    private async Task ProcessQueueAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var queueService = scope.ServiceProvider.GetRequiredService<ProfileQueueService>();
        var connectionFactory = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>();
        var llmRouter = scope.ServiceProvider.GetRequiredService<LlmRouter>();

        // Получаем необработанные сообщения
        var messages = await queueService.GetPendingMessagesAsync(_maxMessagesPerBatch);

        if (messages.Count == 0)
        {
            _logger.LogDebug("No pending messages in queue");
            return;
        }

        _logger.LogInformation("Processing {Count} messages from queue", messages.Count);

        // Группируем по пользователю
        var byUser = messages
            .GroupBy(m => (m.ChatId, m.UserId))
            .ToList();

        var processedIds = new List<int>();

        foreach (var userGroup in byUser)
        {
            var chatId = userGroup.Key.ChatId;
            var userId = userGroup.Key.UserId;
            var userMessages = userGroup.ToList();

            // Если сообщений меньше минимума - откладываем
            if (userMessages.Count < _minMessagesForExtraction)
            {
                _logger.LogDebug("User {UserId} has only {Count} messages, need {Min}",
                    userId, userMessages.Count, _minMessagesForExtraction);
                continue;
            }

            try
            {
                var displayName = userMessages.FirstOrDefault(m => !string.IsNullOrEmpty(m.DisplayName))?.DisplayName
                                  ?? $"User_{userId}";

                var facts = await ExtractFactsAsync(llmRouter, displayName, userMessages, ct);

                if (facts.Count > 0)
                {
                    await SaveFactsAsync(connectionFactory, chatId, userId, facts, userMessages.Select(m => m.MessageId).ToArray());
                    _logger.LogInformation("Extracted {Count} facts for user {User} in chat {Chat}",
                        facts.Count, displayName, chatId);
                }

                processedIds.AddRange(userMessages.Select(m => m.Id));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to extract facts for user {UserId}", userId);
                // Помечаем как обработанные всё равно, чтобы не застрять
                processedIds.AddRange(userMessages.Select(m => m.Id));
            }
        }

        // Помечаем обработанные
        await queueService.MarkAsProcessedAsync(processedIds);

        // Периодически чистим старые
        await queueService.CleanupOldMessagesAsync();
    }

    private async Task<List<ExtractedFact>> ExtractFactsAsync(
        LlmRouter llmRouter,
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
            // Извлекаем JSON из ответа
            var json = ExtractJson(response);
            var result = JsonSerializer.Deserialize<FactsResponse>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            return result?.Facts ?? new List<ExtractedFact>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse facts JSON: {Response}", response);
            return new List<ExtractedFact>();
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
        IDbConnectionFactory connectionFactory,
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

    private class FactsResponse
    {
        public List<ExtractedFact> Facts { get; set; } = new();
    }
}

public class ExtractedFact
{
    public string Type { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public double Confidence { get; set; } = 0.7;
}
