using Telegram.Bot;
using WatchmenBot.Services;

namespace WatchmenBot.Features.Admin.Commands;

/// <summary>
/// /admin prompt_reset <command> - reset prompt to default
/// </summary>
public class PromptResetCommand(
    ITelegramBotClient bot,
    PromptSettingsStore promptSettings,
    ILogger<PromptResetCommand> logger)
    : AdminCommandBase(bot, logger)
{
    public override async Task<bool> ExecuteAsync(AdminCommandContext context, CancellationToken ct)
    {
        if (context.Args.Length == 0)
        {
            await SendMessageAsync(context.ChatId, "❌ Укажи команду: <code>/admin prompt_reset ask</code>", ct);
            return true;
        }

        var command = context.Args[0];
        var defaults = promptSettings.GetDefaults();

        if (!defaults.ContainsKey(command))
        {
            await SendMessageAsync(context.ChatId,
                $"❌ Неизвестная команда: {command}\n\nДоступные: {string.Join(", ", defaults.Keys)}", ct);
            return true;
        }

        await promptSettings.ResetPromptAsync(command);

        await SendMessageAsync(context.ChatId, $"✅ Промпт для <b>/{command}</b> сброшен на дефолтный", ct);

        return true;
    }
}
