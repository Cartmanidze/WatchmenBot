using Telegram.Bot;
using Telegram.Bot.Types.Enums;
using WatchmenBot.Features.Search.Services;
using WatchmenBot.Features.Messages.Services;

namespace WatchmenBot.Features.Admin.Commands;

/// <summary>
/// /admin reindex [chat_id] [confirm] - reindex message embeddings
/// Variants:
/// - /admin reindex - show help
/// - /admin reindex <chat_id> - show confirmation prompt
/// - /admin reindex <chat_id> confirm - execute reindex for chat
/// - /admin reindex all confirm - execute reindex for all chats
/// </summary>
public class ReindexCommand(
    ITelegramBotClient bot,
    EmbeddingService embeddingService,
    MessageStore messageStore,
    ILogger<ReindexCommand> logger)
    : AdminCommandBase(bot, logger)
{
    public override async Task<bool> ExecuteAsync(AdminCommandContext context, CancellationToken ct)
    {
        // No arguments - show help
        if (context.Args.Length == 0)
        {
            return await ShowHelpAsync(context.ChatId, ct);
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
                return await ShowHelpAsync(context.ChatId, ct);
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

    private async Task<bool> ShowHelpAsync(long chatId, CancellationToken ct)
    {
        var (total, indexed, pending) = await messageStore.GetEmbeddingStatsAsync();

        await SendMessageAsync(chatId, $"""
            ⚠️ <b>Переиндексация ВСЕХ эмбеддингов</b>

            Всего эмбеддингов: {indexed}
            Сообщений для индексации: {total}

            Использование:
            • <code>/admin reindex -1234567</code> — конкретный чат
            • <code>/admin reindex all confirm</code> — ВСЕ чаты

            ⚠️ Полная переиндексация может занять много времени и стоить денег (API calls).
            """, ct);

        return true;
    }

    private async Task<bool> ShowConfirmationAsync(long chatId, long targetChatId, CancellationToken ct)
    {
        var stats = await embeddingService.GetStatsAsync(targetChatId, ct);

        await SendMessageAsync(chatId, $"""
            ⚠️ <b>Переиндексация эмбеддингов</b>

            Чат: <code>{targetChatId}</code>
            Текущих эмбеддингов: {stats.TotalEmbeddings}

            Это удалит все эмбеддинги чата и BackgroundService пересоздаст их в новом формате.

            Для подтверждения: <code>/admin reindex {targetChatId} confirm</code>
            """, ct);

        return true;
    }

    private async Task<bool> ExecuteReindexAsync(long chatId, long targetChatId, CancellationToken ct)
    {
        var statusMessage = await Bot.SendMessage(
            chatId: chatId,
            text: $"⏳ Удаляю эмбеддинги чата {targetChatId}...",
            cancellationToken: ct);

        await embeddingService.DeleteChatEmbeddingsAsync(targetChatId, ct);

        await Bot.EditMessageText(
            chatId: chatId,
            messageId: statusMessage.MessageId,
            text: $"""
                ✅ <b>Эмбеддинги чата удалены</b>

                Чат: <code>{targetChatId}</code>

                BackgroundService начнёт переиндексацию автоматически.
                💡 Следить за прогрессом можно в логах.
                """,
            parseMode: ParseMode.Html,
            cancellationToken: ct);

        return true;
    }

    private async Task<bool> ExecuteReindexAllAsync(long chatId, CancellationToken ct)
    {
        var statusMsg = await Bot.SendMessage(
            chatId: chatId,
            text: "⏳ Удаляю ВСЕ эмбеддинги...",
            cancellationToken: ct);

        await embeddingService.DeleteAllEmbeddingsAsync(ct);

        var (total, _, _) = await messageStore.GetEmbeddingStatsAsync();

        await Bot.EditMessageText(
            chatId: chatId,
            messageId: statusMsg.MessageId,
            text: $"""
                ✅ <b>Все эмбеддинги удалены</b>

                BackgroundService начнёт переиндексацию автоматически.
                Сообщений для индексации: {total}

                💡 Следить за прогрессом можно в логах.
                """,
            parseMode: ParseMode.Html,
            cancellationToken: ct);

        return true;
    }
}
