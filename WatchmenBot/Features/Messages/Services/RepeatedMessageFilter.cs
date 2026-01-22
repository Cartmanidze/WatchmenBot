using System.Collections.Concurrent;

namespace WatchmenBot.Features.Messages.Services;

/// <summary>
/// Filters repeated messages from the same user in the same chat.
/// Uses in-memory cache with TTL to detect copy-paste spam.
/// </summary>
public class RepeatedMessageFilter : IDisposable
{
    // Key: (ChatId, UserId, Text) - using full text to avoid hash collisions
    private readonly ConcurrentDictionary<(long ChatId, long UserId, string Text), DateTime> _cache = new();
    private readonly TimeSpan _ttl = TimeSpan.FromSeconds(60);
    private readonly Timer _cleanupTimer;
    private readonly ILogger<RepeatedMessageFilter> _logger;
    private volatile bool _disposed;

    public RepeatedMessageFilter(ILogger<RepeatedMessageFilter> logger)
    {
        _logger = logger;
        // Cleanup every 5 minutes to prevent memory leaks
        _cleanupTimer = new Timer(Cleanup, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    /// <summary>
    /// Check if this message is a repeat of a recent message from the same user.
    /// Also registers the message if it's not a repeat.
    /// </summary>
    /// <returns>True if this is a repeated message that should be ignored</returns>
    public bool IsRepeated(long chatId, long userId, string? text)
    {
        // Skip empty messages
        if (string.IsNullOrWhiteSpace(text))
            return false;

        // Skip very short messages (they might be legitimate repeated responses like "да", "ок")
        if (text.Length < 10)
            return false;

        // Truncate very long messages to prevent memory bloat (first 500 chars is enough for dedup)
        var textKey = text.Length > 500 ? text[..500] : text;
        var key = (chatId, userId, textKey);
        var now = DateTime.UtcNow;

        // Check if exists and not expired
        if (_cache.TryGetValue(key, out var lastSeen))
        {
            if (now - lastSeen < _ttl)
            {
                _logger.LogDebug(
                    "[RepeatedMessageFilter] Detected repeated message from user {UserId} in chat {ChatId}",
                    userId, chatId);
                return true; // This is a repeat!
            }
        }

        // Not a repeat (or expired) - update cache and allow
        _cache[key] = now;
        return false;
    }

    private void Cleanup(object? state)
    {
        if (_disposed) return;

        try
        {
            var threshold = DateTime.UtcNow - _ttl;
            var expiredCount = 0;

            // ToArray() creates a snapshot for safe iteration
            foreach (var kvp in _cache.ToArray())
            {
                if (kvp.Value < threshold)
                {
                    if (_cache.TryRemove(kvp.Key, out _))
                        expiredCount++;
                }
            }

            if (expiredCount > 0)
            {
                _logger.LogDebug("[RepeatedMessageFilter] Cleaned up {Count} expired entries, cache size: {Size}",
                    expiredCount, _cache.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[RepeatedMessageFilter] Error during cleanup");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cleanupTimer.Dispose();
        _cache.Clear();
    }
}
