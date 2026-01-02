namespace WatchmenBot.Features.Profile.Services;

/// <summary>
/// Nightly background service for deep profile generation.
/// Delegates all processing logic to ProfileOrchestrator.
/// </summary>
public class ProfileGeneratorService(
    IServiceProvider serviceProvider,
    ILogger<ProfileGeneratorService> logger,
    IConfiguration configuration)
    : BackgroundService
{
    private readonly ProfileOptions _options = ProfileOptions.FromConfiguration(configuration);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nightlyTime = TimeSpan.Parse(_options.NightlyProfileTime);

        logger.LogInformation("[ProfileGenerator] Started. Nightly run at: {Time}", nightlyTime);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.UtcNow;
                var nextRun = CalculateNextRun(now, nightlyTime);
                var delay = nextRun - now;

                logger.LogInformation("[ProfileGenerator] Next run at: {NextRun} UTC (in {Delay})",
                    nextRun, delay);

                await Task.Delay(delay, stoppingToken);

                // Create scope for each run (fresh DI instances)
                using var scope = serviceProvider.CreateScope();
                var orchestrator = scope.ServiceProvider.GetRequiredService<ProfileOrchestrator>();

                // Run profile generation
                await orchestrator.RunProfileGenerationAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[ProfileGenerator] Error during profile generation");
                // Wait before retry
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
            }
        }

        logger.LogInformation("[ProfileGenerator] Stopped");
    }

    private static DateTime CalculateNextRun(DateTime now, TimeSpan targetTime)
    {
        var todayRun = now.Date.Add(targetTime);

        if (now < todayRun)
            return todayRun;

        return todayRun.AddDays(1);
    }
}
