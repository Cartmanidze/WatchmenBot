using System.Diagnostics;

namespace WatchmenBot.Services.Indexing;

/// <summary>
/// Processes batches with rate limiting and error handling.
/// Reusable component for any batch processing scenario.
/// </summary>
public class BatchProcessor
{
    private readonly IndexingOptions _options;
    private readonly IndexingMetrics _metrics;
    private readonly ILogger<BatchProcessor> _logger;

    public BatchProcessor(
        IndexingOptions options,
        IndexingMetrics metrics,
        ILogger<BatchProcessor> logger)
    {
        _options = options;
        _metrics = metrics;
        _logger = logger;
    }

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
            _logger.LogDebug("[BatchProcessor] Handler {Handler} is disabled, skipping", handler.Name);
            return false;
        }

        // Get current stats
        var stats = await handler.GetStatsAsync(ct);
        if (stats.Pending == 0)
        {
            _logger.LogDebug("[BatchProcessor] Handler {Handler}: no pending work ({Indexed}/{Total})",
                handler.Name, stats.Indexed, stats.Total);
            return false;
        }

        _logger.LogInformation("[BatchProcessor] Handler {Handler}: starting run with {Pending} pending items ({Indexed}/{Total} already indexed)",
            handler.Name, stats.Pending, stats.Indexed, stats.Total);

        var totalProcessed = 0;
        var batchesProcessed = 0;
        var sw = Stopwatch.StartNew();

        while (batchesProcessed < _options.MaxBatchesPerRun && !ct.IsCancellationRequested)
        {
            try
            {
                var result = await handler.ProcessBatchAsync(_options.BatchSize, ct);

                if (result.ProcessedCount == 0)
                {
                    // No more work available
                    break;
                }

                // Record metrics
                _metrics.RecordBatch(handler.Name, result.ProcessedCount, result.ElapsedTime);

                totalProcessed += result.ProcessedCount;
                batchesProcessed++;

                var progress = (double)totalProcessed / stats.Pending * 100;
                _logger.LogInformation("[BatchProcessor] Handler {Handler}: Batch {Batch}: +{Count} items in {Ms}ms | Progress: {Done}/{Pending} ({Percent:F1}%)",
                    handler.Name,
                    batchesProcessed,
                    result.ProcessedCount,
                    result.ElapsedTime.TotalMilliseconds,
                    totalProcessed,
                    stats.Pending,
                    progress);

                // Rate limiting: delay between batches
                if (batchesProcessed < _options.MaxBatchesPerRun && result.HasMoreWork)
                {
                    await Task.Delay(TimeSpan.FromSeconds(_options.DelayBetweenBatchesSeconds), ct);
                }

                // If handler says no more work, exit early
                if (!result.HasMoreWork)
                {
                    break;
                }
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                _metrics.RecordError(handler.Name, "rate_limit");
                _logger.LogWarning("[BatchProcessor] Handler {Handler}: Rate limited! Waiting {Delay}s before retry...",
                    handler.Name, _options.RateLimitRetryDelaySeconds);
                await Task.Delay(TimeSpan.FromSeconds(_options.RateLimitRetryDelaySeconds), ct);
                // Don't increment batch counter, will retry same batch
            }
            catch (Exception ex)
            {
                _metrics.RecordError(handler.Name, ex.GetType().Name);
                _logger.LogError(ex, "[BatchProcessor] Handler {Handler}: Error processing batch {Batch}",
                    handler.Name, batchesProcessed + 1);

                // Don't continue on error, let orchestrator decide what to do
                throw;
            }
        }

        sw.Stop();
        if (totalProcessed > 0)
        {
            var rate = totalProcessed / sw.Elapsed.TotalSeconds;
            _logger.LogInformation("[BatchProcessor] Handler {Handler}: Run complete - {Total} items in {Batches} batches, {Elapsed:F1}s ({Rate:F1} items/s)",
                handler.Name, totalProcessed, batchesProcessed, sw.Elapsed.TotalSeconds, rate);
        }

        // Return true if there's potentially more work
        var hasMore = batchesProcessed >= _options.MaxBatchesPerRun || (stats.Pending - totalProcessed) > 0;
        return hasMore;
    }
}