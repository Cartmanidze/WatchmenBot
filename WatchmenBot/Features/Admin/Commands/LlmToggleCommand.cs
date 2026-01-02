using Telegram.Bot;
using WatchmenBot.Services.Llm;

namespace WatchmenBot.Features.Admin.Commands;

/// <summary>
/// /admin llm_on/llm_off <name> - enable/disable LLM provider
/// </summary>
public class LlmToggleCommand(
    ITelegramBotClient bot,
    LlmRouter llmRouter,
    bool enable,
    ILogger<LlmToggleCommand> logger)
    : AdminCommandBase(bot, logger)
{
    public override async Task<bool> ExecuteAsync(AdminCommandContext context, CancellationToken ct)
    {
        if (context.Args.Length == 0)
        {
            var cmd = enable ? "llm_on" : "llm_off";
            await SendMessageAsync(context.ChatId,
                $"❌ Укажи имя провайдера: <code>/admin {cmd} openrouter</code>", ct);
            return true;
        }

        var providerName = context.Args[0];
        var success = llmRouter.SetProviderEnabled(providerName, enable);

        if (success)
        {
            var status = enable ? "✅ включён" : "❌ выключен";
            await SendMessageAsync(context.ChatId,
                $"Провайдер <b>{providerName}</b> {status}", ct);
        }
        else
        {
            var providers = llmRouter.GetAllProviders();
            await SendMessageAsync(context.ChatId,
                $"❌ Провайдер <b>{providerName}</b> не найден\n\nДоступные: {string.Join(", ", providers.Keys)}", ct);
        }

        return true;
    }
}
