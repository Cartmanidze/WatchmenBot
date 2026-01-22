using Telegram.Bot;
using WatchmenBot.Features.Admin.Services;

namespace WatchmenBot.Features.Admin.Commands;

/// <summary>
/// /admin unban &lt;user_id&gt; - unban a user
/// </summary>
public class UnbanCommand(
    ITelegramBotClient bot,
    BannedUserService bannedUserService,
    ILogger<UnbanCommand> logger) : AdminCommandBase(bot, logger)
{
    public override async Task<bool> ExecuteAsync(AdminCommandContext context, CancellationToken ct)
    {
        if (context.Args.Length < 1)
        {
            await SendMessageAsync(context.ChatId, """
                <b>Использование:</b>
                /admin unban &lt;user_id&gt;

                <b>Пример:</b>
                /admin unban 123456789
                """, ct);
            return true;
        }

        if (!long.TryParse(context.Args[0], out var userId))
        {
            await SendMessageAsync(context.ChatId, "❌ Некорректный user_id. Должно быть число.", ct);
            return true;
        }

        var adminUserId = context.Message.From?.Id;
        if (adminUserId is null or 0)
        {
            Logger.LogWarning("[Admin] Unban command received without valid admin user ID");
            await SendMessageAsync(context.ChatId, "❌ Ошибка: не удалось определить администратора.", ct);
            return true;
        }

        try
        {
            var result = await bannedUserService.UnbanUserAsync(userId, adminUserId.Value, ct);

            if (result.WasNotBanned)
            {
                await SendMessageAsync(context.ChatId,
                    $"⚠️ Пользователь <code>{userId}</code> не был забанен.",
                    ct);
                return true;
            }

            await SendMessageAsync(context.ChatId,
                $"✅ Пользователь <code>{userId}</code> разбанен.",
                ct);

            Logger.LogInformation("[Admin] User {UserId} unbanned by admin {AdminId}", userId, adminUserId.Value);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[Admin] Failed to unban user {UserId}", userId);
            await SendMessageAsync(context.ChatId, "❌ Не удалось разбанить пользователя. Попробуйте позже.", ct);
        }

        return true;
    }
}
