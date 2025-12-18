using Telegram.Bot;

namespace WatchmenBot.Features.Admin;

public class GetWebhookInfoRequest
{
    // No parameters needed
}

public class GetWebhookInfoResponse
{
    public bool IsSuccess { get; init; }
    public string? Url { get; init; }
    public bool HasCustomCertificate { get; init; }
    public int PendingUpdateCount { get; init; }
    public int MaxConnections { get; init; }
    public IEnumerable<string> AllowedUpdates { get; init; } = Array.Empty<string>();
    public DateTime? LastErrorDate { get; init; }
    public string? LastErrorMessage { get; init; }
    public string? ErrorMessage { get; init; }
    
    public static GetWebhookInfoResponse Success(
        string? url, 
        bool hasCustomCertificate, 
        int pendingUpdateCount, 
        int? maxConnections,
        IEnumerable<string> allowedUpdates,
        DateTime? lastErrorDate,
        string? lastErrorMessage) => new()
    {
        IsSuccess = true,
        Url = url,
        HasCustomCertificate = hasCustomCertificate,
        PendingUpdateCount = pendingUpdateCount,
        MaxConnections = maxConnections ?? default,
        AllowedUpdates = allowedUpdates,
        LastErrorDate = lastErrorDate,
        LastErrorMessage = lastErrorMessage
    };
    
    public static GetWebhookInfoResponse Error(string errorMessage) => new()
    {
        IsSuccess = false,
        ErrorMessage = errorMessage
    };
}

public class GetWebhookInfoHandler
{
    private readonly ITelegramBotClient _botClient;
    private readonly ILogger<GetWebhookInfoHandler> _logger;

    public GetWebhookInfoHandler(ITelegramBotClient botClient, ILogger<GetWebhookInfoHandler> logger)
    {
        _botClient = botClient;
        _logger = logger;
    }

    public async Task<GetWebhookInfoResponse> HandleAsync(GetWebhookInfoRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var info = await _botClient.GetWebhookInfo(cancellationToken);
            
            _logger.LogInformation("Retrieved webhook info: URL={Url}, PendingUpdates={PendingUpdates}", 
                info.Url, info.PendingUpdateCount);
            
            return GetWebhookInfoResponse.Success(
                info.Url,
                info.HasCustomCertificate,
                info.PendingUpdateCount,
                info.MaxConnections,
                info.AllowedUpdates?.Select(u => u.ToString()) ?? Array.Empty<string>(),
                info.LastErrorDate,
                info.LastErrorMessage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get webhook info");
            return GetWebhookInfoResponse.Error($"Failed to get webhook info: {ex.Message}");
        }
    }
}