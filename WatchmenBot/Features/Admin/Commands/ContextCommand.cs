using System.Text;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;
using WatchmenBot.Services;

namespace WatchmenBot.Features.Admin.Commands;

/// <summary>
/// /admin context [chat_id] - show context embeddings stats
/// Shows statistics for all chats or specific chat
/// </summary>
public class ContextCommand : AdminCommandBase
{
    private readonly ContextEmbeddingService _contextEmbeddingService;
    private readonly EmbeddingService _embeddingService;
    private readonly MessageStore _messageStore;

    public ContextCommand(
        ITelegramBotClient bot,
        ContextEmbeddingService contextEmbeddingService,
        EmbeddingService embeddingService,
        MessageStore messageStore,
        ILogger<ContextCommand> logger) : base(bot, logger)
    {
        _contextEmbeddingService = contextEmbeddingService;
        _embeddingService = embeddingService;
        _messageStore = messageStore;
    }

    public override async Task<bool> ExecuteAsync(AdminCommandContext context, CancellationToken ct)
    {
        // No arguments - show all chats
        if (context.Args.Length == 0)
        {
            return await ShowAllChatsAsync(context.ChatId, ct);
        }

        // Specific chat
        if (!long.TryParse(context.Args[0], out var targetChatId))
        {
            await SendMessageAsync(context.ChatId, "❌ Неверный формат Chat ID", ct);
            return true;
        }

        return await ShowChatStatsAsync(context.ChatId, targetChatId, ct);
    }

    private async Task<bool> ShowAllChatsAsync(long chatId, CancellationToken ct)
    {
        var chats = await _messageStore.GetKnownChatsAsync();

        var sb = new StringBuilder();
        sb.AppendLine("<b>📊 Контекстные эмбеддинги (Sliding Windows)</b>\n");

        var totalWindows = 0;
        foreach (var chat in chats.Take(10))
        {
            var stats = await _contextEmbeddingService.GetStatsAsync(chat.ChatId, ct);
            totalWindows += stats.TotalWindows;

            var title = !string.IsNullOrWhiteSpace(chat.Title) ? chat.Title : $"Chat {chat.ChatId}";
            sb.AppendLine($"<b>{EscapeHtml(title)}</b>: {stats.TotalWindows} окон");
        }

        sb.AppendLine();
        sb.AppendLine($"<b>Всего:</b> {totalWindows} окон");
        sb.AppendLine();
        sb.AppendLine("💡 <code>/admin context &lt;chat_id&gt;</code> — детали чата");
        sb.AppendLine("💡 <code>/admin context_reindex &lt;chat_id&gt;</code> — пересоздать");

        await SendMessageAsync(chatId, sb.ToString(), ct);

        return true;
    }

    private async Task<bool> ShowChatStatsAsync(long chatId, long targetChatId, CancellationToken ct)
    {
        var stats = await _contextEmbeddingService.GetStatsAsync(targetChatId, ct);
        var embStats = await _embeddingService.GetStatsAsync(targetChatId, ct);

        var coverage = embStats.TotalEmbeddings > 0
            ? $"{(double)stats.TotalWindows * 10 / embStats.TotalEmbeddings * 100:F1}%"
            : "N/A";

        await SendMessageAsync(chatId, $"""
            <b>📊 Контекстные эмбеддинги</b>

            Чат: <code>{targetChatId}</code>

            📦 <b>Окна:</b> {stats.TotalWindows}
            📏 <b>Размер окна:</b> 10 сообщений
            ↔️ <b>Шаг:</b> 3 (перекрытие 7)
            📈 <b>Покрытие:</b> ~{coverage}

            📅 <b>Первое:</b> {stats.OldestWindow?.ToString("dd.MM.yyyy HH:mm") ?? "—"}
            📅 <b>Последнее:</b> {stats.NewestWindow?.ToString("dd.MM.yyyy HH:mm") ?? "—"}

            💡 Для пересоздания: <code>/admin context_reindex {targetChatId}</code>
            """, ct);

        return true;
    }
}
