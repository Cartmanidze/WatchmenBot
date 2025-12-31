using WatchmenBot.Services.Profile;

namespace WatchmenBot.Services;

/// <summary>
/// Nightly background service for deep profile generation.
/// Delegates all processing logic to ProfileOrchestrator.
/// </summary>
public class ProfileGeneratorService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ProfileGeneratorService> _logger;
    private readonly IConfiguration _configuration;
    private readonly ProfileOptions _options;

    public ProfileGeneratorService(
        IServiceProvider serviceProvider,
        ILogger<ProfileGeneratorService> logger,
        IConfiguration configuration)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _configuration = configuration;
        _options = ProfileOptions.FromConfiguration(configuration);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nightlyTime = TimeSpan.Parse(_options.NightlyProfileTime);

        _logger.LogInformation("[ProfileGenerator] Started. Nightly run at: {Time}", nightlyTime);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.UtcNow;
                var nextRun = CalculateNextRun(now, nightlyTime);
                var delay = nextRun - now;

                _logger.LogInformation("[ProfileGenerator] Next run at: {NextRun} UTC (in {Delay})",
                    nextRun, delay);

                await Task.Delay(delay, stoppingToken);

                // Create scope for each run (fresh DI instances)
                using var scope = _serviceProvider.CreateScope();
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
                _logger.LogError(ex, "[ProfileGenerator] Error during profile generation");
                // Wait before retry
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
            }
        }

        _logger.LogInformation("[ProfileGenerator] Stopped");
    }

    private static DateTime CalculateNextRun(DateTime now, TimeSpan targetTime)
    {
        var todayRun = now.Date.Add(targetTime);

        if (now < todayRun)
            return todayRun;

        return todayRun.AddDays(1);
    }
}
