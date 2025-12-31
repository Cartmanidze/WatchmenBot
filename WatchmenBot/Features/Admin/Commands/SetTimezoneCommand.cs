using Telegram.Bot;
using WatchmenBot.Services;

namespace WatchmenBot.Features.Admin.Commands;

/// <summary>
/// /admin set_timezone +N - set timezone offset
/// </summary>
public class SetTimezoneCommand : AdminCommandBase
{
    private readonly AdminSettingsStore _settings;

    public SetTimezoneCommand(
        ITelegramBotClient bot,
        AdminSettingsStore settings,
        ILogger<SetTimezoneCommand> logger) : base(bot, logger)
    {
        _settings = settings;
    }

    public override async Task<bool> ExecuteAsync(AdminCommandContext context, CancellationToken ct)
    {
        if (context.Args.Length == 0)
        {
            await SendMessageAsync(context.ChatId,
                "❌ Укажи часовой пояс: <code>/admin set_timezone +6</code>", ct);
            return true;
        }

        var offset = context.Args[0];

        // Accept formats: +6, +06, +06:00, 6
        var cleanOffset = offset.TrimStart('+');
        if (!cleanOffset.Contains(':'))
            cleanOffset += ":00";

        if (!TimeSpan.TryParse(cleanOffset, out var parsedOffset) ||
            parsedOffset.TotalHours > 14 || parsedOffset.TotalHours < -12)
        {
            await SendMessageAsync(context.ChatId,
                "❌ Неверный часовой пояс. Используй формат: +6, +06:00 и т.д.", ct);
            return true;
        }

        await _settings.SetTimezoneOffsetAsync($"+{cleanOffset}");

        await SendMessageAsync(context.ChatId,
            $"✅ Часовой пояс изменён на <b>UTC+{parsedOffset:hh\\:mm}</b>", ct);

        return true;
    }
}
