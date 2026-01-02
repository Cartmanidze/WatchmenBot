using Telegram.Bot;
using WatchmenBot.Features.Admin.Services;

namespace WatchmenBot.Features.Admin.Commands;

/// <summary>
/// /admin report - send immediate log report
/// </summary>
public class ReportCommand(
    ITelegramBotClient bot,
    DailyLogReportService reportService,
    ILogger<ReportCommand> logger)
    : AdminCommandBase(bot, logger)
{
    public override async Task<bool> ExecuteAsync(AdminCommandContext context, CancellationToken ct)
    {
        await reportService.SendImmediateReportAsync(context.ChatId, ct);
        return true;
    }
}
