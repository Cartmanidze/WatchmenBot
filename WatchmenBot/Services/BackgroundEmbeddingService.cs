using System.Diagnostics;

namespace WatchmenBot.Services;

/// <summary>
/// Background service that gradually indexes historical messages with embeddings.
/// Processes messages in batches to avoid API rate limits.
/// </summary>
public class BackgroundEmbeddingService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<BackgroundEmbeddingService> _logger;
    private readonly IConfiguration _configuration;

    // Configuration defaults
    private const int DefaultBatchSize = 50;
    private const int DefaultDelayBetweenBatchesSeconds = 30;
    private const int DefaultMaxBatchesPerRun = 100;

    public BackgroundEmbeddingService(
        IServiceProvider serviceProvider,
        ILogger<BackgroundEmbeddingService> logger,
        IConfiguration configuration)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _configuration = configuration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[Embeddings] Waiting 30s for app startup...");
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

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

        var intervalMinutes = _configuration.GetValue<int>("Embeddings:BackgroundIndexing:IntervalMinutes", 5);
        _logger.LogInformation("[Embeddings] Background service STARTED (interval: {Interval}min)", intervalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessBatchesAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "[Embeddings] Error during indexing run");
            }

            _logger.LogDebug("[Embeddings] Sleeping {Minutes}min until next run...", intervalMinutes);
            await Task.Delay(TimeSpan.FromMinutes(intervalMinutes), stoppingToken);
        }

        _logger.LogInformation("[Embeddings] Background service STOPPED");
    }

    private async Task ProcessBatchesAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var messageStore = scope.ServiceProvider.GetRequiredService<MessageStore>();
        var embeddingService = scope.ServiceProvider.GetRequiredService<EmbeddingService>();

        // Get current stats
        var (total, indexed, pending) = await messageStore.GetEmbeddingStatsAsync();

        if (pending == 0)
        {
            _logger.LogInformation("[Embeddings] All caught up! {Indexed}/{Total} messages indexed", indexed, total);
            return;
        }

        _logger.LogInformation("[Embeddings] Starting indexing run: {Pending} pending, {Indexed}/{Total} already indexed",
            pending, indexed, total);

        var batchSize = _configuration.GetValue<int>("Embeddings:BackgroundIndexing:BatchSize", DefaultBatchSize);
        var delaySeconds = _configuration.GetValue<int>("Embeddings:BackgroundIndexing:DelayBetweenBatchesSeconds", DefaultDelayBetweenBatchesSeconds);
        var maxBatches = _configuration.GetValue<int>("Embeddings:BackgroundIndexing:MaxBatchesPerRun", DefaultMaxBatchesPerRun);

        var totalProcessed = 0;
        var batchesProcessed = 0;
        var sw = Stopwatch.StartNew();

        while (batchesProcessed < maxBatches && !ct.IsCancellationRequested)
        {
            var messages = await messageStore.GetMessagesWithoutEmbeddingsAsync(batchSize);

            if (messages.Count == 0)
            {
                break;
            }

            try
            {
                var batchSw = Stopwatch.StartNew();
                await embeddingService.StoreMessageEmbeddingsBatchAsync(messages, ct);
                batchSw.Stop();

                totalProcessed += messages.Count;
                batchesProcessed++;

                var remainingEstimate = pending - totalProcessed;
                _logger.LogInformation("[Embeddings] Batch {Batch}: +{Count} messages in {Ms}ms | Progress: {Done}/{Pending} ({Percent:F1}%)",
                    batchesProcessed,
                    messages.Count,
                    batchSw.ElapsedMilliseconds,
                    totalProcessed,
                    pending,
                    (double)totalProcessed / pending * 100);

                // Delay between batches to respect rate limits
                if (batchesProcessed < maxBatches && messages.Count == batchSize)
                {
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct);
                }
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                _logger.LogWarning("[Embeddings] Rate limited! Waiting 60s before retry...");
                await Task.Delay(TimeSpan.FromSeconds(60), ct);
            }
        }

        sw.Stop();
        if (totalProcessed > 0)
        {
            var rate = totalProcessed / sw.Elapsed.TotalSeconds;
            _logger.LogInformation("[Embeddings] Run complete: {Total} messages in {Batches} batches, {Elapsed:F1}s ({Rate:F1} msg/s)",
                totalProcessed, batchesProcessed, sw.Elapsed.TotalSeconds, rate);
        }
    }
}
