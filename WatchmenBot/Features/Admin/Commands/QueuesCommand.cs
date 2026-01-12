using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using WatchmenBot.Infrastructure.Queue;

namespace WatchmenBot.Features.Admin.Commands;

/// <summary>
/// Admin command to show queue statistics and health.
/// Usage: /admin queues
/// </summary>
public class QueuesCommand(
    ITelegramBotClient bot,
    ResilientQueueService queueService,
    QueueMetrics metrics,
    ILogger<QueuesCommand> logger)
    : IAdminCommand
{
    /// <summary>
    /// Queue configurations for all queue types.
    /// </summary>
    private static readonly QueueConfig[] QueueConfigs =
    [
        new() { TableName = "ask_queue", QueueName = "ask", MaxAttempts = 3, LeaseTimeout = TimeSpan.FromMinutes(5) },
        new() { TableName = "summary_queue", QueueName = "summary", MaxAttempts = 3, LeaseTimeout = TimeSpan.FromMinutes(10) },
        new() { TableName = "truth_queue", QueueName = "truth", MaxAttempts = 3, LeaseTimeout = TimeSpan.FromMinutes(5) }
    ];

    public async Task<bool> ExecuteAsync(AdminCommandContext context, CancellationToken ct)
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("<b>📊 Queue Dashboard</b>\n");

            // Get stats for each queue
            foreach (var config in QueueConfigs)
            {
                var dbStats = await queueService.GetDashboardStatsAsync(config);
                var memStats = metrics.GetStats(config.QueueName);

                sb.AppendLine($"<b>{GetQueueEmoji(config.QueueName)} {config.QueueName.ToUpperInvariant()}</b>");
                sb.AppendLine($"├ Pending: {dbStats.Pending}");
                sb.AppendLine($"├ In Progress: {dbStats.InProgress}");
                sb.AppendLine($"├ Done/h: {dbStats.CompletedLastHour}");
                sb.AppendLine($"├ Fail/h: {dbStats.FailedLastHour}");

                if (dbStats.DeadTotal > 0)
                {
                    sb.AppendLine($"├ <b>☠️ Dead: {dbStats.DeadTotal}</b>");
                }

                if (dbStats.OldestPendingSeconds.HasValue && dbStats.OldestPendingSeconds > 60)
                {
                    var oldestMin = dbStats.OldestPendingSeconds.Value / 60;
                    sb.AppendLine($"├ ⚠️ Oldest: {oldestMin:F0}m");
                }

                if (dbStats.AvgProcessingSeconds.HasValue)
                {
                    sb.AppendLine($"└ Avg time: {dbStats.AvgProcessingSeconds:F1}s");
                }
                else
                {
                    sb.AppendLine($"└ Avg time: -");
                }

                // Add in-memory metrics if available
                if (memStats != null && memStats.TotalPicked > 0)
                {
                    sb.AppendLine($"   <i>Session: {memStats.TotalCompleted}/{memStats.TotalPicked} ({memStats.SuccessRate:P0})</i>");
                }

                sb.AppendLine();
            }

            // Check for stuck queues (no activity for 5+ minutes with pending items)
            var stuckQueues = metrics.GetStuckQueues(TimeSpan.FromMinutes(5)).ToList();
            if (stuckQueues.Any())
            {
                sb.AppendLine("<b>⚠️ Stuck Queues:</b>");
                foreach (var (name, idleTime) in stuckQueues)
                {
                    sb.AppendLine($"• {name}: idle {idleTime.TotalMinutes:F0}m");
                }
            }

            await bot.SendMessage(
                chatId: context.ChatId,
                text: sb.ToString(),
                parseMode: ParseMode.Html,
                replyParameters: new ReplyParameters { MessageId = context.Message.MessageId },
                cancellationToken: ct);

            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[QueuesCommand] Failed to get queue stats");
            await bot.SendMessage(
                chatId: context.ChatId,
                text: $"❌ Error: {ex.Message}",
                replyParameters: new ReplyParameters { MessageId = context.Message.MessageId },
                cancellationToken: ct);

            return false;
        }
    }

    private static string GetQueueEmoji(string queueName) => queueName switch
    {
        "ask" => "💬",
        "summary" => "📝",
        "truth" => "🔍",
        _ => "📋"
    };
}