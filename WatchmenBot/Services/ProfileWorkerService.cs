using WatchmenBot.Services.Profile;

namespace WatchmenBot.Services;

/// <summary>
/// Background service for fact extraction from message queue.
/// Delegates all processing logic to ProfileOrchestrator.
/// </summary>
public class ProfileWorkerService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ProfileWorkerService> _logger;
    private readonly IConfiguration _configuration;
    private readonly ProfileOptions _options;

    public ProfileWorkerService(
        IServiceProvider serviceProvider,
        ILogger<ProfileWorkerService> logger,
        IConfiguration configuration)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _configuration = configuration;
        _options = ProfileOptions.FromConfiguration(configuration);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[ProfileWorker] Started. Processing interval: {Interval}min",
            _options.QueueProcessingIntervalMinutes);

        // Startup delay
        await Task.Delay(TimeSpan.FromSeconds(_options.StartupDelaySeconds), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Create scope for each run (fresh DI instances)
                using var scope = _serviceProvider.CreateScope();
                var orchestrator = scope.ServiceProvider.GetRequiredService<ProfileOrchestrator>();

                // Run fact extraction
                var hasMoreWork = await orchestrator.RunFactExtractionAsync(stoppingToken);

                // Adaptive delay: continue if more work, otherwise wait full interval
                if (hasMoreWork)
                {
                    _logger.LogDebug("[ProfileWorker] More work available, continuing in 10s...");
                    await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                }
                else
                {
                    await Task.Delay(TimeSpan.FromMinutes(_options.QueueProcessingIntervalMinutes), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ProfileWorker] Error during fact extraction");
                await Task.Delay(TimeSpan.FromSeconds(_options.ErrorRetryDelaySeconds), stoppingToken);
            }
        }

        _logger.LogInformation("[ProfileWorker] Stopped");
    }
}
