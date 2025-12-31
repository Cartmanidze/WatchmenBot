using Telegram.Bot;
using WatchmenBot.Services;

namespace WatchmenBot.Features.Admin.Commands;

/// <summary>
/// /admin report - send immediate log report
/// </summary>
public class ReportCommand : AdminCommandBase
{
    private readonly DailyLogReportService _reportService;

    public ReportCommand(
        ITelegramBotClient bot,
        DailyLogReportService reportService,
        ILogger<ReportCommand> logger) : base(bot, logger)
    {
        _reportService = reportService;
    }

    public override async Task<bool> ExecuteAsync(AdminCommandContext context, CancellationToken ct)
    {
        await _reportService.SendImmediateReportAsync(context.ChatId, ct);
        return true;
    }
}
