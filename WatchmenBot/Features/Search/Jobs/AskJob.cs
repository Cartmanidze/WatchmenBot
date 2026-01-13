using Hangfire;
using WatchmenBot.Features.Search.Services;

namespace WatchmenBot.Features.Search.Jobs;

/// <summary>
/// Hangfire job for /ask and /smart commands.
/// Thin wrapper — all logic is in AskProcessingService.
/// </summary>
public class AskJob(AskProcessingService processingService, ILogger<AskJob> logger)
{
    [AutomaticRetry(Attempts = 3, DelaysInSeconds = [30, 60, 300])]
    [Queue("default")]
    public async Task ProcessAsync(AskQueueItem item, CancellationToken ct)
    {
        logger.LogInformation("[AskJob] Starting /{Command} for chat {ChatId}", item.Command, item.ChatId);

        var result = await processingService.ProcessAsync(item, ct);

        logger.LogInformation("[AskJob] Completed /{Command} in {Elapsed:F1}s, success: {Success}",
            item.Command, result.ElapsedSeconds, result.Success);
    }
}