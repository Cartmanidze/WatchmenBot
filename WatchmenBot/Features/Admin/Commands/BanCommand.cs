using Telegram.Bot;
using WatchmenBot.Features.Admin.Services;

namespace WatchmenBot.Features.Admin.Commands;

/// <summary>
/// /admin ban &lt;user_id&gt; [reason] [duration] - ban a user globally
/// Examples:
///   /admin ban 123456789
///   /admin ban 123456789 spam
///   /admin ban 123456789 "спам в чате" 7d
///   /admin ban 123456789 spam 24h
/// </summary>
public class BanCommand(
    ITelegramBotClient bot,
    BannedUserService bannedUserService,
    ILogger<BanCommand> logger) : AdminCommandBase(bot, logger)
{
    public override async Task<bool> ExecuteAsync(AdminCommandContext context, CancellationToken ct)
    {
        // Parse arguments: <user_id> [reason] [duration]
        if (context.Args.Length < 1)
        {
            await SendMessageAsync(context.ChatId, """
                <b>Использование:</b>
                /admin ban &lt;user_id&gt; [reason] [duration]

                <b>Примеры:</b>
                /admin ban 123456789
                /admin ban 123456789 spam
                /admin ban 123456789 spam 7d
                /admin ban 123456789 "спам в чате" 24h

                <b>Duration:</b>
                30m — 30 минут
                24h — 24 часа
                7d — 7 дней
                1w — 1 неделя
                (без duration — перманентный бан)
                """, ct);
            return true;
        }

        // Parse user_id
        if (!long.TryParse(context.Args[0], out var userId))
        {
            await SendMessageAsync(context.ChatId, "❌ Некорректный user_id. Должно быть число.", ct);
            return true;
        }

        // Parse reason and duration from remaining args
        string? reason = null;
        TimeSpan? duration = null;

        if (context.Args.Length >= 2)
        {
            // Check if last arg is duration
            var lastArg = context.Args[^1];
            var parsedDuration = DurationParser.Parse(lastArg);

            if (parsedDuration.HasValue)
            {
                duration = parsedDuration;
                // Reason is everything between user_id and duration
                if (context.Args.Length > 2)
                {
                    reason = string.Join(" ", context.Args[1..^1]);
                }
            }
            else
            {
                // No duration, all remaining args are reason
                reason = string.Join(" ", context.Args[1..]);
            }

            // Clean up reason (remove quotes)
            reason = reason?.Trim('"', '\'', ' ');
            if (string.IsNullOrWhiteSpace(reason))
                reason = null;
        }

        var adminUserId = context.Message.From?.Id;
        if (adminUserId is null or 0)
        {
            Logger.LogWarning("[Admin] Ban command received without valid admin user ID");
            await SendMessageAsync(context.ChatId, "❌ Ошибка: не удалось определить администратора.", ct);
            return true;
        }

        try
        {
            var result = await bannedUserService.BanUserAsync(userId, adminUserId.Value, reason, duration, ct);

            if (result.WasAlreadyBanned)
            {
                var existingExpiry = DurationParser.FormatExpiration(result.Ban?.ExpiresAt);
                await SendMessageAsync(context.ChatId,
                    $"⚠️ Пользователь <code>{userId}</code> уже забанен.\n" +
                    $"Истекает: {existingExpiry}\n" +
                    $"Причина: {EscapeHtml(result.Ban?.Reason ?? "не указана")}",
                    ct);
                return true;
            }

            var expiry = DurationParser.FormatExpiration(result.Ban?.ExpiresAt);
            await SendMessageAsync(context.ChatId,
                $"✅ Пользователь <code>{userId}</code> забанен.\n" +
                $"Истекает: {expiry}\n" +
                $"Причина: {EscapeHtml(reason ?? "не указана")}",
                ct);

            Logger.LogInformation("[Admin] User {UserId} banned by admin {AdminId}. Duration: {Duration}",
                userId, adminUserId.Value, duration?.ToString() ?? "permanent");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[Admin] Failed to ban user {UserId}", userId);
            await SendMessageAsync(context.ChatId, "❌ Не удалось забанить пользователя. Попробуйте позже.", ct);
        }

        return true;
    }
}
