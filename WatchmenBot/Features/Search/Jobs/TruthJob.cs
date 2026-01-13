using Hangfire;
using WatchmenBot.Features.Search.Services;

namespace WatchmenBot.Features.Search.Jobs;

/// <summary>
/// Hangfire job for /truth fact-checking command.
/// Thin wrapper — all logic is in TruthProcessingService.
/// </summary>
public class TruthJob(TruthProcessingService processingService, ILogger<TruthJob> logger)
{
    [AutomaticRetry(Attempts = 3, DelaysInSeconds = [30, 60, 300])]
    [Queue("default")]
    public async Task ProcessAsync(TruthQueueItem item, CancellationToken ct)
    {
        logger.LogInformation("[TruthJob] Starting fact-check for chat {ChatId}, {Count} messages",
            item.ChatId, item.MessageCount);

        var result = await processingService.ProcessAsync(item, ct);

        logger.LogInformation("[TruthJob] Completed fact-check in {Elapsed:F1}s, {Count} messages",
            result.ElapsedSeconds, result.MessageCount);
    }
}
