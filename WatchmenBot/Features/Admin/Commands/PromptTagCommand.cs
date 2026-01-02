using Telegram.Bot;
using WatchmenBot.Infrastructure.Settings;
using WatchmenBot.Features.Llm.Services;

namespace WatchmenBot.Features.Admin.Commands;

/// <summary>
/// /admin prompt_tag <command> [tag] - set LLM tag for prompt
/// </summary>
public class PromptTagCommand(
    ITelegramBotClient bot,
    PromptSettingsStore promptSettings,
    LlmRouter llmRouter,
    ILogger<PromptTagCommand> logger)
    : AdminCommandBase(bot, logger)
{
    public override async Task<bool> ExecuteAsync(AdminCommandContext context, CancellationToken ct)
    {
        if (context.Args.Length == 0)
        {
            await SendMessageAsync(context.ChatId,
                "❌ Укажи команду: <code>/admin prompt_tag ask uncensored</code>", ct);
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

        // Get tag (optional, can be null to reset)
        var tag = context.Args.Length > 1 ? context.Args[1] : null;

        // Если тег не указан — сбросить на null
        var tagToSet = string.IsNullOrWhiteSpace(tag) || tag == "null" || tag == "default" ? null : tag;

        await promptSettings.SetLlmTagAsync(command, tagToSet);

        var providers = llmRouter.GetAllProviders();
        var availableTags = providers.Values.SelectMany(p => p.Tags).Distinct().ToList();

        if (tagToSet == null)
        {
            await SendMessageAsync(context.ChatId,
                $"✅ Тег для <b>/{command}</b> сброшен (будет использоваться дефолтный провайдер)", ct);
        }
        else
        {
            var hasProvider = providers.Values.Any(p => p.Tags.Contains(tagToSet, StringComparer.OrdinalIgnoreCase));
            var warning = hasProvider ? "" : $"\n\n⚠️ Провайдер с тегом '{tagToSet}' не найден! Доступные теги: {string.Join(", ", availableTags)}";

            await SendMessageAsync(context.ChatId,
                $"✅ Тег для <b>/{command}</b> установлен: <code>{tagToSet}</code>{warning}", ct);
        }

        return true;
    }
}
