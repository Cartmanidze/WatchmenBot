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

    // Configuration defaults - optimized for speed
    // OpenAI embedding API has 3,000 RPM limit for most tiers
    private const int DefaultBatchSize = 100;
    private const int DefaultDelayBetweenBatchesSeconds = 2;
    private const int DefaultMaxBatchesPerRun = 500;

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
        _logger.LogInformation("[Embeddings] Waiting 10s for app startup...");
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

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

        var idleIntervalMinutes = _configuration.GetValue<int>("Embeddings:BackgroundIndexing:IntervalMinutes", 2);
        _logger.LogInformation("[Embeddings] Background service STARTED (idle interval: {Interval}min)", idleIntervalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var hasMoreWork = await ProcessBatchesAsync(stoppingToken);

                // If there's more work, continue immediately with a short pause
                // If caught up, wait longer before checking again
                if (hasMoreWork)
                {
                    _logger.LogDebug("[Embeddings] More work available, continuing in 5s...");
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
                else
                {
                    _logger.LogDebug("[Embeddings] All caught up, sleeping {Minutes}min...", idleIntervalMinutes);
                    await Task.Delay(TimeSpan.FromMinutes(idleIntervalMinutes), stoppingToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "[Embeddings] Error during indexing run");
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }

        _logger.LogInformation("[Embeddings] Background service STOPPED");
    }

    /// <summary>
    /// Process batches and return true if there's more work to do
    /// </summary>
    private async Task<bool> ProcessBatchesAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var messageStore = scope.ServiceProvider.GetRequiredService<MessageStore>();
        var embeddingService = scope.ServiceProvider.GetRequiredService<EmbeddingService>();
        var logCollector = scope.ServiceProvider.GetRequiredService<LogCollector>();

        // Get current stats
        var (total, indexed, pending) = await messageStore.GetEmbeddingStatsAsync();

        if (pending == 0)
        {
            _logger.LogInformation("[Embeddings] All caught up! {Indexed}/{Total} messages indexed", indexed, total);
            return false;
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
                logCollector.IncrementEmbeddings(messages.Count);

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

        // Build context embeddings (sliding windows) after regular embeddings
        await BuildContextEmbeddingsAsync(scope, ct);

        // Return true if there's more work (we hit max batches or batch was full)
        var hasMore = batchesProcessed >= maxBatches || (pending - totalProcessed) > 0;
        return hasMore;
    }

    /// <summary>
    /// Build context embeddings (sliding windows) for all chats
    /// </summary>
    private async Task BuildContextEmbeddingsAsync(IServiceScope scope, CancellationToken ct)
    {
        try
        {
            var contextEmbeddingEnabled = _configuration.GetValue<bool>("Embeddings:ContextEmbeddings:Enabled", true);
            if (!contextEmbeddingEnabled)
            {
                return;
            }

            var messageStore = scope.ServiceProvider.GetRequiredService<MessageStore>();
            var contextService = scope.ServiceProvider.GetRequiredService<ContextEmbeddingService>();

            // Get all chats with messages
            var chats = await messageStore.GetDistinctChatIdsAsync();

            // Configurable batch size for context embeddings (larger for initial indexing)
            var batchSize = _configuration.GetValue<int>("Embeddings:ContextEmbeddings:BatchSize", 100);

            foreach (var chatId in chats)
            {
                if (ct.IsCancellationRequested)
                    break;

                try
                {
                    await contextService.BuildContextEmbeddingsAsync(chatId, batchSize, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[ContextEmb] Failed to build context embeddings for chat {ChatId}", chatId);
                }

                // Small delay between chats to avoid overwhelming the embedding API
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ContextEmb] Error building context embeddings");
        }
    }
}
