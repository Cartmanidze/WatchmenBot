using System.Text;
using Telegram.Bot;
using WatchmenBot.Services;

namespace WatchmenBot.Features.Admin.Commands;

/// <summary>
/// /admin prompts - list all prompts with status
/// </summary>
public class PromptsCommand(
    ITelegramBotClient bot,
    PromptSettingsStore promptSettings,
    ILogger<PromptsCommand> logger)
    : AdminCommandBase(bot, logger)
{
    public override async Task<bool> ExecuteAsync(AdminCommandContext context, CancellationToken ct)
    {
        var prompts = await promptSettings.GetAllPromptsAsync();

        var sb = new StringBuilder();
        sb.AppendLine("<b>🎭 Промпты команд</b>\n");

        foreach (var prompt in prompts)
        {
            var status = prompt.IsCustom ? "✏️ кастомный" : "📋 дефолтный";
            var tagInfo = !string.IsNullOrEmpty(prompt.LlmTag) ? $" 🏷️ {prompt.LlmTag}" : "";
            sb.AppendLine($"<b>/{prompt.Command}</b> — {prompt.Description}{tagInfo}");
            sb.AppendLine($"   {status}");
            if (prompt.IsCustom && prompt.UpdatedAt.HasValue)
            {
                sb.AppendLine($"   📅 {prompt.UpdatedAt.Value:dd.MM.yyyy HH:mm}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("💡 <b>Команды:</b>");
        sb.AppendLine("<code>/admin prompt ask</code> — показать промпт");
        sb.AppendLine("<code>/admin prompt_tag ask uncensored</code> — тег LLM");
        sb.AppendLine("<code>/admin prompt_reset ask</code> — сбросить на дефолт");

        await SendMessageAsync(context.ChatId, sb.ToString(), ct);

        return true;
    }
}
