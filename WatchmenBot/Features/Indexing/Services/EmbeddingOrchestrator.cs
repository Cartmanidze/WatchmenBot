namespace WatchmenBot.Features.Indexing.Services;

/// <summary>
/// Orchestrates the embedding indexing pipeline.
/// Coordinates multiple handlers in the correct order with dependencies.
/// </summary>
public class EmbeddingOrchestrator
{
    private readonly BatchProcessor _batchProcessor;
    private readonly ILogger<EmbeddingOrchestrator> _logger;
    private readonly List<IEmbeddingHandler> _handlers;

    public EmbeddingOrchestrator(
        IEnumerable<IEmbeddingHandler> handlers,
        BatchProcessor batchProcessor,
        ILogger<EmbeddingOrchestrator> logger)
    {
        _batchProcessor = batchProcessor;
        _logger = logger;

        // Order handlers: message first (context depends on it), then context
        _handlers = handlers
            .OrderBy(h => h.Name == "message" ? 0 : h.Name == "context" ? 1 : 2)
            .ToList();

        _logger.LogInformation("[Orchestrator] Initialized with {Count} handlers: {Names}",
            _handlers.Count,
            string.Join(", ", _handlers.Select(h => h.Name)));
    }

    /// <summary>
    /// Run the full indexing pipeline (all handlers sequentially)
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if any handler has more work to do</returns>
    public async Task<bool> RunPipelineAsync(CancellationToken ct)
    {
        _logger.LogDebug("[Orchestrator] Starting pipeline run");

        var hasMoreWork = false;

        foreach (var handler in _handlers)
        {
            if (ct.IsCancellationRequested)
                break;

            try
            {
                var handlerHasMore = await _batchProcessor.ProcessBatchesAsync(handler, ct);
                hasMoreWork = hasMoreWork || handlerHasMore;

                _logger.LogDebug("[Orchestrator] Handler {Handler} completed: hasMore={HasMore}",
                    handler.Name, handlerHasMore);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Orchestrator] Handler {Handler} failed, continuing with next handler",
                    handler.Name);
                // Continue with next handler on error
            }
        }

        _logger.LogInformation("[Orchestrator] Pipeline run completed: hasMoreWork={HasMore}", hasMoreWork);

        return hasMoreWork;
    }

    /// <summary>
    /// Get combined stats from all handlers
    /// </summary>
    public async Task<Dictionary<string, IndexingStats>> GetAllStatsAsync(CancellationToken ct = default)
    {
        var stats = new Dictionary<string, IndexingStats>();

        foreach (var handler in _handlers)
        {
            try
            {
                var handlerStats = await handler.GetStatsAsync(ct);
                stats[handler.Name] = handlerStats;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Orchestrator] Failed to get stats for handler {Handler}", handler.Name);
                stats[handler.Name] = new IndexingStats(0, 0, 0);
            }
        }

        return stats;
    }
}
