using System.Threading.Channels;
using Npgsql;

namespace WatchmenBot.Infrastructure.Database;

/// <summary>
/// Service for receiving PostgreSQL NOTIFY events in real-time.
/// Maintains a dedicated connection for LISTEN and forwards notifications via Channels.
/// </summary>
public class PostgresNotificationService : IHostedService, IDisposable
{
    private readonly string _connectionString;
    private readonly ILogger<PostgresNotificationService> _logger;

    private NpgsqlConnection? _connection;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;

    // Channels for each queue type
    private readonly Channel<int> _askQueueChannel = Channel.CreateUnbounded<int>();
    private readonly Channel<int> _summaryQueueChannel = Channel.CreateUnbounded<int>();
    private readonly Channel<int> _truthQueueChannel = Channel.CreateUnbounded<int>();

    /// <summary>
    /// Reader for ask_queue notifications (item IDs)
    /// </summary>
    public ChannelReader<int> AskQueueNotifications => _askQueueChannel.Reader;

    /// <summary>
    /// Reader for summary_queue notifications (item IDs)
    /// </summary>
    public ChannelReader<int> SummaryQueueNotifications => _summaryQueueChannel.Reader;

    /// <summary>
    /// Reader for truth_queue notifications (item IDs)
    /// </summary>
    public ChannelReader<int> TruthQueueNotifications => _truthQueueChannel.Reader;

    public PostgresNotificationService(
        IConfiguration configuration,
        ILogger<PostgresNotificationService> logger)
    {
        _connectionString = configuration.GetConnectionString("Default")
            ?? configuration["Database:ConnectionString"]
            ?? throw new InvalidOperationException("Connection string not configured");
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            await ConnectAndListenAsync(_cts.Token);
            _logger.LogInformation("[PgNotify] Started listening to queue channels");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PgNotify] Failed to start notification listener");
            // Don't throw - workers will fall back to polling
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("[PgNotify] Stopping notification listener");

        _cts?.Cancel();

        if (_listenTask != null)
        {
            try
            {
                await _listenTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("[PgNotify] Listen task did not complete in time");
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
        }

        _askQueueChannel.Writer.Complete();
        _summaryQueueChannel.Writer.Complete();
        _truthQueueChannel.Writer.Complete();

        await CloseConnectionAsync();
    }

    private async Task ConnectAndListenAsync(CancellationToken ct)
    {
        _connection = new NpgsqlConnection(_connectionString);
        await _connection.OpenAsync(ct);

        // Subscribe to notification events
        _connection.Notification += OnNotification;

        // Start listening to channels
        await using var cmd = new NpgsqlCommand("LISTEN ask_queue_channel; LISTEN summary_queue_channel; LISTEN truth_queue_channel;", _connection);
        await cmd.ExecuteNonQueryAsync(ct);

        // Start background task to keep connection alive and receive notifications
        _listenTask = Task.Run(() => ListenLoopAsync(ct), ct);
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Wait for notifications (with timeout for health checks)
                await _connection!.WaitAsync(TimeSpan.FromSeconds(30), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[PgNotify] Error in listen loop, attempting reconnect");

                // Try to reconnect
                await Task.Delay(TimeSpan.FromSeconds(5), ct);

                try
                {
                    await ReconnectAsync(ct);
                }
                catch (Exception reconnectEx)
                {
                    _logger.LogError(reconnectEx, "[PgNotify] Reconnect failed, will retry");
                }
            }
        }
    }

    private void OnNotification(object sender, NpgsqlNotificationEventArgs e)
    {
        _logger.LogDebug("[PgNotify] Received notification on channel {Channel}: {Payload}",
            e.Channel, e.Payload);

        if (!int.TryParse(e.Payload, out var itemId))
        {
            _logger.LogWarning("[PgNotify] Invalid payload (not an int): {Payload}", e.Payload);
            return;
        }

        var written = e.Channel switch
        {
            "ask_queue_channel" => _askQueueChannel.Writer.TryWrite(itemId),
            "summary_queue_channel" => _summaryQueueChannel.Writer.TryWrite(itemId),
            "truth_queue_channel" => _truthQueueChannel.Writer.TryWrite(itemId),
            _ => false
        };

        if (!written)
        {
            _logger.LogWarning("[PgNotify] Failed to write notification to channel {Channel}", e.Channel);
        }
    }

    private async Task ReconnectAsync(CancellationToken ct)
    {
        await CloseConnectionAsync();
        await ConnectAndListenAsync(ct);
        _logger.LogInformation("[PgNotify] Reconnected to PostgreSQL");
    }

    private async Task CloseConnectionAsync()
    {
        if (_connection != null)
        {
            _connection.Notification -= OnNotification;

            try
            {
                await _connection.CloseAsync();
                await _connection.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[PgNotify] Error closing connection");
            }

            _connection = null;
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _connection?.Dispose();
    }
}
