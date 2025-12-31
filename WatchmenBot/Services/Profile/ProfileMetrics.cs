using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace WatchmenBot.Services.Profile;

/// <summary>
/// Metrics for profile processing pipeline using .NET's built-in Meter/Counter/Histogram.
/// Thread-safe by design, no locks needed.
/// </summary>
public class ProfileMetrics
{
    private static readonly Meter _meter = new("WatchmenBot.Profiles", "1.0");

    private readonly Counter<int> _itemsProcessed;
    private readonly Counter<int> _errorsCount;
    private readonly Histogram<double> _processingTime;
    private readonly Histogram<int> _batchSize;

    public ProfileMetrics()
    {
        _itemsProcessed = _meter.CreateCounter<int>(
            "profile.items_processed",
            description: "Total number of items processed by handler");

        _errorsCount = _meter.CreateCounter<int>(
            "profile.errors",
            description: "Total number of errors by handler and type");

        _processingTime = _meter.CreateHistogram<double>(
            "profile.processing_time_seconds",
            unit: "s",
            description: "Time taken to process items");

        _batchSize = _meter.CreateHistogram<int>(
            "profile.batch_size",
            description: "Number of items processed in a batch");
    }

    /// <summary>
    /// Record successful processing of items
    /// </summary>
    public void RecordProcessing(string handlerName, int itemsProcessed, TimeSpan elapsed)
    {
        var tags = new TagList { { "handler", handlerName } };

        _itemsProcessed.Add(itemsProcessed, tags);
        _processingTime.Record(elapsed.TotalSeconds, tags);
        _batchSize.Record(itemsProcessed, tags);
    }

    /// <summary>
    /// Record an error during processing
    /// </summary>
    public void RecordError(string handlerName, string errorType)
    {
        var tags = new TagList
        {
            { "handler", handlerName },
            { "error_type", errorType }
        };

        _errorsCount.Add(1, tags);
    }
}
