using System.Collections.Concurrent;
using System.Diagnostics;

namespace WatchmenBot.Infrastructure.Queue;

/// <summary>
/// Tracks queue processing metrics for monitoring and alerting.
/// Thread-safe singleton that collects statistics across all queue types.
/// </summary>
public class QueueMetrics
{
    private readonly ConcurrentDictionary<string, QueueStats> _stats = new();

    /// <summary>
    /// Record a task being picked up for processing.
    /// </summary>
    public void RecordTaskPicked(string queueName)
    {
        var stats = GetOrCreateStats(queueName);
        Interlocked.Increment(ref stats.TotalPicked);
        stats.LastPickedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Record a task completing successfully.
    /// </summary>
    public void RecordTaskCompleted(string queueName, TimeSpan duration, TimeSpan waitTime)
    {
        var stats = GetOrCreateStats(queueName);
        Interlocked.Increment(ref stats.TotalCompleted);
        stats.LastCompletedAt = DateTimeOffset.UtcNow;

        // Update average processing time (exponential moving average)
        var currentAvg = stats.AvgProcessingTimeMs;
        var newValue = duration.TotalMilliseconds;
        stats.AvgProcessingTimeMs = currentAvg == 0 ? newValue : (currentAvg * 0.9 + newValue * 0.1);

        // Update average wait time
        var currentWaitAvg = stats.AvgWaitTimeMs;
        var waitValue = waitTime.TotalMilliseconds;
        stats.AvgWaitTimeMs = currentWaitAvg == 0 ? waitValue : (currentWaitAvg * 0.9 + waitValue * 0.1);
    }

    /// <summary>
    /// Record a task failing.
    /// </summary>
    public void RecordTaskFailed(string queueName, int attemptCount, string? errorType = null)
    {
        var stats = GetOrCreateStats(queueName);
        Interlocked.Increment(ref stats.TotalFailed);
        stats.LastFailedAt = DateTimeOffset.UtcNow;
        stats.LastErrorType = errorType;

        // Track retry distribution
        if (attemptCount > 1)
        {
            Interlocked.Increment(ref stats.TotalRetried);
        }
    }

    /// <summary>
    /// Record a task being requeued (stale lease or retry).
    /// </summary>
    public void RecordTaskRequeued(string queueName, string reason)
    {
        var stats = GetOrCreateStats(queueName);
        Interlocked.Increment(ref stats.TotalRequeued);
        stats.LastRequeuedAt = DateTimeOffset.UtcNow;
        stats.LastRequeueReason = reason;
    }

    /// <summary>
    /// Record a task being permanently failed (max attempts exceeded).
    /// </summary>
    public void RecordTaskDead(string queueName)
    {
        var stats = GetOrCreateStats(queueName);
        Interlocked.Increment(ref stats.TotalDead);
        stats.LastDeadAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Update pending count for a queue.
    /// </summary>
    public void UpdatePendingCount(string queueName, int count)
    {
        var stats = GetOrCreateStats(queueName);
        stats.PendingCount = count;
        stats.PendingCountUpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Get all queue statistics.
    /// </summary>
    public Dictionary<string, QueueStats> GetAllStats()
    {
        return new Dictionary<string, QueueStats>(_stats);
    }

    /// <summary>
    /// Get statistics for a specific queue.
    /// </summary>
    public QueueStats? GetStats(string queueName)
    {
        return _stats.TryGetValue(queueName, out var stats) ? stats : null;
    }

    /// <summary>
    /// Check for stuck queues (no activity for too long).
    /// </summary>
    public IEnumerable<(string QueueName, TimeSpan IdleTime)> GetStuckQueues(TimeSpan threshold)
    {
        var now = DateTimeOffset.UtcNow;

        foreach (var (name, stats) in _stats)
        {
            if (stats.PendingCount > 0)
            {
                var lastActivity = new[]
                {
                    stats.LastPickedAt,
                    stats.LastCompletedAt,
                    stats.LastFailedAt
                }.Max();

                if (lastActivity.HasValue)
                {
                    var idleTime = now - lastActivity.Value;
                    if (idleTime > threshold)
                    {
                        yield return (name, idleTime);
                    }
                }
            }
        }
    }

    private QueueStats GetOrCreateStats(string queueName)
    {
        return _stats.GetOrAdd(queueName, _ => new QueueStats { QueueName = queueName });
    }
}

/// <summary>
/// Statistics for a single queue.
/// </summary>
public class QueueStats
{
    public required string QueueName { get; init; }

    // Counters
    public long TotalPicked;
    public long TotalCompleted;
    public long TotalFailed;
    public long TotalRetried;
    public long TotalRequeued;
    public long TotalDead;

    // Current state
    public int PendingCount;
    public DateTimeOffset? PendingCountUpdatedAt;

    // Timing
    public double AvgProcessingTimeMs;
    public double AvgWaitTimeMs;

    // Last activity
    public DateTimeOffset? LastPickedAt;
    public DateTimeOffset? LastCompletedAt;
    public DateTimeOffset? LastFailedAt;
    public DateTimeOffset? LastRequeuedAt;
    public DateTimeOffset? LastDeadAt;

    // Last error info
    public string? LastErrorType;
    public string? LastRequeueReason;

    /// <summary>
    /// Success rate (completed / (completed + failed)).
    /// </summary>
    public double SuccessRate =>
        TotalCompleted + TotalFailed > 0
            ? (double)TotalCompleted / (TotalCompleted + TotalFailed)
            : 1.0;

    /// <summary>
    /// Format stats for display.
    /// </summary>
    public override string ToString()
    {
        return $"{QueueName}: pending={PendingCount}, picked={TotalPicked}, done={TotalCompleted}, " +
               $"fail={TotalFailed}, retry={TotalRetried}, dead={TotalDead}, " +
               $"success={SuccessRate:P0}, avgTime={AvgProcessingTimeMs:F0}ms, avgWait={AvgWaitTimeMs:F0}ms";
    }
}