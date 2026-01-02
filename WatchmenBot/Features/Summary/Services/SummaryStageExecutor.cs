using System.Diagnostics;
using WatchmenBot.Features.Summary.Models;
using WatchmenBot.Infrastructure.Settings;
using WatchmenBot.Features.Admin.Services;
using WatchmenBot.Features.Llm.Services;

namespace WatchmenBot.Features.Summary.Services;

/// <summary>
/// Executes two-stage summary generation:
/// Stage 1: Extract structured facts (low temperature)
/// Stage 2: Add humor to facts (higher temperature)
/// </summary>
public class SummaryStageExecutor(
    LlmRouter llmRouter,
    PromptSettingsStore promptSettings,
    ILogger<SummaryStageExecutor> logger)
{
    private const string FactsSystemPrompt = """
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

    /// <summary>
    /// Execute two-stage summary generation
    /// </summary>
    public async Task<SummaryStageResult> ExecuteTwoStageAsync(
        string context,
        ChatStats stats,
        DebugReport? debugReport,
        CancellationToken ct)
    {
        var result = new SummaryStageResult();

        // STAGE 1: Extract structured facts with low temperature
        var stage1Sw = Stopwatch.StartNew();
        var factsResponse = await llmRouter.CompleteWithFallbackAsync(
            new LlmRequest
            {
                SystemPrompt = FactsSystemPrompt,
                UserPrompt = context,
                Temperature = 0.1
            },
            preferredTag: null,
            ct: ct);
        stage1Sw.Stop();

        result.Stage1Response = factsResponse.Content;
        result.Stage1Tokens = factsResponse.TotalTokens;
        result.Stage1TimeMs = stage1Sw.ElapsedMilliseconds;

        logger.LogDebug("[SummaryStage] Stage 1 (facts) complete, {Length} chars in {Time}ms",
            factsResponse.Content.Length, stage1Sw.ElapsedMilliseconds);

        // Collect debug info for stage 1
        debugReport?.Stages.Add(new DebugStage
        {
            StageNumber = 1,
            Name = "Facts (JSON)",
            Temperature = 0.1,
            SystemPrompt = FactsSystemPrompt,
            UserPrompt = context,
            Response = factsResponse.Content,
            Tokens = factsResponse.TotalTokens,
            TimeMs = stage1Sw.ElapsedMilliseconds
        });

        // STAGE 2: Add humor based on structured facts
        var settings = await promptSettings.GetSettingsAsync("summary");

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

        var stage2Sw = Stopwatch.StartNew();
        var finalResponse = await llmRouter.CompleteWithFallbackAsync(
            new LlmRequest
            {
                SystemPrompt = humorSystemPrompt,
                UserPrompt = humorUserPrompt,
                Temperature = 0.6
            },
            preferredTag: settings.LlmTag,
            ct: ct);
        stage2Sw.Stop();

        result.FinalContent = finalResponse.Content;
        result.Stage2Tokens = finalResponse.TotalTokens;
        result.Stage2TimeMs = stage2Sw.ElapsedMilliseconds;
        result.TotalTokens = factsResponse.TotalTokens + finalResponse.TotalTokens;
        result.Provider = finalResponse.Provider;
        result.Model = finalResponse.Model;
        result.LlmTag = settings.LlmTag;

        logger.LogDebug("[SummaryStage] Stage 2 (humor) complete. Provider: {Provider}", finalResponse.Provider);

        // Collect debug info for stage 2
        debugReport?.Stages.Add(new DebugStage
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
        if (debugReport != null)
        {
            debugReport.SystemPrompt = humorSystemPrompt;
            debugReport.UserPrompt = humorUserPrompt;
            debugReport.LlmProvider = finalResponse.Provider;
            debugReport.LlmModel = finalResponse.Model;
            debugReport.LlmTag = settings.LlmTag;
            debugReport.Temperature = 0.6;
            debugReport.LlmResponse = finalResponse.Content;
            debugReport.PromptTokens = factsResponse.PromptTokens + finalResponse.PromptTokens;
            debugReport.CompletionTokens = factsResponse.CompletionTokens + finalResponse.CompletionTokens;
            debugReport.TotalTokens = result.TotalTokens;
        }

        return result;
    }
}

/// <summary>
/// Result of two-stage summary generation
/// </summary>
public class SummaryStageResult
{
    public string Stage1Response { get; set; } = string.Empty;
    public int Stage1Tokens { get; set; }
    public long Stage1TimeMs { get; set; }

    public string FinalContent { get; set; } = string.Empty;
    public int Stage2Tokens { get; set; }
    public long Stage2TimeMs { get; set; }

    public int TotalTokens { get; set; }
    public string? Provider { get; set; }
    public string? Model { get; set; }
    public string? LlmTag { get; set; }
}
