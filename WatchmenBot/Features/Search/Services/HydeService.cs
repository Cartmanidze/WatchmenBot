using WatchmenBot.Features.Llm.Services;

namespace WatchmenBot.Features.Search.Services;

/// <summary>
/// HyDE (Hypothetical Document Embeddings) service.
/// Generates hypothetical answers to questions for better semantic search.
///
/// Instead of searching by question embedding (which is in "question space"),
/// we generate a hypothetical answer and search by that (which is in "answer space").
/// This dramatically improves question→answer retrieval.
///
/// Paper: https://arxiv.org/abs/2212.10496
/// </summary>
public class HydeService(
    LlmRouter llmRouter,
    ILogger<HydeService> logger)
{
    private const string SystemPrompt = """
        Ты — помощник в чате. Напиши ГИПОТЕТИЧЕСКИЙ ОТВЕТ на вопрос пользователя.

        ПРАВИЛА:
        1. Отвечай так, как будто ты участник чата и ЗНАЕШЬ ответ
        2. Пиши коротко (1-3 предложения), как в мессенджере
        3. Используй неформальный стиль (как в чате с друзьями)
        4. НЕ говори "я не знаю" или "мне нужно больше информации"
        5. Придумай правдоподобный ответ, даже если не уверен
        6. Если вопрос о боте — отвечай от лица бота
        7. Если вопрос о человеке — отвечай как будто знаешь этого человека

        ПРИМЕРЫ:

        Вопрос: "для чего ты создан?"
        Ответ: "я создан чтобы помогать отвечать на вопросы и искать инфу в истории чата"

        Вопрос: "кто тут самый умный?"
        Ответ: "ну Вася конечно самый умный, он всегда всё знает и помогает разобраться"

        Вопрос: "что вчера обсуждали?"
        Ответ: "вчера обсуждали новый проект, Петя предложил использовать React, а Маша была против"

        Вопрос: "ты разочарован своей целью?"
        Ответ: "не, я не разочарован, моя цель — помогать вам, и мне это нравится"

        Отвечай ТОЛЬКО текст ответа, без пояснений.
        """;

    /// <summary>
    /// Generate a hypothetical answer to a question.
    /// This answer will be used for semantic search instead of the question itself.
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
                Temperature = 0.7 // Some creativity for diverse hypothetical answers
            }, ct);

            var hypotheticalAnswer = response.Content.Trim();

            sw.Stop();

            logger.LogInformation(
                "[HyDE] Question: '{Question}' → Hypothetical: '{Hypo}' ({Ms}ms)",
                question.Length > 40 ? question[..40] + "..." : question,
                hypotheticalAnswer.Length > 50 ? hypotheticalAnswer[..50] + "..." : hypotheticalAnswer,
                sw.ElapsedMilliseconds);

            return new HydeResult
            {
                OriginalQuestion = question,
                HypotheticalAnswer = hypotheticalAnswer,
                GenerationTimeMs = sw.ElapsedMilliseconds,
                Success = true
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            logger.LogWarning(ex, "[HyDE] Failed to generate hypothetical answer for: {Question}", question);

            return new HydeResult
            {
                OriginalQuestion = question,
                HypotheticalAnswer = null,
                GenerationTimeMs = sw.ElapsedMilliseconds,
                Success = false,
                Error = ex.Message
            };
        }
    }
}

/// <summary>
/// Result of HyDE generation
/// </summary>
public class HydeResult
{
    public required string OriginalQuestion { get; init; }
    public string? HypotheticalAnswer { get; init; }
    public long GenerationTimeMs { get; init; }
    public bool Success { get; init; }
    public string? Error { get; init; }
}
