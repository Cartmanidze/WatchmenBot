using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace WatchmenBot.Features.Webhook.Services;

/// <summary>
/// ASP.NET Core Health Check for Telegram webhook status.
/// Reports Healthy/Degraded/Unhealthy based on webhook configuration and status.
///
/// Integrates with /health endpoint for external monitoring.
/// </summary>
public class TelegramWebhookHealthCheck(TelegramWebhookService webhookService) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var status = await webhookService.GetStatusAsync(cancellationToken);

        // Not configured
        if (!status.IsConfigured)
        {
            return HealthCheckResult.Degraded(
                status.Error ?? "Webhook not configured",
                data: BuildData(status));
        }

        // API error
        if (status.Error != null)
        {
            return HealthCheckResult.Unhealthy(
                $"API error: {status.Error}",
                data: BuildData(status));
        }

        // URL is empty (webhook deleted)
        if (!status.IsActive)
        {
            return HealthCheckResult.Unhealthy(
                "Webhook URL is empty - bot is NOT receiving messages!",
                data: BuildData(status));
        }

        // URL mismatch
        if (!status.IsUrlMatch)
        {
            return HealthCheckResult.Unhealthy(
                $"URL mismatch. Expected: {status.ExpectedUrl}, Actual: {status.CurrentUrl}",
                data: BuildData(status));
        }

        // Critical pending updates
        if (status.PendingUpdateCount > TelegramWebhookService.PendingUpdatesCritical)
        {
            return HealthCheckResult.Unhealthy(
                $"Critical: {status.PendingUpdateCount} pending updates - processing may be stalled",
                data: BuildData(status));
        }

        // Recent errors
        if (!string.IsNullOrEmpty(status.LastErrorMessage))
        {
            var errorAge = status.LastErrorDate.HasValue
                ? DateTime.UtcNow - status.LastErrorDate.Value
                : TimeSpan.Zero;

            if (errorAge < TimeSpan.FromMinutes(30))
            {
                return HealthCheckResult.Degraded(
                    $"Recent error ({errorAge.TotalMinutes:F0}m ago): {status.LastErrorMessage}",
                    data: BuildData(status));
            }
        }

        // Warning level pending updates
        if (status.PendingUpdateCount > TelegramWebhookService.PendingUpdatesWarning)
        {
            return HealthCheckResult.Degraded(
                $"High pending updates: {status.PendingUpdateCount}",
                data: BuildData(status));
        }

        // All good!
        return HealthCheckResult.Healthy(
            $"Webhook active: {status.CurrentUrl}",
            data: BuildData(status));
    }

    private static Dictionary<string, object> BuildData(WebhookStatus status)
    {
        var data = new Dictionary<string, object>();

        if (!string.IsNullOrEmpty(status.ExpectedUrl))
            data["expected_url"] = status.ExpectedUrl;

        if (!string.IsNullOrEmpty(status.CurrentUrl))
            data["url"] = status.CurrentUrl;

        data["pending_updates"] = status.PendingUpdateCount;

        if (!string.IsNullOrEmpty(status.LastErrorMessage))
        {
            data["last_error"] = status.LastErrorMessage;
            if (status.LastErrorDate.HasValue)
                data["error_date"] = status.LastErrorDate.Value.ToString("O");
        }

        if (status.MaxConnections > 0)
            data["max_connections"] = status.MaxConnections;

        if (!string.IsNullOrEmpty(status.IpAddress))
            data["ip_address"] = status.IpAddress;

        return data;
    }
}
