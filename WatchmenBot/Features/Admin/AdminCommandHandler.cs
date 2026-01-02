using Telegram.Bot;
using Telegram.Bot.Types;
using WatchmenBot.Features.Admin.Commands;
using WatchmenBot.Services;

namespace WatchmenBot.Features.Admin;

/// <summary>
/// Main handler for admin commands using Command Pattern
/// Delegates to IAdminCommand implementations via AdminCommandRegistry
/// </summary>
public class AdminCommandHandler(
    ITelegramBotClient bot,
    AdminSettingsStore settings,
    LogCollector logCollector,
    AdminCommandRegistry commandRegistry,
    IServiceProvider serviceProvider,
    ILogger<AdminCommandHandler> logger)
{
    public async Task<bool> HandleAsync(Message message, CancellationToken ct)
    {
        var text = message.Text?.Trim() ?? message.Caption?.Trim() ?? "";
        var userId = message.From?.Id ?? 0;
        var username = message.From?.Username;

        // Check admin access
        if (!settings.IsAdmin(userId, username))
        {
            logger.LogWarning("[Admin] Unauthorized access attempt from {UserId} (@{Username})", userId, username);
            return false;
        }

        // Parse command
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            // Show help via command registry
            var helpCommand = commandRegistry.GetCommand("help", serviceProvider);
            if (helpCommand != null)
            {
                var ctx = new AdminCommandContext
                {
                    ChatId = message.Chat.Id,
                    Message = message,
                    Args = [],
                    FullText = text
                };
                await helpCommand.ExecuteAsync(ctx, ct);
            }
            return true;
        }

        var subCommand = parts[1].ToLowerInvariant();

        try
        {
            // Try to get command from registry (Command Pattern)
            var command = commandRegistry.GetCommand(subCommand, serviceProvider);
            if (command != null)
            {
                var context = new AdminCommandContext
                {
                    ChatId = message.Chat.Id,
                    Message = message,
                    Args = parts.Skip(2).ToArray(),
                    FullText = text
                };
                return await command.ExecuteAsync(context, ct);
            }

            // Command not found - show help
            var helpCommand = commandRegistry.GetCommand("help", serviceProvider);
            if (helpCommand != null)
            {
                var ctx = new AdminCommandContext
                {
                    ChatId = message.Chat.Id,
                    Message = message,
                    Args = [],
                    FullText = text
                };
                await helpCommand.ExecuteAsync(ctx, ct);
            }
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Admin] Error handling command: {Command}", text);
            logCollector.LogError("AdminCommand", $"Error: {text}", ex);

            await bot.SendMessage(
                chatId: message.Chat.Id,
                text: $"❌ Ошибка: {ex.Message}",
                cancellationToken: ct);
            return true;
        }
    }
}
