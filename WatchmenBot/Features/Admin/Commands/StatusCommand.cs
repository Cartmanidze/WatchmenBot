using System.Text;
using Telegram.Bot;
using WatchmenBot.Infrastructure.Settings;
using WatchmenBot.Features.Messages.Services;
using WatchmenBot.Features.Admin.Services;
using WatchmenBot.Features.Webhook.Services;

namespace WatchmenBot.Features.Admin.Commands;

/// <summary>
/// /admin status - show current settings
/// </summary>
public class StatusCommand(
    ITelegramBotClient bot,
    AdminSettingsStore settings,
    ILogger<StatusCommand> logger)
    : AdminCommandBase(bot, logger)
{
    public override async Task<bool> ExecuteAsync(AdminCommandContext context, CancellationToken ct)
    {
        var settings1 = await settings.GetAllSettingsAsync();
        var tz = await settings.GetTimezoneOffsetAsync();
        var debugMode = await settings.IsDebugModeEnabledAsync();

        var sb = new StringBuilder();
        sb.AppendLine("<b>⚙️ Текущие настройки</b>");
        sb.AppendLine();
        sb.AppendLine($"🕐 <b>Время саммари:</b> {settings1["summary_time"]}");
        sb.AppendLine($"📋 <b>Время отчёта:</b> {settings1["report_time"]}");
        sb.AppendLine($"🌍 <b>Часовой пояс:</b> UTC+{tz:hh\\:mm}");
        sb.AppendLine($"🔍 <b>Debug mode:</b> {(debugMode ? "✅ ON" : "❌ OFF")}");
        sb.AppendLine();
        sb.AppendLine($"👤 <b>Admin ID:</b> {settings.GetAdminUserId()}");

        await SendMessageAsync(context.ChatId, sb.ToString(), ct);
        return true;
    }
}
