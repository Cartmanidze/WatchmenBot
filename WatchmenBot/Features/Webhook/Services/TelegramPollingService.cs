using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using WatchmenBot.Features.Admin;
using WatchmenBot.Features.Admin.Services;
using WatchmenBot.Features.Messages;
using WatchmenBot.Features.Onboarding;
using WatchmenBot.Features.Search;
using WatchmenBot.Features.Summary;

namespace WatchmenBot.Features.Webhook.Services;

/// <summary>
/// Polling service for local development (instead of webhooks).
/// Enable by setting Telegram:UsePolling = true in appsettings.
/// </summary>
public class TelegramPollingService(
    ITelegramBotClient bot,
    IServiceProvider serviceProvider,
    LogCollector logCollector,
    ILogger<TelegramPollingService> logger,
    IConfiguration configuration)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Always register bot commands menu (works for both polling and webhook modes)
        await RegisterBotCommandsAsync(stoppingToken);

        var usePolling = configuration.GetValue("Telegram:UsePolling", false);
        if (!usePolling)
        {
            logger.LogInformation("[Telegram] Polling DISABLED - using webhooks mode");
            return;
        }

        logger.LogInformation("[Telegram] Starting polling mode...");

        // Delete webhook if exists (polling and webhook can't work together)
        await bot.DeleteWebhook(cancellationToken: stoppingToken);

        var me = await bot.GetMe(stoppingToken);
        logger.LogInformation("[Telegram] Bot ONLINE: @{Username} (ID: {Id})", me.Username, me.Id);

        int offset = 0;
        var totalMessages = 0L;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var updates = await bot.GetUpdates(
                    offset: offset,
                    allowedUpdates: [UpdateType.Message],
                    cancellationToken: stoppingToken);

                foreach (var update in updates)
                {
                    offset = update.Id + 1;
                    totalMessages++;
                    await ProcessUpdateAsync(update, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[Telegram] Error getting updates, retrying in 5s...");
                logCollector.LogError("TelegramPolling", "Error getting updates", ex);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }

        logger.LogInformation("[Telegram] Polling STOPPED (processed {Total} messages this session)", totalMessages);
    }

    private async Task ProcessUpdateAsync(Update update, CancellationToken ct)
    {
        if (update.Message is not { } message)
            return;

        var chatName = message.Chat.Title ?? message.Chat.Id.ToString();
        var userName = message.From?.Username ?? message.From?.FirstName ?? "unknown";

        try
        {
            using var scope = serviceProvider.CreateScope();

            // Handle private messages (admin commands, /q, and /start)
            if (message.Chat.Type == ChatType.Private)
            {
                if (IsStartCommand(message.Text))
                {
                    await HandleStartCommand(scope.ServiceProvider, message, ct);
                }
                else if (IsAdminCommand(message.Text))
                {
                    await HandleAdminCommand(scope.ServiceProvider, message, ct);
                }
                else if (IsQuestionCommand(message.Text))
                {
                    // /q works in private chat too (general questions without chat context)
                    await HandleQuestionCommand(scope.ServiceProvider, message, ct);
                }
                return;
            }

            // Only process group/supergroup messages
            if (message.Chat.Type != ChatType.Group && message.Chat.Type != ChatType.Supergroup)
            {
                logger.LogDebug("[Telegram] Skipping {Type} message from {User}", message.Chat.Type, userName);
                return;
            }

            // Check for /start command in groups (short response that auto-deletes)
            if (IsStartCommand(message.Text))
            {
                await HandleStartCommand(scope.ServiceProvider, message, ct);
                return;
            }

            // Check for /admin command in groups (redirect to PM)
            if (IsAdminCommand(message.Text))
            {
                await HandleAdminCommand(scope.ServiceProvider, message, ct);
                return;
            }

            // Check for /summary command
            if (IsSummaryCommand(message.Text))
            {
                await HandleSummaryCommand(scope.ServiceProvider, message, ct);
                return;
            }

            // Check for /ask command
            if (IsAskCommand(message.Text))
            {
                await HandleAskCommand(scope.ServiceProvider, message, ct);
                return;
            }

            // Check for /q command (serious question)
            if (IsQuestionCommand(message.Text))
            {
                await HandleQuestionCommand(scope.ServiceProvider, message, ct);
                return;
            }

            // Log message receipt
            var msgType = string.IsNullOrWhiteSpace(message.Text) ? $"[{message.Type}]" : "text";
            var preview = message.Text?.Length > 40 ? message.Text.Substring(0, 40) + "..." : message.Text ?? "";
            logger.LogInformation("[Telegram] [{Chat}] @{User}: {Preview} ({Type})",
                chatName, userName, preview, msgType);

            // Save message to database
            var saveHandler = scope.ServiceProvider.GetRequiredService<SaveMessageHandler>();
            var request = new SaveMessageRequest { Message = message };
            var response = await saveHandler.HandleAsync(request, ct);

            if (response.IsSuccess)
            {
                logCollector.IncrementMessages();
            }
            else
            {
                logger.LogWarning("[Telegram] Failed to save message: {Error}", response.ErrorMessage);
                logCollector.LogWarning("SaveMessage", response.ErrorMessage ?? "Unknown error");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Telegram] Error processing message {MessageId} in {Chat}", message.MessageId, chatName);
            logCollector.LogError("TelegramPolling", $"Error processing message in {chatName}", ex);
        }
    }

    private static bool IsSummaryCommand(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        // Handle both /summary and /summary@BotUsername formats
        return text.StartsWith("/summary", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAdminCommand(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return text.StartsWith("/admin", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAskCommand(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return text.StartsWith("/ask", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsQuestionCommand(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        // Match /q but not /qsomething (only /q or /q followed by space/@ for bot mentions)
        return text.StartsWith("/q ", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("/q@", StringComparison.OrdinalIgnoreCase)
            || text.Equals("/q", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsStartCommand(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return text.StartsWith("/start", StringComparison.OrdinalIgnoreCase);
    }

    private async Task HandleStartCommand(IServiceProvider serviceProvider, Message message, CancellationToken ct)
    {
        var userName = message.From?.Username ?? message.From?.FirstName ?? "unknown";
        logger.LogInformation("[Telegram] /start from @{User}", userName);

        var startHandler = serviceProvider.GetRequiredService<StartCommandHandler>();
        await startHandler.HandleAsync(message, ct);
    }

    private async Task HandleSummaryCommand(IServiceProvider serviceProvider, Message message, CancellationToken ct)
    {
        var chatName = message.Chat.Title ?? message.Chat.Id.ToString();
        var userName = message.From?.Username ?? message.From?.FirstName ?? "unknown";
        var hours = GenerateSummaryHandler.ParseHoursFromCommand(message.Text);

        logger.LogInformation("[Telegram] [{Chat}] @{User} requested /summary for {Hours}h",
            chatName, userName, hours);

        var summaryHandler = serviceProvider.GetRequiredService<GenerateSummaryHandler>();

        var request = new GenerateSummaryRequest
        {
            Message = message,
            Hours = hours
        };

        var response = await summaryHandler.HandleAsync(request, ct);

        if (response.IsSuccess)
        {
            logCollector.IncrementSummaries();
            logger.LogInformation("[Telegram] [{Chat}] Summary sent: {Count} messages analyzed",
                chatName, response.MessageCount);
        }
        else
        {
            logCollector.LogWarning("Summary", response.ErrorMessage ?? "Unknown error");
            logger.LogWarning("[Telegram] [{Chat}] Summary failed: {Error}", chatName, response.ErrorMessage);
        }
    }

    private async Task HandleAdminCommand(IServiceProvider serviceProvider, Message message, CancellationToken ct)
    {
        var userName = message.From?.Username ?? message.From?.FirstName ?? "unknown";
        logger.LogInformation("[Telegram] Admin command from @{User}", userName);

        var adminHandler = serviceProvider.GetRequiredService<AdminCommandHandler>();
        await adminHandler.HandleAsync(message, ct);
    }

    private async Task HandleAskCommand(IServiceProvider serviceProvider, Message message, CancellationToken ct)
    {
        var chatName = message.Chat.Title ?? message.Chat.Id.ToString();
        var userName = message.From?.Username ?? message.From?.FirstName ?? "unknown";

        logger.LogInformation("[Telegram] [{Chat}] @{User} requested /ask", chatName, userName);

        var askHandler = serviceProvider.GetRequiredService<AskHandler>();
        await askHandler.HandleAsync(message, ct);
    }

    private async Task HandleQuestionCommand(IServiceProvider serviceProvider, Message message, CancellationToken ct)
    {
        var chatName = message.Chat.Title ?? message.Chat.Id.ToString();
        var userName = message.From?.Username ?? message.From?.FirstName ?? "unknown";

        logger.LogInformation("[Telegram] [{Chat}] @{User} requested /q", chatName, userName);

        var askHandler = serviceProvider.GetRequiredService<AskHandler>();
        await askHandler.HandleQuestionAsync(message, ct);
    }

    private async Task RegisterBotCommandsAsync(CancellationToken ct)
    {
        try
        {
            // Commands for group chats
            var groupCommands = new BotCommand[]
            {
                new() { Command = "ask", Description = "Вопрос по истории чата (RAG)" },
                new() { Command = "smart", Description = "Поиск в интернете (Perplexity)" },
                new() { Command = "summary", Description = "Саммари за N часов" },
                new() { Command = "truth", Description = "Фактчек последних сообщений" },
            };

            // Commands for private chat
            var privateCommands = new BotCommand[]
            {
                new() { Command = "start", Description = "Начать работу с ботом" },
                new() { Command = "admin", Description = "Показать справку по админ-командам" },
            };

            // Set commands for all group chats
            await bot.SetMyCommands(
                groupCommands,
                new BotCommandScopeAllGroupChats(),
                cancellationToken: ct);

            // Set commands for private chats
            await bot.SetMyCommands(
                privateCommands,
                new BotCommandScopeAllPrivateChats(),
                cancellationToken: ct);

            // Enable menu button (shows commands) for all chats
            await bot.SetChatMenuButton(
                menuButton: new MenuButtonCommands(),
                cancellationToken: ct);

            logger.LogInformation("[Telegram] Bot commands menu registered");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Telegram] Failed to register bot commands");
        }
    }
}
