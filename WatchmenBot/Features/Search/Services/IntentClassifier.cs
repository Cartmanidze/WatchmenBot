using System.Text.Json;
using System.Text.RegularExpressions;
using WatchmenBot.Features.Search.Models;
using WatchmenBot.Features.Llm.Services;

namespace WatchmenBot.Features.Search.Services;

/// <summary>
/// LLM-based intent classification for /ask command.
/// Replaces simple pattern matching with rich understanding of user questions.
/// </summary>
public partial class IntentClassifier(
    LlmRouter llmRouter,
    ILogger<IntentClassifier> logger)
{
    private const string SystemPrompt = """
        Анализируй вопрос пользователя к боту чата. Отвечай ТОЛЬКО JSON без markdown:

        {
          "intent": "personal_self|personal_other|factual|event|temporal|comparison|multi_entity",
          "confidence": 0.95,
          "entities": [
            {"type": "person|topic|object", "text": "значение", "mentioned_as": "@имя или null"}
          ],
          "temporal": {
            "detected": true,
            "text": "вчера",
            "type": "relative|absolute",
            "relative_days": -1
          },
          "mentioned_people": ["Глеб", "@vasia"],
          "reasoning": "краткое объяснение классификации"
        }

        ПРАВИЛА КЛАССИФИКАЦИИ:
        - personal_self: вопрос о себе ("я", "какой я", "обо мне", "что я")
        - personal_other: вопрос о конкретном человеке ("что за тип Глеб", "@vasia как?", "кто такой X")
        - factual: общий вопрос о теме ("что такое X", "как сделать Y", "почему Z")
        - event: о событии в чате ("что произошло", "кто выиграл", "что случилось")
        - temporal: явная привязка ко времени ("вчера", "на прошлой неделе", "сегодня утром")
        - comparison: сравнение ("кто лучше X или Y", "X vs Y", "что круче")
        - multi_entity: несколько сущностей без сравнения ("X, Y и Z говорили", "между X и Y")

        ВРЕМЕННЫЕ МАРКЕРЫ (relative_days):
        - "сегодня" → 0
        - "вчера" → -1
        - "позавчера" → -2
        - "на этой неделе" → -3
        - "на прошлой неделе" → -7
        - "в этом месяце" → -15
        - "месяц назад" → -30
        - "давно", "раньше" → -90

        ВАЖНО:
        - Если temporal маркер есть, установи temporal.detected = true
        - Вопрос может иметь и temporal, и personal_other одновременно — выбирай более специфичный intent
        - Если упоминаются люди, добавь их в mentioned_people
        - confidence: 0.9+ для явных случаев, 0.5-0.9 для неявных, <0.5 для неуверенных
        """;

    /// <summary>
    /// Classify user question using LLM
    /// </summary>
    public async Task<ClassifiedQuery> ClassifyAsync(
        string question,
        string askerName,
        string? askerUsername,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            return CreateFallback(question);
        }

        try
        {
            var response = await llmRouter.CompleteAsync(new LlmRequest
            {
                SystemPrompt = SystemPrompt,
                UserPrompt = $"Вопрос: {question}",
                Temperature = 0.1 // Low temperature for factual classification
            }, ct);

            var result = ParseResponse(response.Content, question);

            logger.LogInformation(
                "[IntentClassifier] Question: '{Question}' → Intent: {Intent}, Confidence: {Conf:F2}, Entities: {Entities}, Temporal: {Temporal}",
                question.Length > 50 ? question[..50] + "..." : question,
                result.Intent,
                result.Confidence,
                result.Entities.Count,
                result.HasTemporal ? result.TemporalRef?.Text : "none");

            return result;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[IntentClassifier] LLM classification failed, using fallback");
            return CreateFallback(question);
        }
    }

    private ClassifiedQuery ParseResponse(string content, string originalQuestion)
    {
        var cleaned = CleanJsonResponse(content);

        try
        {
            var json = JsonDocument.Parse(cleaned);
            var root = json.RootElement;

            var result = new ClassifiedQuery
            {
                OriginalQuestion = originalQuestion,
                Intent = ParseIntent(root),
                Confidence = root.TryGetProperty("confidence", out var conf) && conf.ValueKind == JsonValueKind.Number
                    ? conf.GetDouble()
                    : 0.5,
                Reasoning = root.TryGetProperty("reasoning", out var reason) && reason.ValueKind == JsonValueKind.String
                    ? reason.GetString()
                    : null,
                MentionedPeople = ParseStringArray(root, "mentioned_people"),
                Entities = ParseEntities(root),
                TemporalRef = ParseTemporal(root)
            };

            return result;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "[IntentClassifier] Failed to parse JSON: {Content}",
                cleaned.Length > 200 ? cleaned[..200] : cleaned);
            return new ClassifiedQuery
            {
                OriginalQuestion = originalQuestion,
                Intent = QueryIntent.Factual,
                Confidence = 0.3
            };
        }
    }

    private static QueryIntent ParseIntent(JsonElement root)
    {
        if (!root.TryGetProperty("intent", out var intentEl) || intentEl.ValueKind != JsonValueKind.String)
            return QueryIntent.Factual;

        var intentStr = intentEl.GetString()?.ToLowerInvariant();

        return intentStr switch
        {
            "personal_self" => QueryIntent.PersonalSelf,
            "personal_other" => QueryIntent.PersonalOther,
            "factual" => QueryIntent.Factual,
            "event" => QueryIntent.Event,
            "temporal" => QueryIntent.Temporal,
            "comparison" => QueryIntent.Comparison,
            "multi_entity" => QueryIntent.MultiEntity,
            _ => QueryIntent.Factual
        };
    }

    private static List<ExtractedEntity> ParseEntities(JsonElement root)
    {
        var result = new List<ExtractedEntity>();

        if (!root.TryGetProperty("entities", out var entitiesEl) || entitiesEl.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var e in entitiesEl.EnumerateArray())
        {
            var typeStr = e.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()?.ToLowerInvariant()
                : "topic";

            var text = e.TryGetProperty("text", out var txt) && txt.ValueKind == JsonValueKind.String
                ? txt.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(text))
                continue;

            result.Add(new ExtractedEntity
            {
                Type = typeStr switch
                {
                    "person" => EntityType.Person,
                    "topic" => EntityType.Topic,
                    "object" => EntityType.Object,
                    _ => EntityType.Topic
                },
                Text = text,
                MentionedAs = e.TryGetProperty("mentioned_as", out var m) && m.ValueKind == JsonValueKind.String
                    ? m.GetString()
                    : null
            });
        }

        return result;
    }

    private static TemporalReference? ParseTemporal(JsonElement root)
    {
        if (!root.TryGetProperty("temporal", out var temporalEl) || temporalEl.ValueKind != JsonValueKind.Object)
            return null;

        var detected = temporalEl.TryGetProperty("detected", out var det) &&
                      det.ValueKind == JsonValueKind.True;

        if (!detected)
            return new TemporalReference { Detected = false };

        var typeStr = temporalEl.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString()?.ToLowerInvariant()
            : "relative";

        return new TemporalReference
        {
            Detected = true,
            Text = temporalEl.TryGetProperty("text", out var txt) && txt.ValueKind == JsonValueKind.String
                ? txt.GetString()
                : null,
            Type = typeStr == "absolute" ? TemporalType.Absolute : TemporalType.Relative,
            RelativeDays = temporalEl.TryGetProperty("relative_days", out var days) && days.ValueKind == JsonValueKind.Number
                ? days.GetInt32()
                : null
        };
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
            cleaned = cleaned[start..(end + 1)];
        }

        return cleaned;
    }

    /// <summary>
    /// Create fallback classification using simple pattern matching
    /// (reuses PersonalQuestionDetector logic)
    /// </summary>
    private ClassifiedQuery CreateFallback(string question)
    {
        var q = question.ToLowerInvariant().Trim();

        // Self patterns
        var selfPatterns = new[]
        {
            "я ", "кто я", "какой я", "какая я", "что я", "как я",
            "обо мне", "про меня", "меня ", "мне ", "мной "
        };

        if (selfPatterns.Any(p => q.Contains(p)))
        {
            logger.LogDebug("[IntentClassifier] Fallback: detected self pattern");
            return new ClassifiedQuery
            {
                OriginalQuestion = question,
                Intent = QueryIntent.PersonalSelf,
                Confidence = 0.4,
                Reasoning = "Fallback: self pattern detected"
            };
        }

        // @username patterns
        var usernameMatch = MyRegex().Match(question);
        if (usernameMatch.Success)
        {
            var username = usernameMatch.Groups[1].Value;
            logger.LogDebug("[IntentClassifier] Fallback: detected @{Username}", username);
            return new ClassifiedQuery
            {
                OriginalQuestion = question,
                Intent = QueryIntent.PersonalOther,
                Confidence = 0.4,
                MentionedPeople = [username],
                Entities = [new ExtractedEntity { Type = EntityType.Person, Text = username, MentionedAs = $"@{username}" }],
                Reasoning = "Fallback: @username pattern detected"
            };
        }

        // Simple temporal patterns
        var temporalPatterns = new Dictionary<string, int>
        {
            { "сегодня", 0 },
            { "вчера", -1 },
            { "позавчера", -2 },
            { "на этой неделе", -3 },
            { "на прошлой неделе", -7 }
        };

        foreach (var (pattern, days) in temporalPatterns)
        {
            if (q.Contains(pattern))
            {
                logger.LogDebug("[IntentClassifier] Fallback: detected temporal pattern '{Pattern}'", pattern);
                return new ClassifiedQuery
                {
                    OriginalQuestion = question,
                    Intent = QueryIntent.Temporal,
                    Confidence = 0.4,
                    TemporalRef = new TemporalReference
                    {
                        Detected = true,
                        Text = pattern,
                        Type = TemporalType.Relative,
                        RelativeDays = days
                    },
                    Reasoning = $"Fallback: temporal pattern '{pattern}' detected"
                };
            }
        }

        // Default to factual
        logger.LogDebug("[IntentClassifier] Fallback: no pattern matched, defaulting to Factual");
        return new ClassifiedQuery
        {
            OriginalQuestion = question,
            Intent = QueryIntent.Factual,
            Confidence = 0.3,
            Reasoning = "Fallback: no specific pattern detected"
        };
    }

    [GeneratedRegex(@"@(\w+)")]
    private static partial Regex MyRegex();
}
