using System.Net;
using Telegram.Bot.Types;
using WatchmenBot.Extensions;
using WatchmenBot.Features.Messages;

namespace WatchmenBot.Features.Webhook;

public class ProcessTelegramUpdateRequest
{
    public required Update Update { get; init; }
    public required IPAddress? RemoteIpAddress { get; init; }
    public required IHeaderDictionary Headers { get; init; }
}

public class ProcessTelegramUpdateResponse
{
    public bool IsSuccess { get; init; }
    public string? ErrorMessage { get; init; }
    public int StatusCode { get; init; }
    
    public static ProcessTelegramUpdateResponse Success() => new() { IsSuccess = true, StatusCode = 200 };
    public static ProcessTelegramUpdateResponse Unauthorized(string message) => new() { IsSuccess = false, ErrorMessage = message, StatusCode = 403 };
    public static ProcessTelegramUpdateResponse BadRequest(string message) => new() { IsSuccess = false, ErrorMessage = message, StatusCode = 400 };
    public static ProcessTelegramUpdateResponse InternalError(string message) => new() { IsSuccess = false, ErrorMessage = message, StatusCode = 500 };
}

public class ProcessTelegramUpdateHandler
{
    private readonly IConfiguration _configuration;
    private readonly SaveMessageHandler _saveMessageHandler;
    private readonly ILogger<ProcessTelegramUpdateHandler> _logger;

    public ProcessTelegramUpdateHandler(
        IConfiguration configuration,
        SaveMessageHandler saveMessageHandler,
        ILogger<ProcessTelegramUpdateHandler> logger)
    {
        _configuration = configuration;
        _saveMessageHandler = saveMessageHandler;
        _logger = logger;
    }

    public async Task<ProcessTelegramUpdateResponse> HandleAsync(ProcessTelegramUpdateRequest request, CancellationToken cancellationToken)
    {
        // Security validation using extensions
        var secretValidation = request.Headers.ValidateSecretToken(_configuration["Telegram:WebhookSecret"]);
        if (!secretValidation.IsValid)
        {
            _logger.LogWarning("Unauthorized webhook request from {RemoteIpAddress}: {Reason}", 
                request.RemoteIpAddress, secretValidation.Reason);
            return ProcessTelegramUpdateResponse.Unauthorized(secretValidation.Reason);
        }

        var ipValidation = request.RemoteIpAddress.ValidateIpRange(_configuration.GetValue<bool>("Telegram:ValidateIpRange"));
        if (!ipValidation.IsValid)
        {
            _logger.LogWarning("Webhook request from invalid IP: {RemoteIpAddress}", request.RemoteIpAddress);
            return ProcessTelegramUpdateResponse.Unauthorized("Invalid IP address");
        }

        // Validate update using extensions
        var update = request.Update;
        if (!update.HasMessage())
        {
            return ProcessTelegramUpdateResponse.Success(); // Not a message update, ignore
        }

        if (!update.IsGroupMessage())
        {
            return ProcessTelegramUpdateResponse.Success(); // Not a group message, ignore
        }

        // Delegate to SaveMessage feature
        try
        {
            var saveRequest = new SaveMessageRequest { Message = update.Message! };
            var saveResponse = await _saveMessageHandler.HandleAsync(saveRequest, cancellationToken);
            
            if (!saveResponse.IsSuccess)
            {
                _logger.LogError("Failed to save message: {Error}", saveResponse.ErrorMessage);
                return ProcessTelegramUpdateResponse.InternalError("Failed to save message");
            }

            return ProcessTelegramUpdateResponse.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error processing webhook update");
            return ProcessTelegramUpdateResponse.InternalError("Internal server error");
        }
    }
}