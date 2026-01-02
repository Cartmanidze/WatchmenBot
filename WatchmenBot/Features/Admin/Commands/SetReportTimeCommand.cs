using Telegram.Bot;
using WatchmenBot.Services;

namespace WatchmenBot.Features.Admin.Commands;

/// <summary>
/// /admin set_report_time HH:mm - set daily report time
/// </summary>
public class SetReportTimeCommand(
    ITelegramBotClient bot,
    AdminSettingsStore settings,
    ILogger<SetReportTimeCommand> logger)
    : AdminCommandBase(bot, logger)
{
    public override async Task<bool> ExecuteAsync(AdminCommandContext context, CancellationToken ct)
    {
        if (context.Args.Length == 0)
        {
            await SendMessageAsync(context.ChatId,
                "❌ Укажи время: <code>/admin set_report_time 10:00</code>", ct);
            return true;
        }

        var time = context.Args[0];

        if (!TimeSpan.TryParse(time, out var parsedTime) || parsedTime.TotalHours >= 24)
        {
            await SendMessageAsync(context.ChatId,
                "❌ Неверный формат времени. Используй HH:mm (например: 10:00)", ct);
            return true;
        }

        await settings.SetReportTimeAsync(time);

        await SendMessageAsync(context.ChatId,
            $"✅ Время отчёта в личку изменено на <b>{time}</b>", ct);

        return true;
    }
}
