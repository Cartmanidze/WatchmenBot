using System.Text;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using WatchmenBot.Services;

namespace WatchmenBot.Features.Admin;

public class AdminCommandHandler
{
    private readonly ITelegramBotClient _bot;
    private readonly AdminSettingsStore _settings;
    private readonly LogCollector _logCollector;
    private readonly DailyLogReportService _reportService;
    private readonly ILogger<AdminCommandHandler> _logger;

    public AdminCommandHandler(
        ITelegramBotClient bot,
        AdminSettingsStore settings,
        LogCollector logCollector,
        DailyLogReportService reportService,
        ILogger<AdminCommandHandler> logger)
    {
        _bot = bot;
        _settings = settings;
        _logCollector = logCollector;
        _reportService = reportService;
        _logger = logger;
    }

    public async Task<bool> HandleAsync(Message message, CancellationToken ct)
    {
        var text = message.Text?.Trim() ?? "";
        var userId = message.From?.Id ?? 0;
        var username = message.From?.Username;

        // Check admin access
        if (!_settings.IsAdmin(userId, username))
        {
            _logger.LogWarning("[Admin] Unauthorized access attempt from {UserId} (@{Username})", userId, username);
            return false;
        }

        // Parse command
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            await SendHelpAsync(message.Chat.Id, ct);
            return true;
        }

        var subCommand = parts[1].ToLowerInvariant();

        try
        {
            return subCommand switch
            {
                "status" => await HandleStatusAsync(message.Chat.Id, ct),
                "report" => await HandleReportAsync(message.Chat.Id, ct),
                "set_summary_time" when parts.Length >= 3 => await HandleSetSummaryTimeAsync(message.Chat.Id, parts[2], ct),
                "set_report_time" when parts.Length >= 3 => await HandleSetReportTimeAsync(message.Chat.Id, parts[2], ct),
                "set_timezone" when parts.Length >= 3 => await HandleSetTimezoneAsync(message.Chat.Id, parts[2], ct),
                "help" => await SendHelpAsync(message.Chat.Id, ct),
                _ => await SendHelpAsync(message.Chat.Id, ct)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Admin] Error handling command: {Command}", text);
            _logCollector.LogError("AdminCommand", $"Error: {text}", ex);

            await _bot.SendTextMessageAsync(
                chatId: message.Chat.Id,
                text: $"❌ Ошибка: {ex.Message}",
                cancellationToken: ct);
            return true;
        }
    }

    private async Task<bool> HandleStatusAsync(long chatId, CancellationToken ct)
    {
        var settings = await _settings.GetAllSettingsAsync();
        var tz = await _settings.GetTimezoneOffsetAsync();

        var sb = new StringBuilder();
        sb.AppendLine("<b>⚙️ Текущие настройки</b>");
        sb.AppendLine();
        sb.AppendLine($"🕐 <b>Время саммари:</b> {settings["summary_time"]}");
        sb.AppendLine($"📋 <b>Время отчёта:</b> {settings["report_time"]}");
        sb.AppendLine($"🌍 <b>Часовой пояс:</b> UTC+{tz:hh\\:mm}");
        sb.AppendLine();
        sb.AppendLine($"👤 <b>Admin ID:</b> {_settings.GetAdminUserId()}");

        await _bot.SendTextMessageAsync(
            chatId: chatId,
            text: sb.ToString(),
            parseMode: ParseMode.Html,
            cancellationToken: ct);

        return true;
    }

    private async Task<bool> HandleReportAsync(long chatId, CancellationToken ct)
    {
        await _reportService.SendImmediateReportAsync(chatId, ct);
        return true;
    }

    private async Task<bool> HandleSetSummaryTimeAsync(long chatId, string time, CancellationToken ct)
    {
        if (!TimeSpan.TryParse(time, out var parsedTime) || parsedTime.TotalHours >= 24)
        {
            await _bot.SendTextMessageAsync(
                chatId: chatId,
                text: "❌ Неверный формат времени. Используй HH:mm (например: 21:00)",
                cancellationToken: ct);
            return true;
        }

        await _settings.SetSummaryTimeAsync(time);

        await _bot.SendTextMessageAsync(
            chatId: chatId,
            text: $"✅ Время ежедневного саммари изменено на <b>{time}</b>\n\n⚠️ Изменения вступят в силу после перезапуска бота.",
            parseMode: ParseMode.Html,
            cancellationToken: ct);

        return true;
    }

    private async Task<bool> HandleSetReportTimeAsync(long chatId, string time, CancellationToken ct)
    {
        if (!TimeSpan.TryParse(time, out var parsedTime) || parsedTime.TotalHours >= 24)
        {
            await _bot.SendTextMessageAsync(
                chatId: chatId,
                text: "❌ Неверный формат времени. Используй HH:mm (например: 10:00)",
                cancellationToken: ct);
            return true;
        }

        await _settings.SetReportTimeAsync(time);

        await _bot.SendTextMessageAsync(
            chatId: chatId,
            text: $"✅ Время отчёта в личку изменено на <b>{time}</b>",
            parseMode: ParseMode.Html,
            cancellationToken: ct);

        return true;
    }

    private async Task<bool> HandleSetTimezoneAsync(long chatId, string offset, CancellationToken ct)
    {
        // Accept formats: +6, +06, +06:00, 6
        var cleanOffset = offset.TrimStart('+');
        if (!cleanOffset.Contains(':'))
            cleanOffset += ":00";

        if (!TimeSpan.TryParse(cleanOffset, out var parsedOffset) || parsedOffset.TotalHours > 14 || parsedOffset.TotalHours < -12)
        {
            await _bot.SendTextMessageAsync(
                chatId: chatId,
                text: "❌ Неверный часовой пояс. Используй формат: +6, +06:00 и т.д.",
                cancellationToken: ct);
            return true;
        }

        await _settings.SetTimezoneOffsetAsync($"+{cleanOffset}");

        await _bot.SendTextMessageAsync(
            chatId: chatId,
            text: $"✅ Часовой пояс изменён на <b>UTC+{parsedOffset:hh\\:mm}</b>",
            parseMode: ParseMode.Html,
            cancellationToken: ct);

        return true;
    }

    private async Task<bool> SendHelpAsync(long chatId, CancellationToken ct)
    {
        var help = """
            <b>🔧 Админ-команды</b>

            <b>Просмотр:</b>
            /admin status — текущие настройки
            /admin report — отчёт по логам прямо сейчас

            <b>Настройки:</b>
            /admin set_summary_time HH:mm — время ежедневного саммари
            /admin set_report_time HH:mm — время отчёта в личку
            /admin set_timezone +N — часовой пояс (напр: +6)

            <b>Примеры:</b>
            <code>/admin set_summary_time 21:00</code>
            <code>/admin set_report_time 10:00</code>
            <code>/admin set_timezone +6</code>
            """;

        await _bot.SendTextMessageAsync(
            chatId: chatId,
            text: help,
            parseMode: ParseMode.Html,
            cancellationToken: ct);

        return true;
    }
}
