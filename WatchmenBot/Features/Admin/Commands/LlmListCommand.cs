using System.Text;
using Telegram.Bot;
using WatchmenBot.Services.Llm;

namespace WatchmenBot.Features.Admin.Commands;

/// <summary>
/// /admin llm - list all LLM providers
/// </summary>
public class LlmListCommand : AdminCommandBase
{
    private readonly LlmRouter _llmRouter;

    public LlmListCommand(
        ITelegramBotClient bot,
        LlmRouter llmRouter,
        ILogger<LlmListCommand> logger) : base(bot, logger)
    {
        _llmRouter = llmRouter;
    }

    public override async Task<bool> ExecuteAsync(AdminCommandContext context, CancellationToken ct)
    {
        var providers = _llmRouter.GetAllProviders();
        var defaultName = _llmRouter.DefaultProviderName;

        var sb = new StringBuilder();
        sb.AppendLine("<b>🤖 LLM Провайдеры</b>\n");

        if (providers.Count == 0)
        {
            sb.AppendLine("❌ Нет зарегистрированных провайдеров");
        }
        else
        {
            foreach (var (name, options) in providers.OrderBy(p => p.Value.Priority))
            {
                var status = options.Enabled ? "✅" : "⏸️";
                var isDefault = name == defaultName ? " ⭐ <i>(default)</i>" : "";

                sb.AppendLine($"{status} <b>{name}</b>{isDefault}");
                sb.AppendLine($"   📦 {options.Model}");
                sb.AppendLine($"   🏷️ [{string.Join(", ", options.Tags)}]");
                sb.AppendLine();
            }
        }

        sb.AppendLine("💡 <code>/admin llm_set &lt;name&gt;</code> — сменить дефолтный");

        await SendMessageAsync(context.ChatId, sb.ToString(), ct);
        return true;
    }
}
