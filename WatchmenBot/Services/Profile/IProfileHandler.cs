namespace WatchmenBot.Services.Profile;

/// <summary>
/// Interface for profile processing handlers.
/// Each handler is responsible for a specific stage of the profile pipeline.
/// </summary>
public interface IProfileHandler
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
    /// Get current processing statistics
    /// </summary>
    Task<ProfileStats> GetStatsAsync(CancellationToken ct = default);

    /// <summary>
    /// Process items for this handler
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Result with number of items processed</returns>
    Task<ProfileResult> ProcessAsync(CancellationToken ct = default);
}

/// <summary>
/// Statistics about profile processing
/// </summary>
public record ProfileStats(
    long TotalItems,
    long ProcessedItems,
    long PendingItems);

/// <summary>
/// Result of a profile processing operation
/// </summary>
public record ProfileResult(
    int ProcessedCount,
    TimeSpan ElapsedTime,
    bool HasMoreWork);
