using System.Text.Json;
using WatchmenBot.Features.Llm.Services;

namespace WatchmenBot.Features.Search.Services;

/// <summary>
/// HyDE (Hypothetical Document Embeddings) service.
/// Generates hypothetical answers AND search patterns for better semantic search.
///
/// Instead of searching by question embedding (which is in "question space"),
/// we generate hypothetical answers and patterns (which are in "answer space").
/// This dramatically improves question→answer retrieval.
///
/// Enhanced with Q→A Transformation: generates structural patterns
/// that would appear in real answers, not just natural responses.
///
/// Paper: https://arxiv.org/abs/2212.10496
/// </summary>
public class HydeService(
    LlmRouter llmRouter,
    ILogger<HydeService> logger)
{
    private const string SystemPrompt = """
        Ты — помощник для поиска в истории чата. Сгенерируй ГИПОТЕТИЧЕСКИЙ ОТВЕТ и ПАТТЕРНЫ ПОИСКА.

        ЗАДАЧА:
        1. Напиши короткий ответ на вопрос (как участник чата)
        2. Придумай 2-3 фразы/паттерна, которые могли бы быть в реальных сообщениях

        ПРАВИЛА:
        - Отвечай как участник чата, который ЗНАЕТ ответ
        - Пиши коротко и неформально
        - НЕ говори "я не знаю"
        - Если вопрос с "ты" — это вопрос К БОТУ, генерируй паттерны о боте
        - Паттерны должны быть фразами которые РЕАЛЬНО пишут в чатах

        КРИТИЧНО для вопросов К БОТУ ("ты создан?", "зачем ты?", "ты умеешь?"):
        - answer: ответ от лица бота
        - patterns: ["создан чтобы", "бот для", "ты нужен для", "[имя бота] умеет"]
        - НЕ генерируй человеческие экзистенциальные паттерны!

        ПРИМЕРЫ:

        Вопрос: "для чего ты создан?"
        {
          "answer": "я создан чтобы помогать искать инфу в истории чата",
          "patterns": ["создан чтобы", "бот для", "бот нужен для"]
        }

        Вопрос: "ты разочарован своей целью?"
        {
          "answer": "не, я не разочарован, моя цель — помогать вам",
          "patterns": ["создан чтобы", "цель бота", "бот существует для"]
        }

        Вопрос: "кто тут самый умный?"
        {
          "answer": "ну Вася конечно самый умный, он всегда всё знает",
          "patterns": ["самый умный", "умнее всех", "гений"]
        }

        Вопрос: "что вчера обсуждали?"
        {
          "answer": "вчера обсуждали новый проект, Петя предложил React",
          "patterns": ["вчера говорили", "обсуждали", "вчера было"]
        }

        Отвечай ТОЛЬКО JSON без markdown:
        {"answer": "...", "patterns": ["...", "..."]}
        """;

    /// <summary>
    /// Generate hypothetical answer and search patterns for a question.
    /// Both will be used for semantic search to bridge Q→A gap.
    /// </summary>
    public async Task<HydeResult> GenerateHypotheticalAnswerAsync(
        string question,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var response = await llmRouter.CompleteAsync(new LlmRequest
            {
                SystemPrompt = SystemPrompt,
                UserPrompt = question,
                Temperature = 0.7
            }, ct);

            var (answer, patterns) = ParseResponse(response.Content);

            sw.Stop();

            logger.LogInformation(
                "[HyDE] Question: '{Question}' → Answer: '{Answer}', Patterns: [{Patterns}] ({Ms}ms)",
                question.Length > 40 ? question[..40] + "..." : question,
                answer?.Length > 50 ? answer[..50] + "..." : answer ?? "null",
                string.Join(", ", patterns.Take(3)),
                sw.ElapsedMilliseconds);

            return new HydeResult
            {
                OriginalQuestion = question,
                HypotheticalAnswer = answer,
                SearchPatterns = patterns,
                GenerationTimeMs = sw.ElapsedMilliseconds,
                Success = !string.IsNullOrEmpty(answer)
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            logger.LogWarning(ex, "[HyDE] Failed to generate for: {Question}", question);

            return new HydeResult
            {
                OriginalQuestion = question,
                HypotheticalAnswer = null,
                SearchPatterns = [],
                GenerationTimeMs = sw.ElapsedMilliseconds,
                Success = false,
                Error = ex.Message
            };
        }
    }

    private (string? answer, List<string> patterns) ParseResponse(string content)
    {
        var cleaned = CleanJsonResponse(content);

        try
        {
            using var doc = JsonDocument.Parse(cleaned);
            var root = doc.RootElement;

            var answer = root.TryGetProperty("answer", out var ans) && ans.ValueKind == JsonValueKind.String
                ? ans.GetString()
                : null;

            var patterns = new List<string>();
            if (root.TryGetProperty("patterns", out var pats) && pats.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in pats.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var p = item.GetString();
                        if (!string.IsNullOrWhiteSpace(p))
                            patterns.Add(p);
                    }
                }
            }

            return (answer, patterns);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "[HyDE] Failed to parse JSON, using raw content as answer");
            // Fallback: treat the whole response as the answer
            return (cleaned, []);
        }
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

        return cleaned.Trim();
    }
}

/// <summary>
/// Result of HyDE generation
/// </summary>
public class HydeResult
{
    public required string OriginalQuestion { get; init; }

    /// <summary>
    /// Natural hypothetical answer (for semantic search in "answer space")
    /// </summary>
    public string? HypotheticalAnswer { get; init; }

    /// <summary>
    /// Structural patterns that would appear in real answers (Q→A Transformation)
    /// </summary>
    public List<string> SearchPatterns { get; init; } = [];

    public long GenerationTimeMs { get; init; }
    public bool Success { get; init; }
    public string? Error { get; init; }
}
