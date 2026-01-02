using System.Diagnostics;

namespace WatchmenBot.Services.Indexing;

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
        // For context embeddings, we don't have a simple "pending count"
        // Instead, we return the number of chats that need processing
        var chats = await messageStore.GetDistinctChatIdsAsync();
        var totalChats = chats.Count;

        // Approximate: count chats that have context embeddings
        // This is a rough estimate, not exact "indexed" count
        return new IndexingStats(
            Total: totalChats,
            Indexed: 0,  // We don't track this separately for context
            Pending: totalChats);  // Assume all chats need periodic updates
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
            var contextBatchSize = configuration.GetValue<int>("Embeddings:ContextEmbeddings:BatchSize", 100);

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
