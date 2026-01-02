namespace WatchmenBot.Features.Indexing.Services;

/// <summary>
/// Interface for embedding indexing handlers.
/// Each handler is responsible for a specific type of embedding (message, context, etc.)
/// </summary>
public interface IEmbeddingHandler
{
    /// <summary>
    /// Name of this handler (for logging and metrics)
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Check if this handler is enabled in configuration
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Get current indexing statistics
    /// </summary>
    Task<IndexingStats> GetStatsAsync(CancellationToken ct = default);

    /// <summary>
    /// Process a batch of items that need indexing
    /// </summary>
    /// <param name="batchSize">Maximum items to process in this batch</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Result with number of items processed</returns>
    Task<IndexingResult> ProcessBatchAsync(int batchSize, CancellationToken ct = default);
}

/// <summary>
/// Statistics about indexing progress
/// </summary>
public record IndexingStats(
    long Total,
    long Indexed,
    long Pending);

/// <summary>
/// Result of a batch processing operation
/// </summary>
public record IndexingResult(
    int ProcessedCount,
    TimeSpan ElapsedTime,
    bool HasMoreWork);