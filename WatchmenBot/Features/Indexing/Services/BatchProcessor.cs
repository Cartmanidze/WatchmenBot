using System.Diagnostics;

namespace WatchmenBot.Features.Indexing.Services;

/// <summary>
/// Processes batches with rate limiting and error handling.
/// Reusable component for any batch processing scenario.
/// </summary>
public class BatchProcessor(
    IndexingOptions options,
    IndexingMetrics metrics,
    ILogger<BatchProcessor> logger)
{
    /// <summary>
    /// Process batches using the provided handler until max batches reached or no more work
    /// </summary>
    /// <param name="handler">Handler to process batches</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if more work is available</returns>
    public async Task<bool> ProcessBatchesAsync(IEmbeddingHandler handler, CancellationToken ct)
    {
        if (!handler.IsEnabled)
        {
            logger.LogDebug("[BatchProcessor] Handler {Handler} is disabled, skipping", handler.Name);
            return false;
        }

        // Get current stats
        var stats = await handler.GetStatsAsync(ct);
        if (stats.Pending == 0)
        {
            logger.LogDebug("[BatchProcessor] Handler {Handler}: no pending work ({Indexed}/{Total})",
                handler.Name, stats.Indexed, stats.Total);
            return false;
        }

        logger.LogInformation("[BatchProcessor] Handler {Handler}: starting run with {Pending} pending items ({Indexed}/{Total} already indexed)",
            handler.Name, stats.Pending, stats.Indexed, stats.Total);

        var totalProcessed = 0;
        var batchesProcessed = 0;
        var sw = Stopwatch.StartNew();

        while (batchesProcessed < options.MaxBatchesPerRun && !ct.IsCancellationRequested)
        {
            try
            {
                var result = await handler.ProcessBatchAsync(options.BatchSize, ct);

                if (result.ProcessedCount == 0)
                {
                    // No more work available
                    break;
                }

                // Record metrics
                metrics.RecordBatch(handler.Name, result.ProcessedCount, result.ElapsedTime);

                totalProcessed += result.ProcessedCount;
                batchesProcessed++;

                var progress = (double)totalProcessed / stats.Pending * 100;
                logger.LogInformation("[BatchProcessor] Handler {Handler}: Batch {Batch}: +{Count} items in {Ms}ms | Progress: {Done}/{Pending} ({Percent:F1}%)",
                    handler.Name,
                    batchesProcessed,
                    result.ProcessedCount,
                    result.ElapsedTime.TotalMilliseconds,
                    totalProcessed,
                    stats.Pending,
                    progress);

                // Rate limiting: delay between batches
                if (batchesProcessed < options.MaxBatchesPerRun && result.HasMoreWork)
                {
                    await Task.Delay(TimeSpan.FromSeconds(options.DelayBetweenBatchesSeconds), ct);
                }

                // If handler says no more work, exit early
                if (!result.HasMoreWork)
                {
                    break;
                }
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                metrics.RecordError(handler.Name, "rate_limit");
                logger.LogWarning("[BatchProcessor] Handler {Handler}: Rate limited! Waiting {Delay}s before retry...",
                    handler.Name, options.RateLimitRetryDelaySeconds);
                await Task.Delay(TimeSpan.FromSeconds(options.RateLimitRetryDelaySeconds), ct);
                // Don't increment batch counter, will retry same batch
            }
            catch (Exception ex)
            {
                metrics.RecordError(handler.Name, ex.GetType().Name);
                logger.LogError(ex, "[BatchProcessor] Handler {Handler}: Error processing batch {Batch}",
                    handler.Name, batchesProcessed + 1);

                // Don't continue on error, let orchestrator decide what to do
                throw;
            }
        }

        sw.Stop();
        if (totalProcessed > 0)
        {
            var rate = totalProcessed / sw.Elapsed.TotalSeconds;
            logger.LogInformation("[BatchProcessor] Handler {Handler}: Run complete - {Total} items in {Batches} batches, {Elapsed:F1}s ({Rate:F1} items/s)",
                handler.Name, totalProcessed, batchesProcessed, sw.Elapsed.TotalSeconds, rate);
        }

        // Return true if there's potentially more work
        var hasMore = batchesProcessed >= options.MaxBatchesPerRun || (stats.Pending - totalProcessed) > 0;
        return hasMore;
    }
}