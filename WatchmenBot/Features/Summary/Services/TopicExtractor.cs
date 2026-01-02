using System.Text;
using System.Text.Json;
using WatchmenBot.Features.Search.Models;
using WatchmenBot.Features.Llm.Services;

namespace WatchmenBot.Features.Summary.Services;

/// <summary>
/// Extracts main topics from chat messages using LLM
/// </summary>
public class TopicExtractor(
    LlmRouter llmRouter,
    ILogger<TopicExtractor> logger)
{
    private const string SystemPrompt = """
        Ты анализируешь сообщения из чата.
        Твоя задача — выделить 3-7 основных тем/топиков обсуждения.

        Отвечай ТОЛЬКО JSON массивом строк, без markdown, без пояснений.
        Пример: ["Работа и дедлайны", "Политика", "Мемы и шутки", "Технические вопросы"]

        Темы должны быть:
        - Конкретными (не "разное")
        - На русском языке
        - Короткими (2-4 слова)
        """;

    /// <summary>
    /// Extract main topics from diverse messages sample
    /// </summary>
    public async Task<List<string>> ExtractTopicsAsync(
        List<SearchResult> messages,
        int sampleSize = 50,
        CancellationToken ct = default)
    {
        var sampleText = new StringBuilder();
        foreach (var msg in messages.Take(sampleSize))
        {
            sampleText.AppendLine(msg.ChunkText);
        }

        var userPrompt = $"Сообщения:\n{sampleText}\n\nВыдели основные темы:";

        try
        {
            var response = await llmRouter.CompleteAsync(new LlmRequest
            {
                SystemPrompt = SystemPrompt,
                UserPrompt = userPrompt,
                Temperature = 0.3
            }, ct);

            // Parse JSON array
            var cleaned = response.Content.Trim();
            if (cleaned.StartsWith("```"))
            {
                cleaned = cleaned.Split('\n').Skip(1).TakeWhile(l => !l.StartsWith("```")).Aggregate((a, b) => a + b);
            }

            var topics = JsonSerializer.Deserialize<List<string>>(cleaned);

            logger.LogInformation("[TopicExtractor] Extracted {Count} topics: {Topics}",
                topics?.Count ?? 0, topics != null ? string.Join(", ", topics) : "none");

            return topics ?? [];
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[TopicExtractor] Failed to extract topics");
            return [];
        }
    }
}
