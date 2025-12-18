using Telegram.Bot;
using Telegram.Bot.Types.Enums;

namespace WatchmenBot.Features.Admin;

public class SetWebhookRequest
{
    // No additional parameters needed - uses configuration
}

public class SetWebhookResponse
{
    public bool IsSuccess { get; init; }
    public string? Url { get; init; }
    public bool HasSecret { get; init; }
    public string? Message { get; init; }
    public string? ErrorMessage { get; init; }
    
    public static SetWebhookResponse Success(string url, bool hasSecret) => new()
    {
        IsSuccess = true,
        Url = url,
        HasSecret = hasSecret,
        Message = "Webhook set successfully"
    };
    
    public static SetWebhookResponse Error(string errorMessage) => new()
    {
        IsSuccess = false,
        ErrorMessage = errorMessage
    };
}

public class SetWebhookHandler
{
    private readonly ITelegramBotClient _botClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SetWebhookHandler> _logger;

    public SetWebhookHandler(
        ITelegramBotClient botClient,
        IConfiguration configuration,
        ILogger<SetWebhookHandler> logger)
    {
        _botClient = botClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<SetWebhookResponse> HandleAsync(SetWebhookRequest request, CancellationToken cancellationToken)
    {
        var webhookUrl = _configuration["Telegram:WebhookUrl"];
        var secretToken = _configuration["Telegram:WebhookSecret"];

        if (string.IsNullOrWhiteSpace(webhookUrl))
        {
            return SetWebhookResponse.Error("Telegram:WebhookUrl is required in configuration");
        }

        if (!webhookUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return SetWebhookResponse.Error("Webhook URL must use HTTPS");
        }

        try
        {
            await _botClient.SetWebhook(
                url: webhookUrl,
                allowedUpdates: new[] { UpdateType.Message },
                secretToken: string.IsNullOrWhiteSpace(secretToken) ? null : secretToken,
                maxConnections: 40,
                dropPendingUpdates: true,
                cancellationToken: cancellationToken);

            _logger.LogInformation("Webhook set to {WebhookUrl}", webhookUrl);
            
            return SetWebhookResponse.Success(webhookUrl, !string.IsNullOrWhiteSpace(secretToken));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set webhook");
            return SetWebhookResponse.Error($"Failed to set webhook: {ex.Message}");
        }
    }
}