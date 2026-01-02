using System.Diagnostics;
using WatchmenBot.Features.Search.Services;
using WatchmenBot.Features.Messages.Services;
using WatchmenBot.Features.Admin.Services;

namespace WatchmenBot.Features.Indexing.Services;

/// <summary>
/// Handler for message embeddings indexing.
/// Processes messages without embeddings and stores them.
/// </summary>
public class MessageEmbeddingHandler(
    MessageStore messageStore,
    EmbeddingService embeddingService,
    LogCollector logCollector,
    IConfiguration configuration,
    ILogger<MessageEmbeddingHandler> logger)
    : IEmbeddingHandler
{
    public string Name => "message";

    public bool IsEnabled => configuration.GetValue("Embeddings:BackgroundIndexing:Enabled", true);

    public async Task<IndexingStats> GetStatsAsync(CancellationToken ct = default)
    {
        var (total, indexed, pending) = await messageStore.GetEmbeddingStatsAsync();
        return new IndexingStats(total, indexed, pending);
    }

    public async Task<IndexingResult> ProcessBatchAsync(int batchSize, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            // Get messages without embeddings
            var messages = await messageStore.GetMessagesWithoutEmbeddingsAsync(batchSize);

            if (messages.Count == 0)
            {
                return new IndexingResult(
                    ProcessedCount: 0,
                    ElapsedTime: sw.Elapsed,
                    HasMoreWork: false);
            }

            // Process batch
            await embeddingService.StoreMessageEmbeddingsBatchAsync(messages, ct);

            // Update log collector stats
            logCollector.IncrementEmbeddings(messages.Count);

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
            logger.LogError(ex, "[MessageHandler] Error processing batch");
            throw;
        }
    }
}
