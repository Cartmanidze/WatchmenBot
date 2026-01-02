using Telegram.Bot;
using WatchmenBot.Services;

namespace WatchmenBot.Features.Admin.Commands;

/// <summary>
/// /admin debug [on|off] - toggle or show debug mode status
/// </summary>
public class DebugCommand(
    ITelegramBotClient bot,
    AdminSettingsStore settings,
    ILogger<DebugCommand> logger)
    : AdminCommandBase(bot, logger)
{
    public override async Task<bool> ExecuteAsync(AdminCommandContext context, CancellationToken ct)
    {
        // If no argument, show status
        if (context.Args.Length == 0)
        {
            return await ShowStatusAsync(context.ChatId, ct);
        }

        // Parse toggle mode
        var mode = context.Args[0];
        var enable = mode.ToLowerInvariant() switch
        {
            "on" or "1" or "true" or "enable" => true,
            "off" or "0" or "false" or "disable" => false,
            _ => (bool?)null
        };

        if (enable == null)
        {
            await SendMessageAsync(context.ChatId,
                "❌ Используй: <code>/admin debug on</code> или <code>/admin debug off</code>", ct);
            return true;
        }

        // Toggle debug mode
        await settings.SetDebugModeAsync(enable.Value);

        var status = enable.Value ? "✅ включён" : "❌ выключен";
        var info = enable.Value
            ? "\n\n📊 Теперь при каждом /ask, /q, /summary, /truth ты будешь получать отчёт:\n• Результаты поиска (score, текст)\n• Контекст для LLM\n• Промпты (system + user)\n• Ответ LLM (токены, время)"
            : "";

        await SendMessageAsync(context.ChatId, $"🔍 Debug mode {status}{info}", ct);
        return true;
    }

    private async Task<bool> ShowStatusAsync(long chatId, CancellationToken ct)
    {
        var enabled = await settings.IsDebugModeEnabledAsync();

        await SendMessageAsync(chatId, $"""
            🔍 <b>Debug Mode</b>

            Статус: {(enabled ? "✅ ON" : "❌ OFF")}

            <b>Команды:</b>
            <code>/admin debug on</code> — включить
            <code>/admin debug off</code> — выключить

            <b>Что показывает:</b>
            • Query (запрос пользователя)
            • TopK результаты поиска (score, message_ids, текст)
            • Контекст для LLM (токены, сообщения)
            • Промпты (system + user)
            • Ответ LLM (провайдер, токены, время)
            """, ct);

        return true;
    }
}
