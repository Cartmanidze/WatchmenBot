namespace WatchmenBot.Features.Indexing.Services;

/// <summary>
/// Background service that orchestrates embedding indexing.
/// Delegates all processing logic to EmbeddingOrchestrator.
/// </summary>
public class BackgroundEmbeddingService(
    IServiceProvider serviceProvider,
    ILogger<BackgroundEmbeddingService> logger,
    IConfiguration configuration)
    : BackgroundService
{
    private readonly IndexingOptions _options = IndexingOptions.FromConfiguration(configuration);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("[Embeddings] Waiting {Delay}s for app startup...", _options.StartupDelaySeconds);
        await Task.Delay(TimeSpan.FromSeconds(_options.StartupDelaySeconds), stoppingToken);

        var enabled = configuration.GetValue("Embeddings:BackgroundIndexing:Enabled", true);
        if (!enabled)
        {
            logger.LogWarning("[Embeddings] Background indexing DISABLED in config");
            return;
        }

        var apiKey = configuration["Embeddings:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            logger.LogWarning("[Embeddings] No API key configured - background indexing DISABLED");
            return;
        }

        logger.LogInformation("[Embeddings] Background service STARTED (idle interval: {Interval}min)",
            _options.IdleIntervalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Create scope for each run (fresh DI instances)
                using var scope = serviceProvider.CreateScope();
                var orchestrator = scope.ServiceProvider.GetRequiredService<EmbeddingOrchestrator>();

                // Run the pipeline (all handlers)
                var hasMoreWork = await orchestrator.RunPipelineAsync(stoppingToken);

                // Adaptive delay: short if more work, long if idle
                if (hasMoreWork)
                {
                    logger.LogDebug("[Embeddings] More work available, continuing in {Delay}s...",
                        _options.ActiveIntervalSeconds);
                    await Task.Delay(TimeSpan.FromSeconds(_options.ActiveIntervalSeconds), stoppingToken);
                }
                else
                {
                    logger.LogDebug("[Embeddings] All caught up, sleeping {Minutes}min...",
                        _options.IdleIntervalMinutes);
                    await Task.Delay(TimeSpan.FromMinutes(_options.IdleIntervalMinutes), stoppingToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "[Embeddings] Error during indexing run");
                await Task.Delay(TimeSpan.FromSeconds(_options.ErrorRetryDelaySeconds), stoppingToken);
            }
        }

        logger.LogInformation("[Embeddings] Background service STOPPED");
    }
}
