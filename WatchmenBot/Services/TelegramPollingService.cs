using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using WatchmenBot.Features.Messages;
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
    private readonly ILogger<TelegramPollingService> _logger;
    private readonly IConfiguration _configuration;

    public TelegramPollingService(
        ITelegramBotClient bot,
        IServiceProvider serviceProvider,
        ILogger<TelegramPollingService> logger,
        IConfiguration configuration)
    {
        _bot = bot;
        _serviceProvider = serviceProvider;
        _logger = logger;
        _configuration = configuration;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var usePolling = _configuration.GetValue<bool>("Telegram:UsePolling", false);
        if (!usePolling)
        {
            _logger.LogInformation("[Telegram] Polling DISABLED - using webhooks mode");
            return;
        }

        _logger.LogInformation("[Telegram] Starting polling mode...");

        // Delete webhook if exists (polling and webhook can't work together)
        await _bot.DeleteWebhookAsync(cancellationToken: stoppingToken);

        var me = await _bot.GetMeAsync(stoppingToken);
        _logger.LogInformation("[Telegram] Bot ONLINE: @{Username} (ID: {Id})", me.Username, me.Id);

        int offset = 0;
        var totalMessages = 0L;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var updates = await _bot.GetUpdatesAsync(
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

        // Only process group/supergroup messages
        if (message.Chat.Type != ChatType.Group && message.Chat.Type != ChatType.Supergroup)
        {
            _logger.LogDebug("[Telegram] Skipping {Type} message from {User}", message.Chat.Type, userName);
            return;
        }

        try
        {
            using var scope = _serviceProvider.CreateScope();

            // Check for /summary command
            if (IsSummaryCommand(message.Text))
            {
                await HandleSummaryCommand(scope.ServiceProvider, message, ct);
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

            if (!response.IsSuccess)
            {
                _logger.LogWarning("[Telegram] Failed to save message: {Error}", response.ErrorMessage);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Telegram] Error processing message {MessageId} in {Chat}", message.MessageId, chatName);
        }
    }

    private static bool IsSummaryCommand(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        // Handle both /summary and /summary@BotUsername formats
        return text.StartsWith("/summary", StringComparison.OrdinalIgnoreCase);
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
            _logger.LogInformation("[Telegram] [{Chat}] Summary sent: {Count} messages analyzed",
                chatName, response.MessageCount);
        }
        else
        {
            _logger.LogWarning("[Telegram] [{Chat}] Summary failed: {Error}", chatName, response.ErrorMessage);
        }
    }
}
