using WatchmenBot.Features.Webhook.Services;

namespace WatchmenBot.Features.Webhook.Jobs;

/// <summary>
/// Hangfire recurring job that monitors Telegram webhook health
/// and automatically re-registers it if problems are detected.
///
/// Telegram may silently deactivate webhooks after repeated errors (502, 504, timeouts).
/// This watchdog ensures the bot recovers automatically.
///
/// Runs every 5 minutes via Hangfire scheduler.
/// </summary>
public class WebhookWatchdogJob(
    TelegramWebhookService webhookService,
    ILogger<WebhookWatchdogJob> logger)
{
    /// <summary>
    /// Check webhook status and recover if necessary.
    /// Called by Hangfire every 5 minutes.
    /// </summary>
    public async Task ExecuteAsync()
    {
        try
        {
            var result = await webhookService.CheckAndRecoverAsync();

            switch (result.Action)
            {
                case WebhookRecoveryAction.Skip:
                    logger.LogDebug("[WebhookWatchdog] Skipped: {Reason}", result.Reason);
                    break;

                case WebhookRecoveryAction.Healthy:
                    logger.LogDebug("[WebhookWatchdog] Webhook healthy. Pending: {Pending}",
                        result.Status.PendingUpdateCount);
                    break;

                case WebhookRecoveryAction.Recovered:
                    logger.LogWarning("[WebhookWatchdog] Recovered: {Reason}. Success: {Success}",
                        result.Reason, result.WasRecovered);
                    break;

                case WebhookRecoveryAction.Alert:
                    logger.LogError("[WebhookWatchdog] Alert: {Reason}", result.Reason);
                    break;

                case WebhookRecoveryAction.Error:
                    logger.LogError("[WebhookWatchdog] Error: {Reason}", result.Reason);
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[WebhookWatchdog] Unhandled exception during check");
        }
    }
}
