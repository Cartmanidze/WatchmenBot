using System.Text.Json;
using WatchmenBot.Features.Summary.Models;
using WatchmenBot.Features.Llm.Services;

namespace WatchmenBot.Features.Summary.Services;

/// <summary>
/// Extracts key events, decisions, and open questions from chat messages using LLM.
/// Identifies what happened, what was decided, and what remains unresolved.
/// </summary>
public class EventDetector(
    LlmRouter llmRouter,
    ILogger<EventDetector> logger)
{
    private const string SystemPrompt = """
        Ты анализируешь переписку чата. Твоя задача — найти:

        1. КЛЮЧЕВЫЕ СОБЫТИЯ — что важного произошло (проблемы, решения, достижения)
        2. РЕШЕНИЯ — о чём договорились, что постановили
        3. ОТКРЫТЫЕ ВОПРОСЫ — что осталось нерешённым, на что не ответили

        Отвечай ТОЛЬКО JSON без markdown:
        {
          "events": [
            {"time": "14:23", "description": "краткое описание", "participants": ["имя1", "имя2"], "importance": "critical|notable|minor"}
          ],
          "decisions": [
            {"what": "что решили", "who": "кто предложил/решил", "when": "когда"}
          ],
          "open_questions": [
            {"question": "вопрос", "context": "контекст вопроса"}
          ]
        }

        Правила:
        - importance: "critical" = срочно/важно, "notable" = интересно, "minor" = мелочь
        - Если время неизвестно — пиши null
        - Максимум 5 событий, 5 решений, 3 вопроса
        - Пиши кратко и по делу
        - Если ничего важного нет — возвращай пустые массивы
        """;

    /// <summary>
    /// Extract events, decisions, and open questions from chat context
    /// </summary>
    public async Task<ExtractedEvents> ExtractEventsAsync(
        string context,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(context))
        {
            return new ExtractedEvents();
        }

        var userPrompt = $"Переписка:\n{context}\n\nНайди события, решения и открытые вопросы:";

        try
        {
            var response = await llmRouter.CompleteAsync(new LlmRequest
            {
                SystemPrompt = SystemPrompt,
                UserPrompt = userPrompt,
                Temperature = 0.1 // Low temperature for factual extraction
            }, ct);

            var result = ParseResponse(response.Content);

            logger.LogInformation(
                "[EventDetector] Extracted {Events} events, {Decisions} decisions, {Questions} open questions",
                result.Events.Count, result.Decisions.Count, result.OpenQuestions.Count);

            return result;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[EventDetector] Failed to extract events");
            return new ExtractedEvents();
        }
    }

    private ExtractedEvents ParseResponse(string content)
    {
        var cleaned = CleanJsonResponse(content);

        try
        {
            var json = JsonDocument.Parse(cleaned);
            var root = json.RootElement;

            var result = new ExtractedEvents();

            // Parse events
            if (root.TryGetProperty("events", out var eventsEl) && eventsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in eventsEl.EnumerateArray())
                {
                    result.Events.Add(new KeyEvent
                    {
                        Time = e.TryGetProperty("time", out var t) && t.ValueKind == JsonValueKind.String
                            ? t.GetString()
                            : null,
                        Description = e.TryGetProperty("description", out var d)
                            ? d.GetString() ?? ""
                            : "",
                        Participants = ParseStringArray(e, "participants"),
                        Importance = e.TryGetProperty("importance", out var i)
                            ? i.GetString() ?? "notable"
                            : "notable"
                    });
                }
            }

            // Parse decisions
            if (root.TryGetProperty("decisions", out var decisionsEl) && decisionsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var d in decisionsEl.EnumerateArray())
                {
                    result.Decisions.Add(new Decision
                    {
                        What = d.TryGetProperty("what", out var w)
                            ? w.GetString() ?? ""
                            : "",
                        Who = d.TryGetProperty("who", out var wh) && wh.ValueKind == JsonValueKind.String
                            ? wh.GetString()
                            : null,
                        When = d.TryGetProperty("when", out var wn) && wn.ValueKind == JsonValueKind.String
                            ? wn.GetString()
                            : null
                    });
                }
            }

            // Parse open questions
            if (root.TryGetProperty("open_questions", out var questionsEl) && questionsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var q in questionsEl.EnumerateArray())
                {
                    result.OpenQuestions.Add(new OpenQuestion
                    {
                        Question = q.TryGetProperty("question", out var qText)
                            ? qText.GetString() ?? ""
                            : "",
                        Context = q.TryGetProperty("context", out var ctx) && ctx.ValueKind == JsonValueKind.String
                            ? ctx.GetString()
                            : null
                    });
                }
            }

            return result;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "[EventDetector] Failed to parse JSON: {Content}", cleaned.Substring(0, Math.Min(200, cleaned.Length)));
            return new ExtractedEvents();
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
