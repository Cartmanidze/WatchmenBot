using System.Diagnostics.Metrics;

namespace WatchmenBot.Features.Indexing.Services;

/// <summary>
/// Tracks metrics for embedding indexing operations using System.Diagnostics.Metrics
/// Thread-safe and integrates with OpenTelemetry/Prometheus
/// </summary>
public class IndexingMetrics
{
    private static readonly Meter Meter = new("WatchmenBot.Embeddings", "1.0");

    private readonly Counter<int> _itemsProcessed = Meter.CreateCounter<int>(
        name: "embeddings.items.processed",
        unit: "items",
        description: "Total embedding items processed");
    private readonly Counter<int> _batchesProcessed = Meter.CreateCounter<int>(
        name: "embeddings.batches.processed",
        unit: "batches",
        description: "Total batches processed");
    private readonly Counter<int> _errorsCount = Meter.CreateCounter<int>(
        name: "embeddings.errors.total",
        unit: "errors",
        description: "Total errors during embedding processing");
    private readonly Histogram<double> _batchProcessingTime = Meter.CreateHistogram<double>(
        name: "embeddings.batch.duration",
        unit: "ms",
        description: "Batch processing duration in milliseconds");
    private readonly Histogram<int> _batchSize = Meter.CreateHistogram<int>(
        name: "embeddings.batch.size",
        unit: "items",
        description: "Number of items per batch");

    // Counter: Total items processed by handler
    // Counter: Total batches processed
    // Counter: Total errors encountered
    // Histogram: Batch processing time distribution
    // Histogram: Batch size distribution

    /// <summary>
    /// Record a batch processing operation
    /// </summary>
    /// <param name="handlerName">Name of the handler (e.g., "message", "context")</param>
    /// <param name="itemsProcessed">Number of items processed in this batch</param>
    /// <param name="elapsed">Time taken to process the batch</param>
    public void RecordBatch(string handlerName, int itemsProcessed, TimeSpan elapsed)
    {
        var tags = new KeyValuePair<string, object?>("handler", handlerName);

        _itemsProcessed.Add(itemsProcessed, tags);
        _batchesProcessed.Add(1, tags);
        _batchProcessingTime.Record(elapsed.TotalMilliseconds, tags);
        _batchSize.Record(itemsProcessed, tags);
    }

    /// <summary>
    /// Record an error during batch processing
    /// </summary>
    /// <param name="handlerName">Name of the handler where error occurred</param>
    /// <param name="errorType">Type of error (e.g., "rate_limit", "http_error", "processing_error")</param>
    public void RecordError(string handlerName, string errorType)
    {
        var tags = new[]
        {
            new KeyValuePair<string, object?>("handler", handlerName),
            new KeyValuePair<string, object?>("error_type", errorType)
        };

        _errorsCount.Add(1, tags);
    }
}