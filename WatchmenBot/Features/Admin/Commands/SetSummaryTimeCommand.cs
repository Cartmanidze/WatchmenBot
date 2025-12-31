using Telegram.Bot;
using WatchmenBot.Services;

namespace WatchmenBot.Features.Admin.Commands;

/// <summary>
/// /admin set_summary_time HH:mm - set daily summary time
/// </summary>
public class SetSummaryTimeCommand : AdminCommandBase
{
    private readonly AdminSettingsStore _settings;

    public SetSummaryTimeCommand(
        ITelegramBotClient bot,
        AdminSettingsStore settings,
        ILogger<SetSummaryTimeCommand> logger) : base(bot, logger)
    {
        _settings = settings;
    }

    public override async Task<bool> ExecuteAsync(AdminCommandContext context, CancellationToken ct)
    {
        if (context.Args.Length == 0)
        {
            await SendMessageAsync(context.ChatId,
                "❌ Укажи время: <code>/admin set_summary_time 21:00</code>", ct);
            return true;
        }

        var time = context.Args[0];

        if (!TimeSpan.TryParse(time, out var parsedTime) || parsedTime.TotalHours >= 24)
        {
            await SendMessageAsync(context.ChatId,
                "❌ Неверный формат времени. Используй HH:mm (например: 21:00)", ct);
            return true;
        }

        await _settings.SetSummaryTimeAsync(time);

        await SendMessageAsync(context.ChatId,
            $"✅ Время ежедневного саммари изменено на <b>{time}</b>\n\n⚠️ Изменения вступят в силу после перезапуска бота.", ct);

        return true;
    }
}
