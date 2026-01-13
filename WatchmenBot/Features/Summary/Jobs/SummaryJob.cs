using Hangfire;
using WatchmenBot.Features.Summary.Services;

namespace WatchmenBot.Features.Summary.Jobs;

/// <summary>
/// Hangfire job for /summary command.
/// Thin wrapper — all logic is in SummaryProcessingService.
/// </summary>
public class SummaryJob(SummaryProcessingService processingService, ILogger<SummaryJob> logger)
{
    [AutomaticRetry(Attempts = 3, DelaysInSeconds = [30, 60, 300])]
    [Queue("default")]
    public async Task ProcessAsync(SummaryQueueItem item, CancellationToken ct)
    {
        logger.LogInformation("[SummaryJob] Starting summary for chat {ChatId}, {Hours}h", item.ChatId, item.Hours);

        var result = await processingService.ProcessAsync(item, ct);

        logger.LogInformation("[SummaryJob] Completed summary in {Elapsed:F1}s, {Count} messages",
            result.ElapsedSeconds, result.MessageCount);
    }
}
