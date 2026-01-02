namespace WatchmenBot.Features.Profile.Services;

/// <summary>
/// Background service for fact extraction from message queue.
/// Delegates all processing logic to ProfileOrchestrator.
/// </summary>
public class ProfileWorkerService(
    IServiceProvider serviceProvider,
    ILogger<ProfileWorkerService> logger,
    IConfiguration configuration)
    : BackgroundService
{
    private readonly ProfileOptions _options = ProfileOptions.FromConfiguration(configuration);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("[ProfileWorker] Started. Processing interval: {Interval}min",
            _options.QueueProcessingIntervalMinutes);

        // Startup delay
        await Task.Delay(TimeSpan.FromSeconds(_options.StartupDelaySeconds), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Create scope for each run (fresh DI instances)
                using var scope = serviceProvider.CreateScope();
                var orchestrator = scope.ServiceProvider.GetRequiredService<ProfileOrchestrator>();

                // Run fact extraction
                var hasMoreWork = await orchestrator.RunFactExtractionAsync(stoppingToken);

                // Adaptive delay: continue if more work, otherwise wait full interval
                if (hasMoreWork)
                {
                    logger.LogDebug("[ProfileWorker] More work available, continuing in 10s...");
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
                logger.LogError(ex, "[ProfileWorker] Error during fact extraction");
                await Task.Delay(TimeSpan.FromSeconds(_options.ErrorRetryDelaySeconds), stoppingToken);
            }
        }

        logger.LogInformation("[ProfileWorker] Stopped");
    }
}
