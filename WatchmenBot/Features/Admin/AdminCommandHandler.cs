using Telegram.Bot;
using Telegram.Bot.Types;
using WatchmenBot.Features.Admin.Commands;
using WatchmenBot.Services;

namespace WatchmenBot.Features.Admin;

/// <summary>
/// Main handler for admin commands using Command Pattern
/// Delegates to IAdminCommand implementations via AdminCommandRegistry
/// </summary>
public class AdminCommandHandler
{
    private readonly ITelegramBotClient _bot;
    private readonly AdminSettingsStore _settings;
    private readonly LogCollector _logCollector;
    private readonly AdminCommandRegistry _commandRegistry;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<AdminCommandHandler> _logger;

    public AdminCommandHandler(
        ITelegramBotClient bot,
        AdminSettingsStore settings,
        LogCollector logCollector,
        AdminCommandRegistry commandRegistry,
        IServiceProvider serviceProvider,
        ILogger<AdminCommandHandler> logger)
    {
        _bot = bot;
        _settings = settings;
        _logCollector = logCollector;
        _commandRegistry = commandRegistry;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task<bool> HandleAsync(Message message, CancellationToken ct)
    {
        var text = message.Text?.Trim() ?? message.Caption?.Trim() ?? "";
        var userId = message.From?.Id ?? 0;
        var username = message.From?.Username;

        // Check admin access
        if (!_settings.IsAdmin(userId, username))
        {
            _logger.LogWarning("[Admin] Unauthorized access attempt from {UserId} (@{Username})", userId, username);
            return false;
        }

        // Parse command
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            // Show help via command registry
            var helpCommand = _commandRegistry.GetCommand("help", _serviceProvider);
            if (helpCommand != null)
            {
                var ctx = new AdminCommandContext
                {
                    ChatId = message.Chat.Id,
                    Message = message,
                    Args = Array.Empty<string>(),
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
            var command = _commandRegistry.GetCommand(subCommand, _serviceProvider);
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
            var helpCommand = _commandRegistry.GetCommand("help", _serviceProvider);
            if (helpCommand != null)
            {
                var ctx = new AdminCommandContext
                {
                    ChatId = message.Chat.Id,
                    Message = message,
                    Args = Array.Empty<string>(),
                    FullText = text
                };
                await helpCommand.ExecuteAsync(ctx, ct);
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Admin] Error handling command: {Command}", text);
            _logCollector.LogError("AdminCommand", $"Error: {text}", ex);

            await _bot.SendMessage(
                chatId: message.Chat.Id,
                text: $"❌ Ошибка: {ex.Message}",
                cancellationToken: ct);
            return true;
        }
    }
}
