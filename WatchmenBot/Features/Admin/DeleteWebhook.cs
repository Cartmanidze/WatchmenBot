using Telegram.Bot;

namespace WatchmenBot.Features.Admin;

public class DeleteWebhookRequest
{
    // No parameters needed
}

public class DeleteWebhookResponse
{
    public bool IsSuccess { get; init; }
    public string? Message { get; init; }
    public string? ErrorMessage { get; init; }
    
    public static DeleteWebhookResponse Success() => new()
    {
        IsSuccess = true,
        Message = "Webhook deleted successfully"
    };
    
    public static DeleteWebhookResponse Error(string errorMessage) => new()
    {
        IsSuccess = false,
        ErrorMessage = errorMessage
    };
}

public class DeleteWebhookHandler
{
    private readonly ITelegramBotClient _botClient;
    private readonly ILogger<DeleteWebhookHandler> _logger;

    public DeleteWebhookHandler(ITelegramBotClient botClient, ILogger<DeleteWebhookHandler> logger)
    {
        _botClient = botClient;
        _logger = logger;
    }

    public async Task<DeleteWebhookResponse> HandleAsync(DeleteWebhookRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await _botClient.DeleteWebhookAsync(cancellationToken: cancellationToken);
            _logger.LogInformation("Webhook deleted successfully");
            
            return DeleteWebhookResponse.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete webhook");
            return DeleteWebhookResponse.Error($"Failed to delete webhook: {ex.Message}");
        }
    }
}