using System.Diagnostics;
using WatchmenBot.Features.Search.Services;
using WatchmenBot.Features.Messages.Services;

namespace WatchmenBot.Features.Indexing.Services;

/// <summary>
/// Handler for context embeddings (sliding windows) indexing.
/// Processes chats one at a time to build context windows.
/// </summary>
public class ContextEmbeddingHandler(
    MessageStore messageStore,
    ContextEmbeddingService contextService,
    IConfiguration configuration,
    ILogger<ContextEmbeddingHandler> logger)
    : IEmbeddingHandler
{
    // State: current chat index being processed
    private List<long>? _chatIds;
    private int _currentChatIndex;

    public string Name => "context";

    public bool IsEnabled => configuration.GetValue("Embeddings:ContextEmbeddings:Enabled", true);

    public async Task<IndexingStats> GetStatsAsync(CancellationToken ct = default)
    {
        // Get real stats from the database
        var stats = await contextService.GetIndexingStatsAsync(ct);

        // Calculate pending: estimated total - already indexed
        var pending = Math.Max(0, stats.EstimatedTotal - stats.Indexed);

        return new IndexingStats(
            Total: stats.EstimatedTotal,
            Indexed: stats.Indexed,
            Pending: pending);
    }

    public async Task<IndexingResult> ProcessBatchAsync(int batchSize, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            // Lazy load chat IDs on first call
            if (_chatIds == null)
            {
                _chatIds = await messageStore.GetDistinctChatIdsAsync();
                _currentChatIndex = 0;
                logger.LogDebug("[ContextHandler] Loaded {Count} chats for processing", _chatIds.Count);
            }

            // No chats to process
            if (_chatIds.Count == 0 || _currentChatIndex >= _chatIds.Count)
            {
                // Reset for next run
                _chatIds = null;
                _currentChatIndex = 0;

                return new IndexingResult(
                    ProcessedCount: 0,
                    ElapsedTime: sw.Elapsed,
                    HasMoreWork: false);
            }

            // Process current chat
            var chatId = _chatIds[_currentChatIndex];
            var contextBatchSize = configuration.GetValue("Embeddings:ContextEmbeddings:BatchSize", 500);

            try
            {
                await contextService.BuildContextEmbeddingsAsync(chatId, contextBatchSize, ct);

                // Move to next chat
                _currentChatIndex++;

                sw.Stop();

                // Has more work if we haven't processed all chats
                var hasMore = _currentChatIndex < _chatIds.Count;

                // If we completed the cycle, reset for next run
                if (!hasMore)
                {
                    _chatIds = null;
                    _currentChatIndex = 0;
                }

                return new IndexingResult(
                    ProcessedCount: 1,  // Processed 1 chat
                    ElapsedTime: sw.Elapsed,
                    HasMoreWork: hasMore);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[ContextHandler] Failed to build context embeddings for chat {ChatId}", chatId);

                // Skip to next chat on error
                _currentChatIndex++;

                sw.Stop();

                // Continue with next chat (safe: _chatIds is not null in this scope)
                var hasMore = _currentChatIndex < _chatIds!.Count;

                if (!hasMore)
                {
                    _chatIds = null;
                    _currentChatIndex = 0;
                }

                // Rethrow to let BatchProcessor handle error metrics
                throw;
            }
        }
        catch (Exception ex)
        {
            sw.Stop();
            logger.LogError(ex, "[ContextHandler] Error in ProcessBatchAsync");
            throw;
        }
    }
}
