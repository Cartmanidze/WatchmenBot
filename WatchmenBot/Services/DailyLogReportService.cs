using Telegram.Bot;
using Telegram.Bot.Types.Enums;

namespace WatchmenBot.Services;

public class DailyLogReportService(
    ITelegramBotClient bot,
    AdminSettingsStore settings,
    LogCollector logCollector,
    IServiceProvider serviceProvider,
    ILogger<DailyLogReportService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var adminUserId = settings.GetAdminUserId();
        if (adminUserId == 0)
        {
            logger.LogWarning("[LogReport] Admin:UserId not configured. Daily reports disabled.");
            return;
        }

        logger.LogInformation("[LogReport] Service STARTED. Reports will be sent to user {UserId}", adminUserId);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var reportTime = await settings.GetReportTimeAsync();
                var timezoneOffset = await settings.GetTimezoneOffsetAsync();

                var nextRun = GetNextRunTime(reportTime, timezoneOffset);
                var delay = nextRun - DateTimeOffset.UtcNow;

                if (delay < TimeSpan.Zero)
                    delay = TimeSpan.FromMinutes(1);

                logger.LogInformation("[LogReport] Next report at {Time} (in {Hours:F1}h)",
                    nextRun.ToOffset(timezoneOffset).ToString("yyyy-MM-dd HH:mm"), delay.TotalHours);

                await Task.Delay(delay, stoppingToken);

                if (stoppingToken.IsCancellationRequested)
                    break;

                await SendDailyReportAsync(adminUserId, timezoneOffset, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[LogReport] Error in report loop");
                logCollector.LogError("LogReportService", "Error in report loop", ex);

                // Wait before retrying
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }

        logger.LogInformation("[LogReport] Service STOPPED");
    }

    private static DateTimeOffset GetNextRunTime(string timeStr, TimeSpan timezoneOffset)
    {
        if (!TimeSpan.TryParse(timeStr, out var targetTime))
            targetTime = new TimeSpan(10, 0, 0);

        var now = DateTimeOffset.UtcNow;
        var nowInTimezone = now.ToOffset(timezoneOffset);

        var todayTarget = new DateTimeOffset(
            nowInTimezone.Year, nowInTimezone.Month, nowInTimezone.Day,
            targetTime.Hours, targetTime.Minutes, 0, timezoneOffset);

        // If we've passed the target time today, schedule for tomorrow
        if (todayTarget <= now)
            todayTarget = todayTarget.AddDays(1);

        return todayTarget.ToUniversalTime();
    }

    private async Task SendDailyReportAsync(long adminUserId, TimeSpan timezoneOffset, CancellationToken ct)
    {
        try
        {
            var report = logCollector.GetReportSinceLastTime();
            var message = report.ToTelegramHtml(timezoneOffset);

            // Add usage info
            var usageInfo = await GetUsageInfoAsync(ct);
            if (usageInfo != null)
            {
                message += "\n" + usageInfo.ToTelegramHtml();
            }

            // Add embeddings usage
            var embeddingStats = GetEmbeddingStats();
            if (embeddingStats != null && embeddingStats.TotalRequests > 0)
            {
                message += "\n" + embeddingStats.ToTelegramHtml();
            }

            await bot.SendMessage(
                chatId: adminUserId,
                text: message,
                parseMode: ParseMode.Html,
                cancellationToken: ct);

            logger.LogInformation("[LogReport] Daily report sent to admin {UserId}", adminUserId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[LogReport] Failed to send daily report to admin {UserId}", adminUserId);
        }
    }

    private async Task<UsageInfo?> GetUsageInfoAsync(CancellationToken ct)
    {
        try
        {
            using var scope = serviceProvider.CreateScope();
            var usageService = scope.ServiceProvider.GetRequiredService<OpenRouterUsageService>();
            return await usageService.GetUsageInfoAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[LogReport] Failed to get usage info");
            return null;
        }
    }

    private EmbeddingUsageStats? GetEmbeddingStats()
    {
        try
        {
            using var scope = serviceProvider.CreateScope();
            var embeddingClient = scope.ServiceProvider.GetRequiredService<EmbeddingClient>();
            return embeddingClient.GetUsageStats();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[LogReport] Failed to get embedding stats");
            return null;
        }
    }

    /// <summary>
    /// Send immediate report (for /admin report command)
    /// </summary>
    public async Task SendImmediateReportAsync(long chatId, CancellationToken ct = default)
    {
        try
        {
            var timezoneOffset = await settings.GetTimezoneOffsetAsync();
            var report = logCollector.GetFullReport();
            var message = report.ToTelegramHtml(timezoneOffset);

            // Add usage info
            var usageInfo = await GetUsageInfoAsync(ct);
            if (usageInfo != null)
            {
                message += "\n" + usageInfo.ToTelegramHtml();
            }

            // Add embeddings usage
            var embeddingStats = GetEmbeddingStats();
            if (embeddingStats != null && embeddingStats.TotalRequests > 0)
            {
                message += "\n" + embeddingStats.ToTelegramHtml();
            }

            await bot.SendMessage(
                chatId: chatId,
                text: message,
                parseMode: ParseMode.Html,
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[LogReport] Failed to send immediate report");
            throw;
        }
    }
}
