using Telegram.Bot;
using Telegram.Bot.Types.Enums;
using WatchmenBot.Features.Search.Services;

namespace WatchmenBot.Features.Admin.Commands;

/// <summary>
/// /admin context_reindex [chat_id] [confirm] - reindex context embeddings
/// Variants:
/// - /admin context_reindex all - show help
/// - /admin context_reindex <chat_id> - show confirmation prompt
/// - /admin context_reindex <chat_id> confirm - execute reindex for chat
/// - /admin context_reindex all confirm - execute reindex for all chats
/// </summary>
public class ContextReindexCommand(
    ITelegramBotClient bot,
    ContextEmbeddingService contextEmbeddingService,
    ILogger<ContextReindexCommand> logger)
    : AdminCommandBase(bot, logger)
{
    public override async Task<bool> ExecuteAsync(AdminCommandContext context, CancellationToken ct)
    {
        if (context.Args.Length == 0)
        {
            await SendMessageAsync(context.ChatId,
                "❌ Укажи chat_id или 'all': <code>/admin context_reindex -1234567</code>", ct);
            return true;
        }

        var chatIdStr = context.Args[0];
        var isConfirm = context.Args.Length > 1 && context.Args[1] == "confirm";

        // Handle "all" case
        if (chatIdStr.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            if (isConfirm)
            {
                return await ExecuteReindexAllAsync(context.ChatId, ct);
            }
            else
            {
                return await ShowAllHelpAsync(context.ChatId, ct);
            }
        }

        // Handle specific chat
        if (!long.TryParse(chatIdStr, out var targetChatId))
        {
            await SendMessageAsync(context.ChatId, "❌ Неверный формат Chat ID", ct);
            return true;
        }

        if (isConfirm)
        {
            return await ExecuteReindexAsync(context.ChatId, targetChatId, ct);
        }
        else
        {
            return await ShowConfirmationAsync(context.ChatId, targetChatId, ct);
        }
    }

    private async Task<bool> ShowAllHelpAsync(long chatId, CancellationToken ct)
    {
        await SendMessageAsync(chatId, """
            ⚠️ <b>Переиндексация ВСЕХ контекстных эмбеддингов</b>

            Это удалит ВСЕ контекстные эмбеддинги из ВСЕХ чатов.
            BackgroundService пересоздаст их автоматически.

            Использование:
            • <code>/admin context_reindex -1234567</code> — конкретный чат
            • <code>/admin context_reindex all confirm</code> — ВСЕ чаты

            ⚠️ Полная переиндексация может занять много времени и стоить денег (API calls).
            """, ct);

        return true;
    }

    private async Task<bool> ShowConfirmationAsync(long chatId, long targetChatId, CancellationToken ct)
    {
        var stats = await contextEmbeddingService.GetStatsAsync(targetChatId, ct);

        await SendMessageAsync(chatId, $"""
            ⚠️ <b>Переиндексация контекстных эмбеддингов</b>

            Чат: <code>{targetChatId}</code>
            Текущих окон: {stats.TotalWindows}

            Это удалит все контекстные эмбеддинги чата и BackgroundService пересоздаст их.

            Для подтверждения: <code>/admin context_reindex {targetChatId} confirm</code>
            """, ct);

        return true;
    }

    private async Task<bool> ExecuteReindexAsync(long chatId, long targetChatId, CancellationToken ct)
    {
        var statusMsg = await Bot.SendMessage(
            chatId: chatId,
            text: $"⏳ Удаляю контекстные эмбеддинги чата {targetChatId}...",
            cancellationToken: ct);

        await contextEmbeddingService.DeleteChatContextEmbeddingsAsync(targetChatId, ct);

        await Bot.EditMessageText(
            chatId: chatId,
            messageId: statusMsg.MessageId,
            text: $"""
                ✅ <b>Контекстные эмбеддинги удалены</b>

                Чат: <code>{targetChatId}</code>

                BackgroundService начнёт переиндексацию автоматически.
                💡 Следить за прогрессом: <code>/admin context {targetChatId}</code>
                """,
            parseMode: ParseMode.Html,
            cancellationToken: ct);

        return true;
    }

    private async Task<bool> ExecuteReindexAllAsync(long chatId, CancellationToken ct)
    {
        var statusMsg = await Bot.SendMessage(
            chatId: chatId,
            text: "⏳ Удаляю ВСЕ контекстные эмбеддинги...",
            cancellationToken: ct);

        await contextEmbeddingService.DeleteAllContextEmbeddingsAsync(ct);

        await Bot.EditMessageText(
            chatId: chatId,
            messageId: statusMsg.MessageId,
            text: """
                ✅ <b>Все контекстные эмбеддинги удалены</b>

                BackgroundService начнёт переиндексацию автоматически.

                💡 Следить за прогрессом можно в логах:
                <code>docker logs watchmenbot-app --tail 50 -f | grep ContextEmb</code>
                """,
            parseMode: ParseMode.Html,
            cancellationToken: ct);

        return true;
    }
}
