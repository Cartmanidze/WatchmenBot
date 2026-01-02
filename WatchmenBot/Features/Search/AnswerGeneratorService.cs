using WatchmenBot.Infrastructure.Settings;
using WatchmenBot.Features.Admin.Services;
using WatchmenBot.Features.Llm.Services;

namespace WatchmenBot.Features.Search;

/// <summary>
/// Service for generating LLM answers for /ask and /smart commands
/// Handles prompt building and LLM interaction with debug support
/// </summary>
public class AnswerGeneratorService(
    LlmRouter llmRouter,
    PromptSettingsStore promptSettings,
    ILogger<AnswerGeneratorService> logger)
{
    public async Task<string> GenerateAnswerWithDebugAsync(
        string command, string question, string? context, string? memoryContext, string askerName, DebugReport debugReport, CancellationToken ct)
    {
        var settings = await promptSettings.GetSettingsAsync(command);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // For /ask with context - use one-stage generation (faster than two-stage)
        if (command == "ask" && !string.IsNullOrWhiteSpace(context))
        {
            return await GenerateOneStageAnswerWithDebugAsync(question, context, memoryContext, askerName, settings, debugReport, ct);
        }

        // Build memory section if available
        var memorySection = !string.IsNullOrWhiteSpace(memoryContext)
            ? $"\n{memoryContext}\n"
            : "";

        // For /q or /ask without context - single stage
        var userPrompt = string.IsNullOrWhiteSpace(context)
            ? $"""
                Сегодняшняя дата: {DateTime.UtcNow:dd.MM.yyyy}
                {memorySection}
                Спрашивает: {askerName}
                Вопрос: {question}
                """
            : $"""
                Сегодняшняя дата: {DateTime.UtcNow:dd.MM.yyyy}
                {memorySection}
                Контекст из чата:
                {context}

                Спрашивает: {askerName}
                Вопрос: {question}
                """;

        var response = await llmRouter.CompleteWithFallbackAsync(
            new LlmRequest
            {
                SystemPrompt = settings.SystemPrompt,
                UserPrompt = userPrompt,
                Temperature = 0.5
            },
            preferredTag: settings.LlmTag,
            ct: ct);

        sw.Stop();

        // Collect debug info
        debugReport.SystemPrompt = settings.SystemPrompt;
        debugReport.UserPrompt = userPrompt;
        debugReport.LlmProvider = response.Provider;
        debugReport.LlmModel = response.Model;
        debugReport.LlmTag = settings.LlmTag;
        debugReport.Temperature = 0.5;
        debugReport.LlmResponse = response.Content;
        debugReport.PromptTokens = response.PromptTokens;
        debugReport.CompletionTokens = response.CompletionTokens;
        debugReport.TotalTokens = response.TotalTokens;
        debugReport.LlmTimeMs = sw.ElapsedMilliseconds;

        logger.LogInformation("[{Command}] LLM: provider={Provider}, model={Model}, tag={Tag}",
            command.ToUpper(), response.Provider, response.Model, settings.LlmTag ?? "default");

        return response.Content;
    }

    /// <summary>
    /// One-stage generation for /ask: analyze context and generate response in single LLM call.
    /// Faster than two-stage (saves ~1-2 sec) while maintaining quality.
    /// </summary>
    private async Task<string> GenerateOneStageAnswerWithDebugAsync(
        string question, string context, string? memoryContext, string askerName, PromptSettings settings, DebugReport debugReport, CancellationToken ct)
    {
        debugReport.IsMultiStage = false;
        debugReport.StageCount = 1;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Build memory section if available
        var memorySection = !string.IsNullOrWhiteSpace(memoryContext)
            ? $"""

              === ПАМЯТЬ О ПОЛЬЗОВАТЕЛЕ ===
              {memoryContext}

              """
            : "";

        // One-stage prompt: analyze context internally, then respond with humor
        var userPrompt = $"""
            Сегодняшняя дата: {DateTime.UtcNow:dd.MM.yyyy}
            Спрашивает: {askerName}
            Вопрос: {question}
            {memorySection}
            === КОНТЕКСТ ИЗ ЧАТА ===
            {context}

            ИНСТРУКЦИИ:
            1. Проанализируй контекст и найди ТОЛЬКО релевантные сообщения
            2. ИГНОРИРУЙ нерелевантные диалоги — они добавлены для контекста
            3. Если в памяти есть прямой ответ — используй его
            4. Имена пиши ТОЧНО как в контексте (НЕ транслитерируй!)
            5. Если нашёл смешную/глупую цитату по теме — вставь в <i>кавычках</i>
            6. Ответ должен быть дерзким, с подъёбкой, можно с матом
            7. НЕ придумывай факты — только из контекста выше
            8. ЗАПРЕЩЕНО упоминать технические детали: "контекст", "память", "данные"
            9. Отвечай как будто сам всё помнишь

            Формат: 2-4 предложения, HTML для <b> и <i>.
            """;

        var response = await llmRouter.CompleteWithFallbackAsync(
            new LlmRequest
            {
                SystemPrompt = settings.SystemPrompt,
                UserPrompt = userPrompt,
                Temperature = 0.5 // Balanced: accurate facts + some creativity
            },
            preferredTag: settings.LlmTag,
            ct: ct);

        sw.Stop();

        // Collect debug info
        debugReport.SystemPrompt = settings.SystemPrompt;
        debugReport.UserPrompt = userPrompt;
        debugReport.LlmProvider = response.Provider;
        debugReport.LlmModel = response.Model;
        debugReport.LlmTag = settings.LlmTag;
        debugReport.Temperature = 0.5;
        debugReport.LlmResponse = response.Content;
        debugReport.PromptTokens = response.PromptTokens;
        debugReport.CompletionTokens = response.CompletionTokens;
        debugReport.TotalTokens = response.TotalTokens;
        debugReport.LlmTimeMs = sw.ElapsedMilliseconds;

        debugReport.Stages.Add(new DebugStage
        {
            StageNumber = 1,
            Name = "OneStage",
            Temperature = 0.5,
            SystemPrompt = settings.SystemPrompt,
            UserPrompt = userPrompt,
            Response = response.Content,
            Tokens = response.TotalTokens,
            TimeMs = sw.ElapsedMilliseconds
        });

        logger.LogInformation("[ASK] OneStage: provider={Provider}, model={Model}, {Tokens} tokens in {Ms}ms",
            response.Provider, response.Model, response.TotalTokens, sw.ElapsedMilliseconds);

        return response.Content;
    }
}
