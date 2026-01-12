using Dapper;
using WatchmenBot.Infrastructure.Database;

namespace WatchmenBot.Infrastructure.Queue;

/// <summary>
/// Configuration for resilient queue processing.
/// </summary>
public class QueueConfig
{
    /// <summary>Name of the queue table.</summary>
    public required string TableName { get; init; }

    /// <summary>Name for metrics/logging.</summary>
    public required string QueueName { get; init; }

    /// <summary>Maximum number of retry attempts.</summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>Base delay for exponential backoff (doubles each retry).</summary>
    public TimeSpan BaseRetryDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Maximum backoff delay.</summary>
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>How long a task can be "picked" before considered stale.</summary>
    public TimeSpan LeaseTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How often to check for stale tasks.</summary>
    public TimeSpan StaleCheckInterval { get; init; } = TimeSpan.FromMinutes(1);
}

/// <summary>
/// Base interface for queue items with retry support.
/// </summary>
public interface IQueueItem
{
    int Id { get; }
    DateTimeOffset CreatedAt { get; }
    int AttemptCount { get; }
    string? Error { get; }
}

/// <summary>
/// Provides resilient queue operations: atomic pick with lease, retry with backoff,
/// stale task recovery, and metrics collection.
///
/// ★ Insight ─────────────────────────────────────
/// This implements the "at-least-once" delivery pattern:
/// - Tasks are "leased" (picked_at timestamp) rather than deleted
/// - Stale leases are automatically reclaimed
/// - Exponential backoff prevents thundering herd
/// ─────────────────────────────────────────────────
/// </summary>
public class ResilientQueueService(
    IDbConnectionFactory connectionFactory,
    QueueMetrics metrics,
    ILogger<ResilientQueueService> logger)
{
    /// <summary>
    /// Atomically pick a task from the queue with lease acquisition.
    /// Uses SELECT FOR UPDATE SKIP LOCKED for safe concurrent access.
    /// </summary>
    public async Task<T?> PickAsync<T>(QueueConfig config, CancellationToken ct = default) where T : class, IQueueItem
    {
        try
        {
            using var connection = await connectionFactory.CreateConnectionAsync();

            // Atomic pick: find ready task, set picked_at, increment attempt
            // Uses SKIP LOCKED to avoid contention with other workers
            // NOTE: Dapper.DefaultTypeMap.MatchNamesWithUnderscores enabled in Program.cs
            var pickSql = $"""
                UPDATE {config.TableName}
                SET picked_at = NOW(),
                    attempt_count = attempt_count + 1,
                    started_at = NOW()
                WHERE id = (
                    SELECT id FROM {config.TableName}
                    WHERE processed = FALSE
                      AND next_run_at <= NOW()
                      AND (picked_at IS NULL OR picked_at < NOW() - @LeaseTimeout)
                      AND attempt_count < @MaxAttempts
                    ORDER BY next_run_at
                    LIMIT 1
                    FOR UPDATE SKIP LOCKED
                )
                RETURNING *
                """;

            var item = await connection.QueryFirstOrDefaultAsync<T>(
                pickSql,
                new
                {
                    LeaseTimeout = config.LeaseTimeout,
                    MaxAttempts = config.MaxAttempts
                });

            if (item != null)
            {
                metrics.RecordTaskPicked(config.QueueName);

                var waitTime = DateTimeOffset.UtcNow - item.CreatedAt;
                logger.LogDebug("[{Queue}] Picked task {Id}, attempt {Attempt}, waited {Wait:F1}s",
                    config.QueueName, item.Id, item.AttemptCount, waitTime.TotalSeconds);
            }
            else
            {
                // Queue is empty — update metrics for stuck detector
                metrics.UpdatePendingCount(config.QueueName, 0);
            }

            return item;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[{Queue}] Failed to pick task", config.QueueName);
            return null;
        }
    }

    /// <summary>
    /// Mark task as successfully completed.
    /// Clears any previous error from failed attempts.
    /// </summary>
    public async Task CompleteAsync(QueueConfig config, int taskId, DateTimeOffset createdAt)
    {
        try
        {
            using var connection = await connectionFactory.CreateConnectionAsync();

            // Get timing data and clear error (important for successful retries)
            var timing = await connection.QueryFirstOrDefaultAsync<(DateTimeOffset? StartedAt, DateTimeOffset? PickedAt)>($"""
                UPDATE {config.TableName}
                SET processed = TRUE,
                    completed_at = NOW(),
                    picked_at = NULL,
                    error = NULL
                WHERE id = @Id
                RETURNING started_at, picked_at
                """,
                new { Id = taskId });

            // Calculate proper metrics
            var now = DateTimeOffset.UtcNow;
            var startedAt = timing.StartedAt ?? timing.PickedAt ?? createdAt;
            var duration = now - startedAt;
            var waitTime = startedAt - createdAt;

            metrics.RecordTaskCompleted(config.QueueName, duration, waitTime);

            logger.LogDebug("[{Queue}] Completed task {Id} in {Duration:F1}s (waited {Wait:F1}s)",
                config.QueueName, taskId, duration.TotalSeconds, waitTime.TotalSeconds);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[{Queue}] Failed to mark task {Id} as completed", config.QueueName, taskId);
        }
    }

    /// <summary>
    /// Mark task as failed with retry scheduling.
    /// If max attempts reached, marks as permanently failed.
    /// </summary>
    public async Task<bool> FailAsync(QueueConfig config, int taskId, int attemptCount, string error)
    {
        try
        {
            using var connection = await connectionFactory.CreateConnectionAsync();

            var isExhausted = attemptCount >= config.MaxAttempts;

            if (isExhausted)
            {
                // Permanently failed - mark as processed with error
                await connection.ExecuteAsync($"""
                    UPDATE {config.TableName}
                    SET processed = TRUE,
                        completed_at = NOW(),
                        picked_at = NULL,
                        error = @Error
                    WHERE id = @Id
                    """,
                    new { Id = taskId, Error = $"[DEAD] {error}" });

                metrics.RecordTaskDead(config.QueueName);
                logger.LogWarning("[{Queue}] Task {Id} permanently failed after {Attempts} attempts: {Error}",
                    config.QueueName, taskId, attemptCount, error);

                return false; // Not retryable
            }
            else
            {
                // Schedule retry with exponential backoff
                var delay = CalculateBackoff(attemptCount, config.BaseRetryDelay, config.MaxRetryDelay);
                var nextRun = DateTimeOffset.UtcNow + delay;

                await connection.ExecuteAsync($"""
                    UPDATE {config.TableName}
                    SET picked_at = NULL,
                        next_run_at = @NextRun,
                        error = @Error
                    WHERE id = @Id
                    """,
                    new { Id = taskId, NextRun = nextRun, Error = error });

                metrics.RecordTaskFailed(config.QueueName, attemptCount, error.Split(':').FirstOrDefault());

                logger.LogWarning("[{Queue}] Task {Id} failed (attempt {Attempt}/{Max}), retry in {Delay:F0}s: {Error}",
                    config.QueueName, taskId, attemptCount, config.MaxAttempts, delay.TotalSeconds, error);

                return true; // Will be retried
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[{Queue}] Failed to mark task {Id} as failed", config.QueueName, taskId);
            return false;
        }
    }

    /// <summary>
    /// Recover stale tasks (picked but never completed).
    /// Should be called periodically by background workers.
    /// Also marks exhausted stale tasks as dead (max attempts reached but worker crashed).
    /// </summary>
    public async Task<int> RecoverStaleTasksAsync(QueueConfig config)
    {
        try
        {
            using var connection = await connectionFactory.CreateConnectionAsync();

            // 1. Requeue stale tasks that still have attempts left
            var recovered = await connection.ExecuteAsync($"""
                UPDATE {config.TableName}
                SET picked_at = NULL,
                    next_run_at = NOW(),
                    error = COALESCE(error, '') || '[STALE] Worker timeout at ' || NOW()::text
                WHERE processed = FALSE
                  AND picked_at IS NOT NULL
                  AND picked_at < NOW() - @LeaseTimeout
                  AND attempt_count < @MaxAttempts
                """,
                new
                {
                    LeaseTimeout = config.LeaseTimeout,
                    MaxAttempts = config.MaxAttempts
                });

            if (recovered > 0)
            {
                for (int i = 0; i < recovered; i++)
                {
                    metrics.RecordTaskRequeued(config.QueueName, "stale_lease");
                }

                logger.LogWarning("[{Queue}] Recovered {Count} stale tasks", config.QueueName, recovered);
            }

            // 2. Mark exhausted stale tasks as dead (crash on last attempt scenario)
            var markedDead = await connection.ExecuteAsync($"""
                UPDATE {config.TableName}
                SET processed = TRUE,
                    completed_at = NOW(),
                    picked_at = NULL,
                    error = COALESCE(error, '') || '[DEAD] Worker crashed on final attempt at ' || NOW()::text
                WHERE processed = FALSE
                  AND picked_at IS NOT NULL
                  AND picked_at < NOW() - @LeaseTimeout
                  AND attempt_count >= @MaxAttempts
                """,
                new
                {
                    LeaseTimeout = config.LeaseTimeout,
                    MaxAttempts = config.MaxAttempts
                });

            if (markedDead > 0)
            {
                for (int i = 0; i < markedDead; i++)
                {
                    metrics.RecordTaskDead(config.QueueName);
                }

                logger.LogWarning("[{Queue}] Marked {Count} exhausted stale tasks as dead", config.QueueName, markedDead);
            }

            return recovered + markedDead;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[{Queue}] Failed to recover stale tasks", config.QueueName);
            return 0;
        }
    }

    /// <summary>
    /// Get count of truly pending tasks (excludes in-progress).
    /// </summary>
    public async Task<int> GetPendingCountAsync(QueueConfig config)
    {
        try
        {
            using var connection = await connectionFactory.CreateConnectionAsync();

            // Count only tasks that are ready to be picked (not currently being processed)
            var count = await connection.ExecuteScalarAsync<int>($"""
                SELECT COUNT(*) FROM {config.TableName}
                WHERE processed = FALSE
                  AND next_run_at <= NOW()
                  AND (picked_at IS NULL OR picked_at < NOW() - @LeaseTimeout)
                  AND attempt_count < @MaxAttempts
                """,
                new
                {
                    MaxAttempts = config.MaxAttempts,
                    LeaseTimeout = config.LeaseTimeout
                });

            metrics.UpdatePendingCount(config.QueueName, count);
            return count;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[{Queue}] Failed to get pending count", config.QueueName);
            return 0;
        }
    }

    /// <summary>
    /// Get detailed queue statistics for admin dashboard.
    /// </summary>
    public async Task<QueueDashboardStats> GetDashboardStatsAsync(QueueConfig config)
    {
        try
        {
            using var connection = await connectionFactory.CreateConnectionAsync();

            var stats = await connection.QueryFirstOrDefaultAsync<QueueDashboardStats>($"""
                SELECT
                    COUNT(*) FILTER (
                        WHERE processed = FALSE
                          AND attempt_count < @MaxAttempts
                          AND (picked_at IS NULL OR picked_at < NOW() - @LeaseTimeout)
                    ) AS Pending,
                    COUNT(*) FILTER (
                        WHERE processed = FALSE
                          AND picked_at IS NOT NULL
                          AND picked_at >= NOW() - @LeaseTimeout
                    ) AS InProgress,
                    COUNT(*) FILTER (WHERE processed = TRUE AND error IS NULL AND completed_at > NOW() - INTERVAL '1 hour') AS CompletedLastHour,
                    COUNT(*) FILTER (WHERE processed = TRUE AND error IS NOT NULL AND completed_at > NOW() - INTERVAL '1 hour') AS FailedLastHour,
                    COUNT(*) FILTER (WHERE processed = TRUE AND error LIKE '[DEAD]%') AS DeadTotal,
                    MAX(EXTRACT(EPOCH FROM (NOW() - created_at))) FILTER (
                        WHERE processed = FALSE
                          AND (picked_at IS NULL OR picked_at < NOW() - @LeaseTimeout)
                    ) AS OldestPendingSeconds,
                    AVG(EXTRACT(EPOCH FROM (completed_at - started_at))) FILTER (
                        WHERE processed = TRUE
                          AND completed_at > NOW() - INTERVAL '1 hour'
                          AND started_at IS NOT NULL
                    ) AS AvgProcessingSeconds
                FROM {config.TableName}
                """,
                new
                {
                    MaxAttempts = config.MaxAttempts,
                    LeaseTimeout = config.LeaseTimeout
                });

            // Update in-memory metrics for stuck detector
            if (stats != null)
            {
                metrics.UpdatePendingCount(config.QueueName, stats.Pending);
            }

            return stats ?? new QueueDashboardStats();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[{Queue}] Failed to get dashboard stats", config.QueueName);
            return new QueueDashboardStats();
        }
    }

    /// <summary>
    /// Clean up old completed/failed tasks.
    /// </summary>
    public async Task<int> CleanupAsync(QueueConfig config, int daysToKeep = 7)
    {
        try
        {
            using var connection = await connectionFactory.CreateConnectionAsync();

            var deleted = await connection.ExecuteAsync($"""
                DELETE FROM {config.TableName}
                WHERE processed = TRUE
                  AND created_at < NOW() - @Days * INTERVAL '1 day'
                """,
                new { Days = daysToKeep });

            if (deleted > 0)
            {
                logger.LogInformation("[{Queue}] Cleaned up {Count} old tasks", config.QueueName, deleted);
            }

            return deleted;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[{Queue}] Failed to cleanup", config.QueueName);
            return 0;
        }
    }

    /// <summary>
    /// Calculate exponential backoff delay with jitter.
    /// </summary>
    private static TimeSpan CalculateBackoff(int attempt, TimeSpan baseDelay, TimeSpan maxDelay)
    {
        // Exponential: 30s, 60s, 120s, 240s... capped at maxDelay
        var exponentialMs = baseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1);
        var cappedMs = Math.Min(exponentialMs, maxDelay.TotalMilliseconds);

        // Add jitter (±20%) to prevent thundering herd
        var jitter = (Random.Shared.NextDouble() - 0.5) * 0.4;
        var finalMs = cappedMs * (1 + jitter);

        return TimeSpan.FromMilliseconds(finalMs);
    }
}

/// <summary>
/// Queue statistics for admin dashboard.
/// </summary>
public class QueueDashboardStats
{
    public int Pending { get; init; }
    public int InProgress { get; init; }
    public int CompletedLastHour { get; init; }
    public int FailedLastHour { get; init; }
    public int DeadTotal { get; init; }
    public double? OldestPendingSeconds { get; init; }
    public double? AvgProcessingSeconds { get; init; }

    public override string ToString()
    {
        var oldest = OldestPendingSeconds.HasValue
            ? $"{OldestPendingSeconds.Value:F0}s"
            : "-";
        var avgProc = AvgProcessingSeconds.HasValue
            ? $"{AvgProcessingSeconds.Value:F1}s"
            : "-";

        return $"pending={Pending}, active={InProgress}, done/h={CompletedLastHour}, fail/h={FailedLastHour}, " +
               $"dead={DeadTotal}, oldest={oldest}, avgTime={avgProc}";
    }
}