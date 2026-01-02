namespace WatchmenBot.Features.Profile.Services;

/// <summary>
/// Orchestrates the profile processing pipeline.
/// Coordinates fact extraction and profile generation handlers.
/// </summary>
public class ProfileOrchestrator(
    FactExtractionHandler factHandler,
    ProfileGenerationHandler profileHandler,
    ProfileMetrics metrics,
    ILogger<ProfileOrchestrator> logger)
{
    /// <summary>
    /// Run fact extraction from message queue
    /// </summary>
    public async Task<bool> RunFactExtractionAsync(CancellationToken ct)
    {
        logger.LogDebug("[ProfileOrchestrator] Running fact extraction");

        try
        {
            var result = await factHandler.ProcessAsync(ct);

            metrics.RecordProcessing(factHandler.Name, result.ProcessedCount, result.ElapsedTime);

            logger.LogInformation("[ProfileOrchestrator] Fact extraction complete: {Count} facts in {Elapsed:F1}s",
                result.ProcessedCount, result.ElapsedTime.TotalSeconds);

            return result.HasMoreWork;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[ProfileOrchestrator] Fact extraction failed");
            metrics.RecordError(factHandler.Name, ex.GetType().Name);
            throw;
        }
    }

    /// <summary>
    /// Run profile generation (nightly)
    /// </summary>
    public async Task RunProfileGenerationAsync(CancellationToken ct)
    {
        logger.LogDebug("[ProfileOrchestrator] Running profile generation");

        try
        {
            var result = await profileHandler.ProcessAsync(ct);

            metrics.RecordProcessing(profileHandler.Name, result.ProcessedCount, result.ElapsedTime);

            logger.LogInformation("[ProfileOrchestrator] Profile generation complete: {Count} profiles in {Elapsed:F1}s",
                result.ProcessedCount, result.ElapsedTime.TotalSeconds);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[ProfileOrchestrator] Profile generation failed");
            metrics.RecordError(profileHandler.Name, ex.GetType().Name);
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
            stats[factHandler.Name] = await factHandler.GetStatsAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[ProfileOrchestrator] Failed to get stats for {Handler}", factHandler.Name);
            stats[factHandler.Name] = new ProfileStats(0, 0, 0);
        }

        try
        {
            stats[profileHandler.Name] = await profileHandler.GetStatsAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[ProfileOrchestrator] Failed to get stats for {Handler}", profileHandler.Name);
            stats[profileHandler.Name] = new ProfileStats(0, 0, 0);
        }

        return stats;
    }
}
