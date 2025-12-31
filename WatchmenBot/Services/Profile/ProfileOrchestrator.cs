namespace WatchmenBot.Services.Profile;

/// <summary>
/// Orchestrates the profile processing pipeline.
/// Coordinates fact extraction and profile generation handlers.
/// </summary>
public class ProfileOrchestrator
{
    private readonly FactExtractionHandler _factHandler;
    private readonly ProfileGenerationHandler _profileHandler;
    private readonly ProfileMetrics _metrics;
    private readonly ILogger<ProfileOrchestrator> _logger;

    public ProfileOrchestrator(
        FactExtractionHandler factHandler,
        ProfileGenerationHandler profileHandler,
        ProfileMetrics metrics,
        ILogger<ProfileOrchestrator> logger)
    {
        _factHandler = factHandler;
        _profileHandler = profileHandler;
        _metrics = metrics;
        _logger = logger;
    }

    /// <summary>
    /// Run fact extraction from message queue
    /// </summary>
    public async Task<bool> RunFactExtractionAsync(CancellationToken ct)
    {
        _logger.LogDebug("[ProfileOrchestrator] Running fact extraction");

        try
        {
            var result = await _factHandler.ProcessAsync(ct);

            _metrics.RecordProcessing(_factHandler.Name, result.ProcessedCount, result.ElapsedTime);

            _logger.LogInformation("[ProfileOrchestrator] Fact extraction complete: {Count} facts in {Elapsed:F1}s",
                result.ProcessedCount, result.ElapsedTime.TotalSeconds);

            return result.HasMoreWork;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ProfileOrchestrator] Fact extraction failed");
            _metrics.RecordError(_factHandler.Name, ex.GetType().Name);
            throw;
        }
    }

    /// <summary>
    /// Run profile generation (nightly)
    /// </summary>
    public async Task RunProfileGenerationAsync(CancellationToken ct)
    {
        _logger.LogDebug("[ProfileOrchestrator] Running profile generation");

        try
        {
            var result = await _profileHandler.ProcessAsync(ct);

            _metrics.RecordProcessing(_profileHandler.Name, result.ProcessedCount, result.ElapsedTime);

            _logger.LogInformation("[ProfileOrchestrator] Profile generation complete: {Count} profiles in {Elapsed:F1}s",
                result.ProcessedCount, result.ElapsedTime.TotalSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ProfileOrchestrator] Profile generation failed");
            _metrics.RecordError(_profileHandler.Name, ex.GetType().Name);
            throw;
        }
    }

    /// <summary>
    /// Get combined stats from all handlers
    /// </summary>
    public async Task<Dictionary<string, ProfileStats>> GetAllStatsAsync(CancellationToken ct = default)
    {
        var stats = new Dictionary<string, ProfileStats>();

        try
        {
            stats[_factHandler.Name] = await _factHandler.GetStatsAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ProfileOrchestrator] Failed to get stats for {Handler}", _factHandler.Name);
            stats[_factHandler.Name] = new ProfileStats(0, 0, 0);
        }

        try
        {
            stats[_profileHandler.Name] = await _profileHandler.GetStatsAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ProfileOrchestrator] Failed to get stats for {Handler}", _profileHandler.Name);
            stats[_profileHandler.Name] = new ProfileStats(0, 0, 0);
        }

        return stats;
    }
}
