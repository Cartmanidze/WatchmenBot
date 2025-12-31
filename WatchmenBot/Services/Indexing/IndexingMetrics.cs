using System.Diagnostics.Metrics;

namespace WatchmenBot.Services.Indexing;

/// <summary>
/// Tracks metrics for embedding indexing operations using System.Diagnostics.Metrics
/// Thread-safe and integrates with OpenTelemetry/Prometheus
/// </summary>
public class IndexingMetrics
{
    private static readonly Meter _meter = new("WatchmenBot.Embeddings", "1.0");

    private readonly Counter<int> _itemsProcessed;
    private readonly Counter<int> _batchesProcessed;
    private readonly Counter<int> _errorsCount;
    private readonly Histogram<double> _batchProcessingTime;
    private readonly Histogram<int> _batchSize;

    public IndexingMetrics()
    {
        // Counter: Total items processed by handler
        _itemsProcessed = _meter.CreateCounter<int>(
            name: "embeddings.items.processed",
            unit: "items",
            description: "Total embedding items processed");

        // Counter: Total batches processed
        _batchesProcessed = _meter.CreateCounter<int>(
            name: "embeddings.batches.processed",
            unit: "batches",
            description: "Total batches processed");

        // Counter: Total errors encountered
        _errorsCount = _meter.CreateCounter<int>(
            name: "embeddings.errors.total",
            unit: "errors",
            description: "Total errors during embedding processing");

        // Histogram: Batch processing time distribution
        _batchProcessingTime = _meter.CreateHistogram<double>(
            name: "embeddings.batch.duration",
            unit: "ms",
            description: "Batch processing duration in milliseconds");

        // Histogram: Batch size distribution
        _batchSize = _meter.CreateHistogram<int>(
            name: "embeddings.batch.size",
            unit: "items",
            description: "Number of items per batch");
    }

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