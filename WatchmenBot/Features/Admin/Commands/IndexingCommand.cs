using System.Text;
using Telegram.Bot;
using WatchmenBot.Services.Indexing;

namespace WatchmenBot.Features.Admin.Commands;

/// <summary>
/// /admin indexing - show indexing pipeline status
/// </summary>
public class IndexingCommand(
    ITelegramBotClient bot,
    EmbeddingOrchestrator embeddingOrchestrator,
    ILogger<IndexingCommand> logger)
    : AdminCommandBase(bot, logger)
{
    public override async Task<bool> ExecuteAsync(AdminCommandContext context, CancellationToken ct)
    {
        try
        {
            // Get stats from all handlers
            var allStats = await embeddingOrchestrator.GetAllStatsAsync(ct);

            var sb = new StringBuilder();
            sb.AppendLine("<b>📊 Indexing Pipeline Status</b>\n");

            foreach (var (handlerName, stats) in allStats.OrderBy(x => x.Key))
            {
                var icon = handlerName switch
                {
                    "message" => "💬",
                    "context" => "🪟",
                    _ => "📦"
                };

                var progress = stats.Total > 0
                    ? (double)stats.Indexed / stats.Total * 100
                    : 0;

                var progressBar = GenerateProgressBar(progress, 10);

                sb.AppendLine($"{icon} <b>{handlerName.ToUpperInvariant()} EMBEDDINGS</b>");
                sb.AppendLine($"   Total: {stats.Total:N0}");
                sb.AppendLine($"   Indexed: {stats.Indexed:N0}");
                sb.AppendLine($"   Pending: {stats.Pending:N0}");
                sb.AppendLine($"   Progress: {progressBar} {progress:F1}%");
                sb.AppendLine();
            }

            // Summary
            var totalMessages = allStats.Values.Sum(s => s.Total);
            var totalIndexed = allStats.Values.Sum(s => s.Indexed);
            var totalPending = allStats.Values.Sum(s => s.Pending);

            sb.AppendLine("<b>📈 TOTAL</b>");
            sb.AppendLine($"   Items: {totalMessages:N0}");
            sb.AppendLine($"   Indexed: {totalIndexed:N0}");
            sb.AppendLine($"   Pending: {totalPending:N0}");

            if (totalPending > 0)
            {
                sb.AppendLine("\n⏳ Background indexing is running...");
            }
            else
            {
                sb.AppendLine("\n✅ All embeddings are up to date!");
            }

            sb.AppendLine("\n💡 Use <code>/admin reindex &lt;chat_id&gt;</code> to rebuild embeddings");

            await SendMessageAsync(context.ChatId, sb.ToString(), ct);
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[Admin] Error getting indexing stats");
            await SendMessageAsync(context.ChatId, $"❌ Error: {ex.Message}", ct);
            return true;
        }
    }
}
