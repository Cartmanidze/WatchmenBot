using Telegram.Bot;
using Telegram.Bot.Types.Enums;
using WatchmenBot.Features.Admin.Services;

namespace WatchmenBot.Features.Webhook.Services;

/// <summary>
/// Actions taken during webhook recovery check.
/// </summary>
public enum WebhookRecoveryAction
{
    /// <summary>Webhook not configured, skipped check.</summary>
    Skip,

    /// <summary>Webhook is healthy, no action needed.</summary>
    Healthy,

    /// <summary>Webhook was broken and successfully recovered.</summary>
    Recovered,

    /// <summary>Alert condition detected (e.g., critical pending updates).</summary>
    Alert,

    /// <summary>Error occurred during check.</summary>
    Error
}

/// <summary>
/// Centralized service for Telegram webhook management.
/// Handles registration, status checking, health monitoring, and protection against conflicts.
/// </summary>
public class TelegramWebhookService : IDisposable
{
    // Thresholds for health status
    public const int PendingUpdatesWarning = 100;
    public const int PendingUpdatesCritical = 500;
    public const int RecentErrorThresholdMinutes = 30;

    private readonly ITelegramBotClient _bot;
    private readonly IHostEnvironment _environment;
    private readonly LogCollector _logCollector;
    private readonly ILogger<TelegramWebhookService> _logger;

    // Configuration values (initialized once in constructor)
    private readonly string? _webhookUrl;
    private readonly string? _secretToken;
    private readonly long _adminUserId;

    // Thread-safety: prevent concurrent webhook registration
    private readonly SemaphoreSlim _registrationLock = new(1, 1);

    public TelegramWebhookService(
        ITelegramBotClient bot,
        IConfiguration configuration,
        IHostEnvironment environment,
        LogCollector logCollector,
        ILogger<TelegramWebhookService> logger)
    {
        _bot = bot;
        _environment = environment;
        _logCollector = logCollector;
        _logger = logger;

        // Initialize configuration values once (thread-safe)
        _webhookUrl = configuration["Telegram:WebhookUrl"];
        _secretToken = configuration["Telegram:WebhookSecret"];

        // AdminUserId: try Telegram:AdminUserId first, fallback to Admin:UserId
        _adminUserId = configuration.GetValue<long>("Telegram:AdminUserId", 0);
        if (_adminUserId == 0)
        {
            _adminUserId = configuration.GetValue<long>("Admin:UserId", 0);
        }
    }

    /// <summary>
    /// Configured webhook URL from settings.
    /// </summary>
    public string? WebhookUrl => _webhookUrl;

    /// <summary>
    /// Configured webhook secret token.
    /// </summary>
    public string? SecretToken => _secretToken;

    /// <summary>
    /// Admin user ID for notifications.
    /// </summary>
    public long AdminUserId => _adminUserId;

    /// <summary>
    /// Get current webhook status from Telegram API.
    /// </summary>
    public async Task<WebhookStatus> GetStatusAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(WebhookUrl))
        {
            return new WebhookStatus
            {
                IsConfigured = false,
                Error = "Telegram:WebhookUrl not configured"
            };
        }

        try
        {
            var info = await _bot.GetWebhookInfo(ct);

            return new WebhookStatus
            {
                IsConfigured = true,
                CurrentUrl = info.Url,
                ExpectedUrl = WebhookUrl,
                IsUrlMatch = info.Url == WebhookUrl,
                IsActive = !string.IsNullOrEmpty(info.Url),
                PendingUpdateCount = info.PendingUpdateCount,
                LastErrorMessage = info.LastErrorMessage,
                LastErrorDate = info.LastErrorDate,
                MaxConnections = info.MaxConnections ?? 0,
                IpAddress = info.IpAddress
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[WebhookService] Failed to get webhook status");
            return new WebhookStatus
            {
                IsConfigured = true,
                Error = ex.Message
            };
        }
    }

    /// <summary>
    /// Check if webhook needs to be registered or re-registered.
    /// Returns null if healthy, or a reason string if action needed.
    /// </summary>
    public async Task<string?> CheckHealthAsync(CancellationToken ct = default)
    {
        var status = await GetStatusAsync(ct);

        if (!status.IsConfigured)
            return status.Error;

        if (status.Error != null)
            return $"API error: {status.Error}";

        if (!status.IsActive)
            return "Webhook URL is empty";

        if (!status.IsUrlMatch)
            return $"URL mismatch (current: {status.CurrentUrl})";

        if (!string.IsNullOrEmpty(status.LastErrorMessage))
        {
            var errorAge = status.LastErrorDate.HasValue
                ? DateTime.UtcNow - status.LastErrorDate.Value
                : TimeSpan.Zero;

            if (errorAge < TimeSpan.FromMinutes(RecentErrorThresholdMinutes))
                return $"Recent error ({errorAge.TotalMinutes:F0}m ago): {status.LastErrorMessage}";
        }

        return null; // Healthy
    }

    /// <summary>
    /// Register webhook with Telegram.
    /// Thread-safe: uses SemaphoreSlim to prevent concurrent registration.
    /// </summary>
    public async Task<bool> RegisterAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(WebhookUrl))
        {
            _logger.LogWarning("[WebhookService] Cannot register: WebhookUrl not configured");
            return false;
        }

        // Prevent concurrent registration attempts
        if (!await _registrationLock.WaitAsync(TimeSpan.FromSeconds(5), ct))
        {
            _logger.LogDebug("[WebhookService] Registration already in progress, skipping");
            return true; // Assume success if another registration is happening
        }

        try
        {
            await _bot.SetWebhook(
                url: WebhookUrl,
                allowedUpdates: [UpdateType.Message],
                secretToken: string.IsNullOrWhiteSpace(SecretToken) ? null : SecretToken,
                maxConnections: 40,
                cancellationToken: ct);

            _logger.LogInformation("[WebhookService] Webhook registered: {Url}", WebhookUrl);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[WebhookService] Failed to register webhook");
            _logCollector.LogError("WebhookService", "Registration failed", ex);
            return false;
        }
        finally
        {
            _registrationLock.Release();
        }
    }

    /// <summary>
    /// Ensure webhook is registered and healthy. Re-registers if needed.
    /// Returns true if webhook is now healthy.
    /// </summary>
    public async Task<bool> EnsureRegisteredAsync(CancellationToken ct = default)
    {
        var problem = await CheckHealthAsync(ct);

        if (problem == null)
        {
            _logger.LogDebug("[WebhookService] Webhook healthy");
            return true;
        }

        _logger.LogWarning("[WebhookService] Problem detected: {Problem}. Re-registering...", problem);
        _logCollector.LogWarning("WebhookService", $"Re-registering: {problem}");

        return await RegisterAsync(ct);
    }

    /// <summary>
    /// Check and recover webhook if needed. Used by watchdog job.
    /// Returns recovery result with details.
    /// </summary>
    public async Task<WebhookRecoveryResult> CheckAndRecoverAsync(CancellationToken ct = default)
    {
        var status = await GetStatusAsync(ct);
        var result = new WebhookRecoveryResult { Status = status };

        if (!status.IsConfigured)
        {
            result.Action = WebhookRecoveryAction.Skip;
            result.Reason = status.Error ?? "Not configured";
            return result;
        }

        if (status.Error != null)
        {
            result.Action = WebhookRecoveryAction.Error;
            result.Reason = status.Error;
            return result;
        }

        // Check 1: URL is empty (webhook was deleted)
        if (!status.IsActive)
        {
            result.Action = WebhookRecoveryAction.Recovered;
            result.Reason = "Webhook URL was empty";
            result.WasRecovered = await RegisterAsync(ct);

            if (result.WasRecovered)
                await NotifyAdminAsync($"⚠️ Webhook был сброшен и автоматически восстановлен.\n" +
                                       $"Pending updates: {status.PendingUpdateCount}");
            return result;
        }

        // Check 2: URL mismatch
        if (!status.IsUrlMatch)
        {
            result.Action = WebhookRecoveryAction.Recovered;
            result.Reason = $"URL mismatch: {status.CurrentUrl}";
            result.WasRecovered = await RegisterAsync(ct);
            return result;
        }

        // Check 3: Recent errors
        if (!string.IsNullOrEmpty(status.LastErrorMessage))
        {
            var errorAge = status.LastErrorDate.HasValue
                ? DateTime.UtcNow - status.LastErrorDate.Value
                : TimeSpan.Zero;

            if (errorAge < TimeSpan.FromMinutes(RecentErrorThresholdMinutes))
            {
                result.Action = WebhookRecoveryAction.Recovered;
                result.Reason = $"Recent error: {status.LastErrorMessage}";
                result.WasRecovered = await RegisterAsync(ct);
                return result;
            }
        }

        // Check 4: Critical pending updates
        if (status.PendingUpdateCount > PendingUpdatesCritical)
        {
            result.Action = WebhookRecoveryAction.Alert;
            result.Reason = $"Critical pending updates: {status.PendingUpdateCount}";

            await NotifyAdminAsync($"🚨 Критическое количество pending updates: {status.PendingUpdateCount}\n" +
                                   $"Возможно, обработка сообщений остановилась.");
            return result;
        }

        // All good
        result.Action = WebhookRecoveryAction.Healthy;
        return result;
    }

    /// <summary>
    /// Delete webhook (switches to polling mode).
    /// Protected: will refuse in production environment unless forced.
    /// </summary>
    public async Task<bool> DeleteAsync(bool force = false, CancellationToken ct = default)
    {
        // Protection: prevent accidental deletion in production
        if (_environment.IsProduction() && !force)
        {
            _logger.LogError("[WebhookService] BLOCKED: Attempted to delete webhook in PRODUCTION! " +
                            "Use force=true if this is intentional.");
            return false;
        }

        // Additional protection: warn if webhook URL suggests production
        if (!force && !string.IsNullOrEmpty(WebhookUrl) &&
            (WebhookUrl.Contains("sslip.io") || !WebhookUrl.Contains("localhost")))
        {
            _logger.LogWarning("[WebhookService] WARNING: Deleting webhook for non-localhost URL: {Url}. " +
                              "This may affect production!", WebhookUrl);
        }

        try
        {
            await _bot.DeleteWebhook(cancellationToken: ct);
            _logger.LogInformation("[WebhookService] Webhook deleted");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[WebhookService] Failed to delete webhook");
            return false;
        }
    }

    /// <summary>
    /// Send notification to admin.
    /// </summary>
    public async Task NotifyAdminAsync(string message)
    {
        if (AdminUserId <= 0)
        {
            _logger.LogDebug("[WebhookService] Cannot notify admin: UserId not configured");
            return;
        }

        try
        {
            await _bot.SendMessage(AdminUserId, message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[WebhookService] Failed to notify admin");
        }
    }

    public void Dispose()
    {
        _registrationLock.Dispose();
    }
}

/// <summary>
/// Current webhook status information.
/// </summary>
public class WebhookStatus
{
    public bool IsConfigured { get; init; }
    public string? CurrentUrl { get; init; }
    public string? ExpectedUrl { get; init; }
    public bool IsUrlMatch { get; init; }
    public bool IsActive { get; init; }
    public int PendingUpdateCount { get; init; }
    public string? LastErrorMessage { get; init; }
    public DateTime? LastErrorDate { get; init; }
    public int MaxConnections { get; init; }
    public string? IpAddress { get; init; }
    public string? Error { get; init; }

    public bool IsHealthy => IsConfigured && IsActive && IsUrlMatch &&
                             string.IsNullOrEmpty(Error) &&
                             PendingUpdateCount < TelegramWebhookService.PendingUpdatesCritical;
}

/// <summary>
/// Result of webhook recovery check.
/// </summary>
public class WebhookRecoveryResult
{
    public WebhookStatus Status { get; init; } = new();
    public WebhookRecoveryAction Action { get; set; } = WebhookRecoveryAction.Skip;
    public string? Reason { get; set; }
    public bool WasRecovered { get; set; }
}
