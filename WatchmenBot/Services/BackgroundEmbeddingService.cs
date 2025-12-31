using WatchmenBot.Services.Indexing;

namespace WatchmenBot.Services;

/// <summary>
/// Background service that orchestrates embedding indexing.
/// Delegates all processing logic to EmbeddingOrchestrator.
/// </summary>
public class BackgroundEmbeddingService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<BackgroundEmbeddingService> _logger;
    private readonly IConfiguration _configuration;
    private readonly IndexingOptions _options;

    public BackgroundEmbeddingService(
        IServiceProvider serviceProvider,
        ILogger<BackgroundEmbeddingService> logger,
        IConfiguration configuration)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _configuration = configuration;
        _options = IndexingOptions.FromConfiguration(configuration);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[Embeddings] Waiting {Delay}s for app startup...", _options.StartupDelaySeconds);
        await Task.Delay(TimeSpan.FromSeconds(_options.StartupDelaySeconds), stoppingToken);

        var enabled = _configuration.GetValue<bool>("Embeddings:BackgroundIndexing:Enabled", true);
        if (!enabled)
        {
            _logger.LogWarning("[Embeddings] Background indexing DISABLED in config");
            return;
        }

        var apiKey = _configuration["Embeddings:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("[Embeddings] No API key configured - background indexing DISABLED");
            return;
        }

        _logger.LogInformation("[Embeddings] Background service STARTED (idle interval: {Interval}min)",
            _options.IdleIntervalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Create scope for each run (fresh DI instances)
                using var scope = _serviceProvider.CreateScope();
                var orchestrator = scope.ServiceProvider.GetRequiredService<EmbeddingOrchestrator>();

                // Run the pipeline (all handlers)
                var hasMoreWork = await orchestrator.RunPipelineAsync(stoppingToken);

                // Adaptive delay: short if more work, long if idle
                if (hasMoreWork)
                {
                    _logger.LogDebug("[Embeddings] More work available, continuing in {Delay}s...",
                        _options.ActiveIntervalSeconds);
                    await Task.Delay(TimeSpan.FromSeconds(_options.ActiveIntervalSeconds), stoppingToken);
                }
                else
                {
                    _logger.LogDebug("[Embeddings] All caught up, sleeping {Minutes}min...",
                        _options.IdleIntervalMinutes);
                    await Task.Delay(TimeSpan.FromMinutes(_options.IdleIntervalMinutes), stoppingToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "[Embeddings] Error during indexing run");
                await Task.Delay(TimeSpan.FromSeconds(_options.ErrorRetryDelaySeconds), stoppingToken);
            }
        }

        _logger.LogInformation("[Embeddings] Background service STOPPED");
    }
}
