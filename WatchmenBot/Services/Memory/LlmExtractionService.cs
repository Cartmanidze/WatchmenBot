using System.Text.Json;
using WatchmenBot.Services.Llm;

namespace WatchmenBot.Services.Memory;

/// <summary>
/// Service for LLM-based extraction of profile updates and memory summaries
/// </summary>
public class LlmExtractionService(
    LlmRouter llmRouter,
    ILogger<LlmExtractionService> logger)
{
    /// <summary>
    /// Extract profile updates from query/response interaction
    /// </summary>
    public async Task<ProfileExtraction?> ExtractProfileUpdatesAsync(string query, string response, CancellationToken ct)
    {
        try
        {
            const string systemPrompt = """
                                        Извлеки информацию о пользователе из диалога. Отвечай ТОЛЬКО JSON.

                                        Правила:
                                        1. facts — конкретные факты о человеке (работа, семья, достижения)
                                        2. traits — черты характера или поведения
                                        3. interests — темы, которые интересуют
                                        4. quotes — запомнившиеся высказывания (если есть)

                                        КРИТИЧЕСКИ ВАЖНО про имена:
                                        - Имена, фамилии, никнеймы пиши ТОЧНО как в тексте
                                        - НЕ "исправляй" и НЕ транслитерируй (Gleb → Глеб ❌)
                                        - НЕ путай с похожими именами (Bezrukov ≠ Безухов!)
                                        - Используй оригинальное написание из контекста

                                        Формат:
                                        {
                                          "facts": ["факт 1", "факт 2"],
                                          "traits": ["черта 1"],
                                          "interests": ["интерес 1"],
                                          "quotes": []
                                        }

                                        Если ничего полезного нет — верни пустые массивы.
                                        """;

            var userPrompt = $"""
                Вопрос пользователя: {query}
                Ответ бота: {MemoryHelpers.TruncateText(response, 500)}

                Извлеки информацию о пользователе:
                """;

            var llmResponse = await llmRouter.CompleteWithFallbackAsync(
                new LlmRequest
                {
                    SystemPrompt = systemPrompt,
                    UserPrompt = userPrompt,
                    Temperature = 0.1
                },
                preferredTag: null,
                ct: ct);

            var json = CleanJsonResponse(llmResponse.Content);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            return new ProfileExtraction
            {
                Facts = MemoryHelpers.GetJsonStringArray(root, "facts"),
                Traits = MemoryHelpers.GetJsonStringArray(root, "traits"),
                Interests = MemoryHelpers.GetJsonStringArray(root, "interests"),
                Quotes = MemoryHelpers.GetJsonStringArray(root, "quotes")
            };
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[Memory] Failed to extract profile updates");
            return null;
        }
    }

    /// <summary>
    /// Generate memory summary from query/response
    /// </summary>
    public async Task<MemorySummary> GenerateMemorySummaryAsync(string query, string response, CancellationToken ct)
    {
        try
        {
            const string systemPrompt = """
                                        Создай краткое резюме диалога. Отвечай ТОЛЬКО JSON.

                                        Формат:
                                        {
                                          "summary": "краткое резюме в 1 предложение",
                                          "topics": ["тема1", "тема2"],
                                          "facts": ["факт если есть"]
                                        }
                                        """;

            var userPrompt = $"""
                Вопрос: {MemoryHelpers.TruncateText(query, 200)}
                Ответ: {MemoryHelpers.TruncateText(response, 300)}
                """;

            var llmResponse = await llmRouter.CompleteWithFallbackAsync(
                new LlmRequest
                {
                    SystemPrompt = systemPrompt,
                    UserPrompt = userPrompt,
                    Temperature = 0.1
                },
                preferredTag: null,
                ct: ct);

            var json = CleanJsonResponse(llmResponse.Content);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            return new MemorySummary
            {
                Summary = root.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : "",
                Topics = MemoryHelpers.GetJsonStringArray(root, "topics"),
                Facts = MemoryHelpers.GetJsonStringArray(root, "facts")
            };
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[Memory] Failed to generate memory summary");
            return new MemorySummary
            {
                Summary = MemoryHelpers.TruncateText(query, 100),
                Topics = [],
                Facts = []
            };
        }
    }

    /// <summary>
    /// Clean JSON response (remove markdown code blocks if present)
    /// </summary>
    private static string CleanJsonResponse(string response)
    {
        var json = response.Trim();
        if (json.StartsWith("```"))
        {
            var lines = json.Split('\n');
            json = string.Join("\n", lines.Skip(1).TakeWhile(l => !l.StartsWith("```")));
        }
        return json;
    }
}
