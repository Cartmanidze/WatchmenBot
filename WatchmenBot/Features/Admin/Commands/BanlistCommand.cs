using System.Text;
using Telegram.Bot;
using WatchmenBot.Features.Admin.Services;

namespace WatchmenBot.Features.Admin.Commands;

/// <summary>
/// /admin banlist - show list of banned users
/// </summary>
public class BanlistCommand(
    ITelegramBotClient bot,
    BannedUserService bannedUserService,
    ILogger<BanlistCommand> logger) : AdminCommandBase(bot, logger)
{
    public override async Task<bool> ExecuteAsync(AdminCommandContext context, CancellationToken ct)
    {
        try
        {
            var bannedUsers = await bannedUserService.GetBannedUsersAsync(ct);

            if (bannedUsers.Count == 0)
            {
                await SendMessageAsync(context.ChatId, "✅ Нет забаненных пользователей.", ct);
                return true;
            }

            var sb = new StringBuilder();
            sb.AppendLine($"<b>🚫 Забаненные пользователи ({bannedUsers.Count}):</b>");
            sb.AppendLine();

            foreach (var ban in bannedUsers)
            {
                var expiry = DurationParser.FormatExpiration(ban.ExpiresAt);
                var reason = string.IsNullOrWhiteSpace(ban.Reason) ? "—" : EscapeHtml(ban.Reason);

                sb.AppendLine($"• <code>{ban.UserId}</code>");
                sb.AppendLine($"  Причина: {reason}");
                sb.AppendLine($"  Истекает: {expiry}");
                sb.AppendLine($"  Забанен: {ban.BannedAt:dd.MM.yyyy HH:mm}");
                sb.AppendLine();
            }

            await SendMessageAsync(context.ChatId, sb.ToString(), ct);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[Admin] Failed to get banlist");
            await SendMessageAsync(context.ChatId, "❌ Не удалось получить список банов. Попробуйте позже.", ct);
        }

        return true;
    }
}
