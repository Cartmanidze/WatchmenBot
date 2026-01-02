namespace WatchmenBot.Features.Indexing.Services;

/// <summary>
/// Configuration options for background indexing service
/// </summary>
public class IndexingOptions
{
    /// <summary>
    /// Number of items to process in a single batch
    /// </summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>
    /// Delay between batches in seconds (for rate limiting)
    /// </summary>
    public int DelayBetweenBatchesSeconds { get; set; } = 2;

    /// <summary>
    /// Maximum batches to process in a single run
    /// </summary>
    public int MaxBatchesPerRun { get; set; } = 500;

    /// <summary>
    /// Interval in minutes when no work is available (idle state)
    /// </summary>
    public int IdleIntervalMinutes { get; set; } = 2;

    /// <summary>
    /// Delay in seconds when work is available but run completed
    /// </summary>
    public int ActiveIntervalSeconds { get; set; } = 5;

    /// <summary>
    /// Delay in seconds after rate limit error
    /// </summary>
    public int RateLimitRetryDelaySeconds { get; set; } = 60;

    /// <summary>
    /// Delay in seconds after general error
    /// </summary>
    public int ErrorRetryDelaySeconds { get; set; } = 60;

    /// <summary>
    /// Startup delay in seconds to wait for app initialization
    /// </summary>
    public int StartupDelaySeconds { get; set; } = 10;

    /// <summary>
    /// Load from IConfiguration
    /// </summary>
    public static IndexingOptions FromConfiguration(IConfiguration configuration, string section = "Embeddings:BackgroundIndexing")
    {
        var options = new IndexingOptions();
        configuration.GetSection(section).Bind(options);
        return options;
    }
}