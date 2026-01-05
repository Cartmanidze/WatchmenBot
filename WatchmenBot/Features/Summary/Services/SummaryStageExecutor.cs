using System.Diagnostics;
using WatchmenBot.Features.Summary.Models;
using WatchmenBot.Infrastructure.Settings;
using WatchmenBot.Features.Admin.Services;
using WatchmenBot.Features.Llm.Services;

namespace WatchmenBot.Features.Summary.Services;

/// <summary>
/// Executes two-stage summary generation:
/// Stage 1: Extract structured facts (low temperature)
/// Stage 2: Add humor/style to facts based on chat mode (higher temperature)
/// Supports Business and Funny modes with different output styles.
/// </summary>
public class SummaryStageExecutor(
    LlmRouter llmRouter,
    PromptSettingsStore promptSettings,
    ChatSettingsStore chatSettings,
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
        long chatId,
        DebugReport? debugReport,
        CancellationToken ct)
    {
        var result = new SummaryStageResult();

        // Get chat mode for appropriate prompt style
        var chatSettingsData = await chatSettings.GetSettingsAsync(chatId);

        logger.LogDebug("[SummaryStage] Using mode={Mode} for chat {ChatId}",
            chatSettingsData.Mode, chatId);

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

        // STAGE 2: Add style based on chat mode
        var settings = await promptSettings.GetSettingsAsync("summary", chatSettingsData.Mode, chatSettingsData.Language);

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

    /// <summary>
    /// Execute enhanced two-stage summary with pre-extracted facts
    /// </summary>
    public async Task<SummaryStageResult> ExecuteEnhancedTwoStageAsync(
        string context,
        ChatStats stats,
        EnhancedExtractedFacts facts,
        long chatId,
        DebugReport? debugReport,
        CancellationToken ct)
    {
        var result = new SummaryStageResult();

        // Get chat mode for appropriate prompt style
        var chatSettingsData = await chatSettings.GetSettingsAsync(chatId);

        logger.LogDebug("[SummaryStage] Enhanced using mode={Mode} for chat {ChatId}",
            chatSettingsData.Mode, chatId);

        // Build structured facts JSON from pre-extracted data
        var structuredFacts = BuildStructuredFacts(facts);
        result.Stage1Response = structuredFacts;

        logger.LogDebug("[SummaryStage] Using pre-extracted facts: {Events} events, {Quotes} quotes, {Decisions} decisions",
            facts.Events.Count, facts.Quotes.Count, facts.Decisions.Count);

        // Collect debug info for "stage 1" (pre-extracted)
        debugReport?.Stages.Add(new DebugStage
        {
            StageNumber = 1,
            Name = "Pre-extracted Facts",
            Temperature = 0.0,
            SystemPrompt = "(from EventDetector + QuoteMiner)",
            UserPrompt = context,
            Response = structuredFacts,
            Tokens = 0,
            TimeMs = 0
        });

        // STAGE 2: Generate enhanced summary with pre-extracted facts and chat mode
        var settings = await promptSettings.GetSettingsAsync("summary", chatSettingsData.Mode, chatSettingsData.Language);

        var humorSystemPrompt = $"""
            {settings.SystemPrompt}

            КРИТИЧЕСКИ ВАЖНО:
            1. Используй ТОЛЬКО факты из JSON ниже
            2. НЕ придумывай новых событий, имён, цитат
            3. Цитаты бери ДОСЛОВНО из поля "quotes"
            4. Героев дня бери из поля "heroes"
            5. Добавляй юмор к СУЩЕСТВУЮЩИМ фактам

            ФОРМАТ ВЫВОДА:
            📊 <b>Отчёт</b>

            🕐 <b>ХРОНОЛОГИЯ:</b>
            • [время] — [тема/событие] (X сообщений)

            🎯 <b>КЛЮЧЕВОЕ:</b>
            • Решения и важные события

            ❓ <b>ОТКРЫТЫЕ ВОПРОСЫ:</b>
            • Нерешённые вопросы (если есть)

            💬 <b>ЦИТАТЫ ДНЯ:</b>
            • "цитата" — @автор

            🔥 <b>ГОРЯЧИЕ МОМЕНТЫ:</b>
            • Описание споров/бурных обсуждений

            🏆 <b>ГЕРОИ ДНЯ:</b>
            • Имя — за что отличился
            """;

        var humorUserPrompt = $"""
            СТРУКТУРИРОВАННЫЕ ФАКТЫ (JSON):
            {structuredFacts}

            СТАТИСТИКА:
            - Сообщений: {stats.TotalMessages}
            - Участников: {stats.UniqueUsers}

            Сгенерируй саммари по формату из system prompt.
            Используй ТОЛЬКО данные из JSON выше!
            Пропускай пустые секции.
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
        result.TotalTokens = finalResponse.TotalTokens;
        result.Provider = finalResponse.Provider;
        result.Model = finalResponse.Model;
        result.LlmTag = settings.LlmTag;

        logger.LogDebug("[SummaryStage] Enhanced Stage 2 complete. Provider: {Provider}", finalResponse.Provider);

        // Collect debug info for stage 2
        debugReport?.Stages.Add(new DebugStage
        {
            StageNumber = 2,
            Name = "Enhanced Humor",
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
            debugReport.PromptTokens = finalResponse.PromptTokens;
            debugReport.CompletionTokens = finalResponse.CompletionTokens;
            debugReport.TotalTokens = result.TotalTokens;
        }

        return result;
    }

    /// <summary>
    /// Build structured facts JSON from pre-extracted data
    /// </summary>
    private static string BuildStructuredFacts(EnhancedExtractedFacts facts)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("{");

        // Timeline
        sb.AppendLine("  \"timeline\": [");
        for (var i = 0; i < facts.Timeline.Count; i++)
        {
            var t = facts.Timeline[i];
            var comma = i < facts.Timeline.Count - 1 ? "," : "";
            sb.AppendLine($"    {{\"period\": \"{t.Period}\", \"label\": \"{EscapeJson(t.Label)}\", \"topics\": [{string.Join(", ", t.Topics.Select(x => $"\"{EscapeJson(x)}\""))}], \"message_count\": {t.MessageCount}}}{comma}");
        }
        sb.AppendLine("  ],");

        // Events
        sb.AppendLine("  \"events\": [");
        for (var i = 0; i < facts.Events.Count; i++)
        {
            var e = facts.Events[i];
            var comma = i < facts.Events.Count - 1 ? "," : "";
            var participants = string.Join(", ", e.Participants.Select(p => $"\"{EscapeJson(p)}\""));
            sb.AppendLine($"    {{\"time\": \"{e.Time ?? ""}\", \"what\": \"{EscapeJson(e.Description)}\", \"who\": [{participants}], \"importance\": \"{e.Importance}\"}}{comma}");
        }
        sb.AppendLine("  ],");

        // Decisions
        sb.AppendLine("  \"decisions\": [");
        for (var i = 0; i < facts.Decisions.Count; i++)
        {
            var d = facts.Decisions[i];
            var comma = i < facts.Decisions.Count - 1 ? "," : "";
            sb.AppendLine($"    {{\"what\": \"{EscapeJson(d.What)}\", \"who\": \"{EscapeJson(d.Who ?? "")}\"}}{comma}");
        }
        sb.AppendLine("  ],");

        // Quotes
        sb.AppendLine("  \"quotes\": [");
        for (var i = 0; i < facts.Quotes.Count; i++)
        {
            var q = facts.Quotes[i];
            var comma = i < facts.Quotes.Count - 1 ? "," : "";
            sb.AppendLine($"    {{\"text\": \"{EscapeJson(q.Text)}\", \"author\": \"{EscapeJson(q.Author)}\", \"category\": \"{q.Category}\"}}{comma}");
        }
        sb.AppendLine("  ],");

        // Heroes
        sb.AppendLine("  \"heroes\": [");
        for (var i = 0; i < facts.Heroes.Count; i++)
        {
            var h = facts.Heroes[i];
            var comma = i < facts.Heroes.Count - 1 ? "," : "";
            sb.AppendLine($"    {{\"name\": \"{EscapeJson(h.Name)}\", \"why\": \"{EscapeJson(h.Why)}\", \"achievement\": \"{EscapeJson(h.Achievement ?? "")}\"}}{comma}");
        }
        sb.AppendLine("  ],");

        // Hot moments
        sb.AppendLine("  \"hot_moments\": [");
        for (var i = 0; i < facts.HotMoments.Count; i++)
        {
            var m = facts.HotMoments[i];
            var comma = i < facts.HotMoments.Count - 1 ? "," : "";
            var participants = string.Join(", ", m.Participants.Select(p => $"\"{EscapeJson(p)}\""));
            sb.AppendLine($"    {{\"time\": \"{m.Time ?? ""}\", \"description\": \"{EscapeJson(m.Description)}\", \"participants\": [{participants}]}}{comma}");
        }
        sb.AppendLine("  ],");

        // Open questions
        sb.AppendLine("  \"open_questions\": [");
        for (var i = 0; i < facts.OpenQuestions.Count; i++)
        {
            var q = facts.OpenQuestions[i];
            var comma = i < facts.OpenQuestions.Count - 1 ? "," : "";
            sb.AppendLine($"    {{\"question\": \"{EscapeJson(q.Question)}\", \"context\": \"{EscapeJson(q.Context ?? "")}\"}}{comma}");
        }
        sb.AppendLine("  ]");

        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string EscapeJson(string s)
    {
        return s
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r")
            .Replace("\t", "\\t");
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
