using System.Text.Json;
using WatchmenBot.Features.Summary.Models;
using WatchmenBot.Features.Llm.Services;

namespace WatchmenBot.Features.Summary.Services;

/// <summary>
/// Extracts memorable quotes and highlights from chat messages using LLM.
/// Finds funny, wise, or savage quotes worth including in the summary.
/// </summary>
public class QuoteMiner(
    LlmRouter llmRouter,
    ILogger<QuoteMiner> logger)
{
    private const string SystemPrompt = """
        Ты анализируешь переписку чата и ищешь запоминающиеся цитаты.

        Твоя задача — найти:
        1. ЛУЧШИЕ ЦИТАТЫ — смешные, мудрые, острые высказывания
        2. ГОРЯЧИЕ МОМЕНТЫ — бурные обсуждения, споры, всплески активности

        Отвечай ТОЛЬКО JSON без markdown:
        {
          "quotes": [
            {"text": "точный текст цитаты", "author": "имя автора", "context": "о чём речь", "category": "funny|wise|savage|wholesome"}
          ],
          "hot_moments": [
            {"time": "примерное время", "description": "что произошло", "participants": ["имя1", "имя2"]}
          ]
        }

        Правила:
        - Цитата должна быть ДОСЛОВНОЙ из переписки
        - category: "funny" = смешно, "wise" = умно, "savage" = жёстко, "wholesome" = мило
        - Максимум 3 цитаты и 2 горячих момента
        - Выбирай только действительно яркие высказывания
        - Если ничего яркого нет — возвращай пустые массивы
        """;

    /// <summary>
    /// Mine memorable quotes and hot moments from chat context
    /// </summary>
    public async Task<MinedQuotes> MineQuotesAsync(
        string context,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(context))
        {
            return new MinedQuotes();
        }

        var userPrompt = $"Переписка:\n{context}\n\nНайди лучшие цитаты и горячие моменты:";

        try
        {
            var response = await llmRouter.CompleteAsync(new LlmRequest
            {
                SystemPrompt = SystemPrompt,
                UserPrompt = userPrompt,
                Temperature = 0.3 // Slightly higher for creative selection
            }, ct);

            var result = ParseResponse(response.Content);

            logger.LogInformation(
                "[QuoteMiner] Mined {Quotes} quotes, {HotMoments} hot moments",
                result.BestQuotes.Count, result.HotMoments.Count);

            return result;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[QuoteMiner] Failed to mine quotes");
            return new MinedQuotes();
        }
    }

    private MinedQuotes ParseResponse(string content)
    {
        var cleaned = CleanJsonResponse(content);

        try
        {
            var json = JsonDocument.Parse(cleaned);
            var root = json.RootElement;

            var result = new MinedQuotes();

            // Parse quotes
            if (root.TryGetProperty("quotes", out var quotesEl) && quotesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var q in quotesEl.EnumerateArray())
                {
                    var text = q.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                    var author = q.TryGetProperty("author", out var a) ? a.GetString() ?? "" : "";

                    // Skip empty quotes
                    if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(author))
                        continue;

                    result.BestQuotes.Add(new MemoableQuote
                    {
                        Text = text,
                        Author = author,
                        Context = q.TryGetProperty("context", out var ctx) && ctx.ValueKind == JsonValueKind.String
                            ? ctx.GetString()
                            : null,
                        Category = q.TryGetProperty("category", out var cat)
                            ? cat.GetString() ?? "funny"
                            : "funny"
                    });
                }
            }

            // Parse hot moments
            if (root.TryGetProperty("hot_moments", out var momentsEl) && momentsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in momentsEl.EnumerateArray())
                {
                    var description = m.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";

                    // Skip empty moments
                    if (string.IsNullOrWhiteSpace(description))
                        continue;

                    result.HotMoments.Add(new HotMoment
                    {
                        Time = m.TryGetProperty("time", out var t) && t.ValueKind == JsonValueKind.String
                            ? t.GetString()
                            : null,
                        Description = description,
                        Participants = ParseStringArray(m, "participants"),
                        MessageBurst = 0 // Will be set by ThreadDetector if needed
                    });
                }
            }

            return result;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "[QuoteMiner] Failed to parse JSON: {Content}", cleaned.Substring(0, Math.Min(200, cleaned.Length)));
            return new MinedQuotes();
        }
    }

    private static List<string> ParseStringArray(JsonElement element, string propertyName)
    {
        var result = new List<string>();

        if (element.TryGetProperty(propertyName, out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var value = item.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        result.Add(value);
                    }
                }
            }
        }

        return result;
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
            cleaned = cleaned.Substring(start, end - start + 1);
        }

        return cleaned;
    }
}
