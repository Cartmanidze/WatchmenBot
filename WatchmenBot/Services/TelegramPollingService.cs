using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using WatchmenBot.Features.Admin;
using WatchmenBot.Features.Messages;
using WatchmenBot.Features.Search;
using WatchmenBot.Features.Summary;

namespace WatchmenBot.Services;

/// <summary>
/// Polling service for local development (instead of webhooks).
/// Enable by setting Telegram:UsePolling = true in appsettings.
/// </summary>
public class TelegramPollingService : BackgroundService
{
    private readonly ITelegramBotClient _bot;
    private readonly IServiceProvider _serviceProvider;
    private readonly LogCollector _logCollector;
    private readonly ILogger<TelegramPollingService> _logger;
    private readonly IConfiguration _configuration;

    public TelegramPollingService(
        ITelegramBotClient bot,
        IServiceProvider serviceProvider,
        LogCollector logCollector,
        ILogger<TelegramPollingService> logger,
        IConfiguration configuration)
    {
        _bot = bot;
        _serviceProvider = serviceProvider;
        _logCollector = logCollector;
        _logger = logger;
        _configuration = configuration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Always register bot commands menu (works for both polling and webhook modes)
        await RegisterBotCommandsAsync(stoppingToken);

        var usePolling = _configuration.GetValue<bool>("Telegram:UsePolling", false);
        if (!usePolling)
        {
            _logger.LogInformation("[Telegram] Polling DISABLED - using webhooks mode");
            return;
        }

        _logger.LogInformation("[Telegram] Starting polling mode...");

        // Delete webhook if exists (polling and webhook can't work together)
        await _bot.DeleteWebhook(cancellationToken: stoppingToken);

        var me = await _bot.GetMe(stoppingToken);
        _logger.LogInformation("[Telegram] Bot ONLINE: @{Username} (ID: {Id})", me.Username, me.Id);

        int offset = 0;
        var totalMessages = 0L;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var updates = await _bot.GetUpdates(
                    offset: offset,
                    allowedUpdates: new[] { UpdateType.Message },
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
                _logger.LogError(ex, "[Telegram] Error getting updates, retrying in 5s...");
                _logCollector.LogError("TelegramPolling", "Error getting updates", ex);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }

        _logger.LogInformation("[Telegram] Polling STOPPED (processed {Total} messages this session)", totalMessages);
    }

    private async Task ProcessUpdateAsync(Update update, CancellationToken ct)
    {
        if (update.Message is not { } message)
            return;

        var chatName = message.Chat.Title ?? message.Chat.Id.ToString();
        var userName = message.From?.Username ?? message.From?.FirstName ?? "unknown";

        try
        {
            using var scope = _serviceProvider.CreateScope();

            // Handle private messages (admin commands and /q)
            if (message.Chat.Type == ChatType.Private)
            {
                if (IsAdminCommand(message.Text))
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
                _logger.LogDebug("[Telegram] Skipping {Type} message from {User}", message.Chat.Type, userName);
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

            // Check for /search command
            if (IsSearchCommand(message.Text))
            {
                await HandleSearchCommand(scope.ServiceProvider, message, ct);
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

            // Check for /recall command
            if (IsRecallCommand(message.Text))
            {
                await HandleRecallCommand(scope.ServiceProvider, message, ct);
                return;
            }

            // Log message receipt
            var msgType = string.IsNullOrWhiteSpace(message.Text) ? $"[{message.Type}]" : "text";
            var preview = message.Text?.Length > 40 ? message.Text.Substring(0, 40) + "..." : message.Text ?? "";
            _logger.LogInformation("[Telegram] [{Chat}] @{User}: {Preview} ({Type})",
                chatName, userName, preview, msgType);

            // Save message to database
            var saveHandler = scope.ServiceProvider.GetRequiredService<SaveMessageHandler>();
            var request = new SaveMessageRequest { Message = message };
            var response = await saveHandler.HandleAsync(request, ct);

            if (response.IsSuccess)
            {
                _logCollector.IncrementMessages();
            }
            else
            {
                _logger.LogWarning("[Telegram] Failed to save message: {Error}", response.ErrorMessage);
                _logCollector.LogWarning("SaveMessage", response.ErrorMessage ?? "Unknown error");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Telegram] Error processing message {MessageId} in {Chat}", message.MessageId, chatName);
            _logCollector.LogError("TelegramPolling", $"Error processing message in {chatName}", ex);
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

    private static bool IsSearchCommand(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return text.StartsWith("/search", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAskCommand(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return text.StartsWith("/ask", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRecallCommand(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return text.StartsWith("/recall", StringComparison.OrdinalIgnoreCase);
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

    private async Task HandleSummaryCommand(IServiceProvider serviceProvider, Message message, CancellationToken ct)
    {
        var chatName = message.Chat.Title ?? message.Chat.Id.ToString();
        var userName = message.From?.Username ?? message.From?.FirstName ?? "unknown";
        var hours = GenerateSummaryHandler.ParseHoursFromCommand(message.Text);

        _logger.LogInformation("[Telegram] [{Chat}] @{User} requested /summary for {Hours}h",
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
            _logCollector.IncrementSummaries();
            _logger.LogInformation("[Telegram] [{Chat}] Summary sent: {Count} messages analyzed",
                chatName, response.MessageCount);
        }
        else
        {
            _logCollector.LogWarning("Summary", response.ErrorMessage ?? "Unknown error");
            _logger.LogWarning("[Telegram] [{Chat}] Summary failed: {Error}", chatName, response.ErrorMessage);
        }
    }

    private async Task HandleAdminCommand(IServiceProvider serviceProvider, Message message, CancellationToken ct)
    {
        var userName = message.From?.Username ?? message.From?.FirstName ?? "unknown";
        _logger.LogInformation("[Telegram] Admin command from @{User}", userName);

        var adminHandler = serviceProvider.GetRequiredService<AdminCommandHandler>();
        await adminHandler.HandleAsync(message, ct);
    }

    private async Task HandleSearchCommand(IServiceProvider serviceProvider, Message message, CancellationToken ct)
    {
        var chatName = message.Chat.Title ?? message.Chat.Id.ToString();
        var userName = message.From?.Username ?? message.From?.FirstName ?? "unknown";

        _logger.LogInformation("[Telegram] [{Chat}] @{User} requested /search", chatName, userName);

        var searchHandler = serviceProvider.GetRequiredService<SearchHandler>();
        await searchHandler.HandleAsync(message, ct);
    }

    private async Task HandleAskCommand(IServiceProvider serviceProvider, Message message, CancellationToken ct)
    {
        var chatName = message.Chat.Title ?? message.Chat.Id.ToString();
        var userName = message.From?.Username ?? message.From?.FirstName ?? "unknown";

        _logger.LogInformation("[Telegram] [{Chat}] @{User} requested /ask", chatName, userName);

        var askHandler = serviceProvider.GetRequiredService<AskHandler>();
        await askHandler.HandleAsync(message, ct);
    }

    private async Task HandleRecallCommand(IServiceProvider serviceProvider, Message message, CancellationToken ct)
    {
        var chatName = message.Chat.Title ?? message.Chat.Id.ToString();
        var userName = message.From?.Username ?? message.From?.FirstName ?? "unknown";

        _logger.LogInformation("[Telegram] [{Chat}] @{User} requested /recall", chatName, userName);

        var recallHandler = serviceProvider.GetRequiredService<RecallHandler>();
        await recallHandler.HandleAsync(message, ct);
    }

    private async Task HandleQuestionCommand(IServiceProvider serviceProvider, Message message, CancellationToken ct)
    {
        var chatName = message.Chat.Title ?? message.Chat.Id.ToString();
        var userName = message.From?.Username ?? message.From?.FirstName ?? "unknown";

        _logger.LogInformation("[Telegram] [{Chat}] @{User} requested /q", chatName, userName);

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
                new() { Command = "search", Description = "Поиск по истории чата" },
                new() { Command = "ask", Description = "Вопрос по истории чата (RAG)" },
                new() { Command = "smart", Description = "Поиск в интернете (Perplexity)" },
                new() { Command = "summary", Description = "Саммари за N часов" },
                new() { Command = "recall", Description = "Сообщения пользователя за неделю" },
                new() { Command = "truth", Description = "Фактчек последних сообщений" },
            };

            // Commands for private chat (admin + general questions)
            var privateCommands = new BotCommand[]
            {
                new() { Command = "admin", Description = "Показать справку по админ-командам" },
                new() { Command = "smart", Description = "Поиск в интернете (Perplexity)" },
            };

            // Set commands for all group chats
            await _bot.SetMyCommands(
                groupCommands,
                new BotCommandScopeAllGroupChats(),
                cancellationToken: ct);

            // Set commands for private chats
            await _bot.SetMyCommands(
                privateCommands,
                new BotCommandScopeAllPrivateChats(),
                cancellationToken: ct);

            _logger.LogInformation("[Telegram] Bot commands menu registered");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Telegram] Failed to register bot commands");
        }
    }
}
