using System.Diagnostics;

namespace WatchmenBot.Services.Indexing;

/// <summary>
/// Handler for message embeddings indexing.
/// Processes messages without embeddings and stores them.
/// </summary>
public class MessageEmbeddingHandler : IEmbeddingHandler
{
    private readonly MessageStore _messageStore;
    private readonly EmbeddingService _embeddingService;
    private readonly LogCollector _logCollector;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MessageEmbeddingHandler> _logger;

    public string Name => "message";

    public bool IsEnabled => _configuration.GetValue<bool>("Embeddings:BackgroundIndexing:Enabled", true);

    public MessageEmbeddingHandler(
        MessageStore messageStore,
        EmbeddingService embeddingService,
        LogCollector logCollector,
        IConfiguration configuration,
        ILogger<MessageEmbeddingHandler> logger)
    {
        _messageStore = messageStore;
        _embeddingService = embeddingService;
        _logCollector = logCollector;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<IndexingStats> GetStatsAsync(CancellationToken ct = default)
    {
        var (total, indexed, pending) = await _messageStore.GetEmbeddingStatsAsync();
        return new IndexingStats(total, indexed, pending);
    }

    public async Task<IndexingResult> ProcessBatchAsync(int batchSize, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            // Get messages without embeddings
            var messages = await _messageStore.GetMessagesWithoutEmbeddingsAsync(batchSize);

            if (messages.Count == 0)
            {
                return new IndexingResult(
                    ProcessedCount: 0,
                    ElapsedTime: sw.Elapsed,
                    HasMoreWork: false);
            }

            // Process batch
            await _embeddingService.StoreMessageEmbeddingsBatchAsync(messages, ct);

            // Update log collector stats
            _logCollector.IncrementEmbeddings(messages.Count);

            sw.Stop();

            // Has more work if we got a full batch
            var hasMore = messages.Count >= batchSize;

            return new IndexingResult(
                ProcessedCount: messages.Count,
                ElapsedTime: sw.Elapsed,
                HasMoreWork: hasMore);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "[MessageHandler] Error processing batch");
            throw;
        }
    }
}
