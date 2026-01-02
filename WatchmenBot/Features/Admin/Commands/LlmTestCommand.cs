using System.Text;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;
using WatchmenBot.Features.Llm.Services;

namespace WatchmenBot.Features.Admin.Commands;

/// <summary>
/// /admin llm_test [provider_name] - test LLM provider
/// </summary>
public class LlmTestCommand(
    ITelegramBotClient bot,
    LlmRouter llmRouter,
    ILogger<LlmTestCommand> logger)
    : AdminCommandBase(bot, logger)
{
    public override async Task<bool> ExecuteAsync(AdminCommandContext context, CancellationToken ct)
    {
        var providerName = context.Args.Length > 0 ? context.Args[0] : null;

        var statusMsg = await Bot.SendMessage(
            chatId: context.ChatId,
            text: "⏳ Тестирую LLM...",
            cancellationToken: ct);

        try
        {
            ILlmProvider provider;
            if (string.IsNullOrEmpty(providerName))
            {
                provider = llmRouter.GetDefault();
            }
            else
            {
                provider = llmRouter.GetProvider(providerName)
                    ?? throw new ArgumentException($"Провайдер '{providerName}' не найден");
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();

            var response = await provider.CompleteAsync(new LlmRequest
            {
                SystemPrompt = "Ты тестовый бот. Отвечай кратко.",
                UserPrompt = "Скажи 'Привет, я работаю!' и добавь одну случайную шутку про программистов.",
                Temperature = 0.8
            }, ct);

            sw.Stop();

            var sb = new StringBuilder();
            sb.AppendLine("✅ <b>Тест пройден!</b>\n");
            sb.AppendLine($"📦 <b>Провайдер:</b> {response.Provider}");
            sb.AppendLine($"🤖 <b>Модель:</b> {response.Model}");
            sb.AppendLine($"⏱️ <b>Время:</b> {sw.ElapsedMilliseconds}ms");
            sb.AppendLine($"📊 <b>Токены:</b> {response.PromptTokens} + {response.CompletionTokens} = {response.TotalTokens}");
            sb.AppendLine();
            sb.AppendLine("<b>Ответ:</b>");
            sb.AppendLine($"<i>{EscapeHtml(response.Content)}</i>");

            await Bot.EditMessageText(
                chatId: context.ChatId,
                messageId: statusMsg.MessageId,
                text: sb.ToString(),
                parseMode: ParseMode.Html,
                cancellationToken: ct);
        }
        catch (Exception ex)
        {
            await Bot.EditMessageText(
                chatId: context.ChatId,
                messageId: statusMsg.MessageId,
                text: $"❌ <b>Ошибка теста</b>\n\n{EscapeHtml(ex.Message)}",
                parseMode: ParseMode.Html,
                cancellationToken: ct);
        }

        return true;
    }
}
