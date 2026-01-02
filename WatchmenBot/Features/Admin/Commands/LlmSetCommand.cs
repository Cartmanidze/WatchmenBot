using Telegram.Bot;
using WatchmenBot.Features.Llm.Services;

namespace WatchmenBot.Features.Admin.Commands;

/// <summary>
/// /admin llm_set <name> - set default LLM provider
/// </summary>
public class LlmSetCommand(
    ITelegramBotClient bot,
    LlmRouter llmRouter,
    ILogger<LlmSetCommand> logger)
    : AdminCommandBase(bot, logger)
{
    public override async Task<bool> ExecuteAsync(AdminCommandContext context, CancellationToken ct)
    {
        if (context.Args.Length == 0)
        {
            await SendMessageAsync(context.ChatId,
                "❌ Укажи имя провайдера: <code>/admin llm_set openrouter</code>", ct);
            return true;
        }

        var providerName = context.Args[0];
        var providers = llmRouter.GetAllProviders();

        if (!providers.ContainsKey(providerName))
        {
            await SendMessageAsync(context.ChatId,
                $"❌ Провайдер <b>{providerName}</b> не найден\n\nДоступные: {string.Join(", ", providers.Keys)}", ct);
            return true;
        }

        var oldDefault = llmRouter.DefaultProviderName;
        var success = llmRouter.SetDefaultProvider(providerName);

        if (success)
        {
            var newProvider = providers[providerName];
            await SendMessageAsync(context.ChatId, $"""
                ✅ <b>Дефолтный провайдер изменён</b>

                {oldDefault} → <b>{providerName}</b>
                📦 Модель: {newProvider.Model}
                """, ct);
        }
        else
        {
            await SendMessageAsync(context.ChatId, "❌ Не удалось изменить провайдера", ct);
        }

        return true;
    }
}
