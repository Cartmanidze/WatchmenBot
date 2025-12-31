using System.Text;
using Telegram.Bot;
using WatchmenBot.Services;

namespace WatchmenBot.Features.Admin.Commands;

/// <summary>
/// /admin status - show current settings
/// </summary>
public class StatusCommand : AdminCommandBase
{
    private readonly AdminSettingsStore _settings;

    public StatusCommand(
        ITelegramBotClient bot,
        AdminSettingsStore settings,
        ILogger<StatusCommand> logger) : base(bot, logger)
    {
        _settings = settings;
    }

    public override async Task<bool> ExecuteAsync(AdminCommandContext context, CancellationToken ct)
    {
        var settings = await _settings.GetAllSettingsAsync();
        var tz = await _settings.GetTimezoneOffsetAsync();
        var debugMode = await _settings.IsDebugModeEnabledAsync();

        var sb = new StringBuilder();
        sb.AppendLine("<b>⚙️ Текущие настройки</b>");
        sb.AppendLine();
        sb.AppendLine($"🕐 <b>Время саммари:</b> {settings["summary_time"]}");
        sb.AppendLine($"📋 <b>Время отчёта:</b> {settings["report_time"]}");
        sb.AppendLine($"🌍 <b>Часовой пояс:</b> UTC+{tz:hh\\:mm}");
        sb.AppendLine($"🔍 <b>Debug mode:</b> {(debugMode ? "✅ ON" : "❌ OFF")}");
        sb.AppendLine();
        sb.AppendLine($"👤 <b>Admin ID:</b> {_settings.GetAdminUserId()}");

        await SendMessageAsync(context.ChatId, sb.ToString(), ct);
        return true;
    }
}
