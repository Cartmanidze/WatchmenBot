using Hangfire;
using WatchmenBot.Features.Search.Services;

namespace WatchmenBot.Features.Search.Jobs;

/// <summary>
/// Background job for Q→A semantic bridge generation.
/// Generates hypothetical questions for messages to improve search relevance.
/// Runs asynchronously via Hangfire to avoid blocking message processing.
/// </summary>
public class QuestionGenerationJob(
    QuestionGenerationService questionGenerator,
    EmbeddingClient embeddingClient,
    EmbeddingStorageService storageService,
    ILogger<QuestionGenerationJob> logger)
{
    /// <summary>
    /// Process a single message: generate questions and store their embeddings.
    /// </summary>
    [AutomaticRetry(Attempts = 2, DelaysInSeconds = [10, 30])]
    [Queue("low")] // Low priority - doesn't affect user experience
    public async Task ProcessAsync(QuestionGenerationItem item, CancellationToken ct)
    {
        // Skip if message doesn't qualify for question generation
        if (!questionGenerator.ShouldGenerateQuestions(item.MessageText, item.IsForwarded))
        {
            logger.LogDebug("[QuestionGenJob] Skipped message {MsgId}: doesn't qualify", item.MessageId);
            return;
        }

        try
        {
            // Generate questions using LLM
            var questions = await questionGenerator.GenerateQuestionsAsync(item.MessageText, ct);

            if (questions.Count == 0)
            {
                logger.LogDebug("[QuestionGenJob] No questions generated for message {MsgId}", item.MessageId);
                return;
            }

            // Get embeddings for all questions at once
            var embeddings = await embeddingClient.GetEmbeddingsAsync(questions, ct);

            // Store each question embedding
            var storedCount = 0;
            var skippedCount = 0;
            for (var i = 0; i < questions.Count && i < embeddings.Count; i++)
            {
                if (embeddings[i].Length == 0)
                {
                    skippedCount++;
                    logger.LogWarning(
                        "[QuestionGenJob] Empty embedding for question {Index} of message {MsgId}",
                        i, item.MessageId);
                    continue;
                }

                await storageService.StoreQuestionEmbeddingAsync(
                    item.ChatId,
                    item.MessageId,
                    questionIndex: i,
                    questionText: questions[i],
                    embedding: embeddings[i],
                    ct);

                storedCount++;
            }

            logger.LogInformation(
                "[QuestionGenJob] Stored {Stored}/{Total} questions for message {MsgId} in chat {ChatId}",
                storedCount, questions.Count, item.MessageId, item.ChatId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "[QuestionGenJob] Failed for message {MsgId} in chat {ChatId}: {Error}",
                item.MessageId, item.ChatId, ex.Message);
            throw; // Let Hangfire retry
        }
    }
}

/// <summary>
/// Data for question generation job.
/// </summary>
public record QuestionGenerationItem
{
    public required long ChatId { get; init; }
    public required long MessageId { get; init; }
    public required string MessageText { get; init; }
    public bool IsForwarded { get; init; }
}
