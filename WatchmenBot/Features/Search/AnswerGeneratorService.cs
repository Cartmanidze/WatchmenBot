using System.Diagnostics;
using System.Text.Json;
using WatchmenBot.Infrastructure.Settings;
using WatchmenBot.Features.Admin.Services;
using WatchmenBot.Features.Llm.Services;
using WatchmenBot.Features.Search.Models;

namespace WatchmenBot.Features.Search;

/// <summary>
/// Service for generating LLM answers for /ask and /smart commands
/// Handles prompt building and LLM interaction with debug support.
/// Supports two modes:
/// - OneStage: Fast, single LLM call (default)
/// - TwoStage: Anti-hallucination with fact extraction → grounded answer
/// </summary>
public class AnswerGeneratorService(
    LlmRouter llmRouter,
    PromptSettingsStore promptSettings,
    ILogger<AnswerGeneratorService> logger)
{
    /// <summary>
    /// Enable two-stage anti-hallucination mode.
    /// Stage 1: Extract facts from context (T=0.1)
    /// Stage 2: Generate answer from facts only (T=0.5)
    /// </summary>
    private const bool UseTwoStageMode = true;

    private const string FactExtractionPrompt = """
        Извлеки ТОЛЬКО факты из контекста чата для ответа на вопрос.
        Отвечай СТРОГО JSON без markdown:

        {
          "facts": [
            {"claim": "утверждение", "source": "кто сказал/упомянул", "confidence": "high|medium|low"}
          ],
          "quotes": [
            {"text": "прямая цитата", "author": "имя", "context": "о чём"}
          ],
          "not_found": ["что спрашивали, но нет в контексте"],
          "relevance_note": "краткая оценка релевантности контекста"
        }

        ПРАВИЛА:
        1. ТОЛЬКО факты из контекста — НЕ придумывай
        2. Цитаты — ДОСЛОВНО из сообщений
        3. Если информации нет — добавь в not_found
        4. confidence: high = прямое утверждение, medium = можно вывести, low = косвенно
        5. Максимум 5 фактов, 3 цитаты
        """;
    public async Task<string> GenerateAnswerWithDebugAsync(
        string command, string question, string? context, string? memoryContext, string askerName, DebugReport debugReport, CancellationToken ct)
    {
        var settings = await promptSettings.GetSettingsAsync(command);

        // For /ask with context - choose between one-stage (fast) or two-stage (anti-hallucination)
        if (command == "ask" && !string.IsNullOrWhiteSpace(context))
        {
            if (UseTwoStageMode)
            {
                return await GenerateTwoStageAnswerWithDebugAsync(question, context, memoryContext, askerName, settings, debugReport, ct);
            }
        }

        var sw = Stopwatch.StartNew();

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

    /// <summary>
    /// Two-stage anti-hallucination generation for /ask:
    /// Stage 1: Extract facts from context (T=0.1, factual precision)
    /// Stage 2: Generate answer from extracted facts only (T=0.5, allow humor)
    /// </summary>
    private async Task<string> GenerateTwoStageAnswerWithDebugAsync(
        string question, string context, string? memoryContext, string askerName, PromptSettings settings, DebugReport debugReport, CancellationToken ct)
    {
        debugReport.IsMultiStage = true;
        debugReport.StageCount = 2;
        var totalSw = Stopwatch.StartNew();

        // Build memory section if available
        var memorySection = !string.IsNullOrWhiteSpace(memoryContext)
            ? $"""

              === ПАМЯТЬ О ПОЛЬЗОВАТЕЛЕ ===
              {memoryContext}

              """
            : "";

        // ===== STAGE 1: Extract facts =====
        var stage1Sw = Stopwatch.StartNew();

        var factExtractionUserPrompt = $"""
            Спрашивает: {askerName}
            Вопрос: {question}
            {memorySection}
            === КОНТЕКСТ ИЗ ЧАТА ===
            {context}
            """;

        var stage1Response = await llmRouter.CompleteWithFallbackAsync(
            new LlmRequest
            {
                SystemPrompt = FactExtractionPrompt,
                UserPrompt = factExtractionUserPrompt,
                Temperature = 0.1 // Low temperature for factual precision
            },
            preferredTag: null, // Use default fast model for extraction
            ct: ct);

        stage1Sw.Stop();

        // Parse extracted facts
        var facts = ParseAnswerFacts(stage1Response.Content);

        logger.LogInformation("[ASK] Stage1 (Facts): {FactCount} facts, {QuoteCount} quotes, {NotFoundCount} not_found in {Ms}ms",
            facts.Facts.Count, facts.Quotes.Count, facts.NotFound.Count, stage1Sw.ElapsedMilliseconds);

        debugReport.Stages.Add(new DebugStage
        {
            StageNumber = 1,
            Name = "FactExtraction",
            Temperature = 0.1,
            SystemPrompt = FactExtractionPrompt,
            UserPrompt = factExtractionUserPrompt,
            Response = stage1Response.Content,
            Tokens = stage1Response.TotalTokens,
            TimeMs = stage1Sw.ElapsedMilliseconds
        });

        // ===== STAGE 2: Generate grounded answer =====
        var stage2Sw = Stopwatch.StartNew();

        var groundedSystemPrompt = $"""
            {settings.SystemPrompt}

            КРИТИЧЕСКИ ВАЖНО — ANTI-HALLUCINATION:
            1. Используй ТОЛЬКО факты из JSON ниже
            2. НЕ придумывай новых фактов, имён, событий
            3. Цитаты бери ДОСЛОВНО из поля "quotes"
            4. Если факта нет — честно скажи "хз" или "не знаю"
            5. Если в not_found есть то, что спрашивали — упомяни что не нашёл
            6. Добавляй юмор и подъёбку к СУЩЕСТВУЮЩИМ фактам
            """;

        var factsJson = FormatFactsForStage2(facts);

        var groundedUserPrompt = $"""
            Сегодняшняя дата: {DateTime.UtcNow:dd.MM.yyyy}
            Спрашивает: {askerName}
            Вопрос: {question}

            ИЗВЛЕЧЁННЫЕ ФАКТЫ (JSON):
            {factsJson}

            Ответь на вопрос ТОЛЬКО используя факты выше.
            Формат: 2-4 предложения, HTML для <b> и <i>.
            """;

        var stage2Response = await llmRouter.CompleteWithFallbackAsync(
            new LlmRequest
            {
                SystemPrompt = groundedSystemPrompt,
                UserPrompt = groundedUserPrompt,
                Temperature = 0.5 // Allow humor while staying grounded
            },
            preferredTag: settings.LlmTag,
            ct: ct);

        stage2Sw.Stop();
        totalSw.Stop();

        debugReport.Stages.Add(new DebugStage
        {
            StageNumber = 2,
            Name = "GroundedAnswer",
            Temperature = 0.5,
            SystemPrompt = groundedSystemPrompt,
            UserPrompt = groundedUserPrompt,
            Response = stage2Response.Content,
            Tokens = stage2Response.TotalTokens,
            TimeMs = stage2Sw.ElapsedMilliseconds
        });

        // Collect final debug info
        debugReport.SystemPrompt = groundedSystemPrompt;
        debugReport.UserPrompt = groundedUserPrompt;
        debugReport.LlmProvider = stage2Response.Provider;
        debugReport.LlmModel = stage2Response.Model;
        debugReport.LlmTag = settings.LlmTag;
        debugReport.Temperature = 0.5;
        debugReport.LlmResponse = stage2Response.Content;
        debugReport.PromptTokens = stage1Response.PromptTokens + stage2Response.PromptTokens;
        debugReport.CompletionTokens = stage1Response.CompletionTokens + stage2Response.CompletionTokens;
        debugReport.TotalTokens = stage1Response.TotalTokens + stage2Response.TotalTokens;
        debugReport.LlmTimeMs = totalSw.ElapsedMilliseconds;

        logger.LogInformation(
            "[ASK] TwoStage: Stage1={S1Ms}ms, Stage2={S2Ms}ms, Total={TotalMs}ms, {TotalTokens} tokens",
            stage1Sw.ElapsedMilliseconds, stage2Sw.ElapsedMilliseconds,
            totalSw.ElapsedMilliseconds, debugReport.TotalTokens);

        return stage2Response.Content;
    }

    /// <summary>
    /// Parse JSON response from fact extraction stage
    /// </summary>
    private AnswerFacts ParseAnswerFacts(string content)
    {
        var result = new AnswerFacts();

        try
        {
            var cleaned = CleanJsonResponse(content);
            var json = JsonDocument.Parse(cleaned);
            var root = json.RootElement;

            // Parse facts
            if (root.TryGetProperty("facts", out var factsEl) && factsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in factsEl.EnumerateArray())
                {
                    var claim = f.TryGetProperty("claim", out var c) && c.ValueKind == JsonValueKind.String
                        ? c.GetString()
                        : null;

                    if (string.IsNullOrWhiteSpace(claim))
                        continue;

                    result.Facts.Add(new ExtractedFact
                    {
                        Claim = claim,
                        Source = f.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.String
                            ? s.GetString()
                            : null,
                        Confidence = f.TryGetProperty("confidence", out var conf) && conf.ValueKind == JsonValueKind.String
                            ? conf.GetString() ?? "medium"
                            : "medium"
                    });
                }
            }

            // Parse quotes
            if (root.TryGetProperty("quotes", out var quotesEl) && quotesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var q in quotesEl.EnumerateArray())
                {
                    var text = q.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String
                        ? t.GetString()
                        : null;
                    var author = q.TryGetProperty("author", out var a) && a.ValueKind == JsonValueKind.String
                        ? a.GetString()
                        : null;

                    if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(author))
                        continue;

                    result.Quotes.Add(new ExtractedQuote
                    {
                        Text = text,
                        Author = author,
                        Context = q.TryGetProperty("context", out var ctx) && ctx.ValueKind == JsonValueKind.String
                            ? ctx.GetString()
                            : null
                    });
                }
            }

            // Parse not_found
            if (root.TryGetProperty("not_found", out var notFoundEl) && notFoundEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var nf in notFoundEl.EnumerateArray())
                {
                    if (nf.ValueKind == JsonValueKind.String)
                    {
                        var val = nf.GetString();
                        if (!string.IsNullOrWhiteSpace(val))
                        {
                            result.NotFound.Add(val);
                        }
                    }
                }
            }

            // Parse relevance_note
            if (root.TryGetProperty("relevance_note", out var noteEl) && noteEl.ValueKind == JsonValueKind.String)
            {
                result.RelevanceNote = noteEl.GetString();
            }
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "[AnswerFacts] Failed to parse JSON, returning empty facts");
        }

        return result;
    }

    /// <summary>
    /// Format extracted facts as JSON for Stage 2
    /// </summary>
    private static string FormatFactsForStage2(AnswerFacts facts)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("{");

        // Facts
        sb.AppendLine("  \"facts\": [");
        for (var i = 0; i < facts.Facts.Count; i++)
        {
            var f = facts.Facts[i];
            var comma = i < facts.Facts.Count - 1 ? "," : "";
            sb.AppendLine($"    {{\"claim\": \"{EscapeJson(f.Claim)}\", \"source\": \"{EscapeJson(f.Source ?? "")}\", \"confidence\": \"{f.Confidence}\"}}{comma}");
        }
        sb.AppendLine("  ],");

        // Quotes
        sb.AppendLine("  \"quotes\": [");
        for (var i = 0; i < facts.Quotes.Count; i++)
        {
            var q = facts.Quotes[i];
            var comma = i < facts.Quotes.Count - 1 ? "," : "";
            sb.AppendLine($"    {{\"text\": \"{EscapeJson(q.Text)}\", \"author\": \"{EscapeJson(q.Author)}\"}}{comma}");
        }
        sb.AppendLine("  ],");

        // Not found
        sb.AppendLine("  \"not_found\": [");
        for (var i = 0; i < facts.NotFound.Count; i++)
        {
            var comma = i < facts.NotFound.Count - 1 ? "," : "";
            sb.AppendLine($"    \"{EscapeJson(facts.NotFound[i])}\"{comma}");
        }
        sb.AppendLine("  ],");

        // Relevance note
        sb.AppendLine($"  \"relevance_note\": \"{EscapeJson(facts.RelevanceNote ?? "")}\"");
        sb.AppendLine("}");

        return sb.ToString();
    }

    private static string CleanJsonResponse(string content)
    {
        var cleaned = content.Trim();

        // Remove markdown code blocks
        if (cleaned.StartsWith("```"))
        {
            var lines = cleaned.Split('\n');
            cleaned = string.Join("\n", lines.Skip(1).TakeWhile(l => !l.StartsWith("```")));
        }

        // Find JSON object boundaries
        var start = cleaned.IndexOf('{');
        var end = cleaned.LastIndexOf('}');

        if (start >= 0 && end > start)
        {
            cleaned = cleaned[start..(end + 1)];
        }

        return cleaned;
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
