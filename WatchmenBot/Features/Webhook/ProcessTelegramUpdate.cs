using System.Net;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using WatchmenBot.Extensions;
using WatchmenBot.Features.Admin;
using WatchmenBot.Features.Messages;
using WatchmenBot.Features.Search;
using WatchmenBot.Features.Summary;
using WatchmenBot.Services;

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
    private readonly IServiceProvider _serviceProvider;
    private readonly LogCollector _logCollector;
    private readonly ILogger<ProcessTelegramUpdateHandler> _logger;

    public ProcessTelegramUpdateHandler(
        IConfiguration configuration,
        IServiceProvider serviceProvider,
        LogCollector logCollector,
        ILogger<ProcessTelegramUpdateHandler> logger)
    {
        _configuration = configuration;
        _serviceProvider = serviceProvider;
        _logCollector = logCollector;
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

        var message = update.Message!;
        var chatName = message.Chat.Title ?? message.Chat.Id.ToString();
        var userName = message.From?.Username ?? message.From?.FirstName ?? "unknown";

        try
        {
            using var scope = _serviceProvider.CreateScope();

            // Handle private messages (for admin commands)
            if (message.Chat.Type == ChatType.Private)
            {
                if (IsCommand(message.Text, "/admin"))
                {
                    var adminHandler = scope.ServiceProvider.GetRequiredService<AdminCommandHandler>();
                    await adminHandler.HandleAsync(message, cancellationToken);
                }
                return ProcessTelegramUpdateResponse.Success();
            }

            // Only process group/supergroup messages
            if (message.Chat.Type != ChatType.Group && message.Chat.Type != ChatType.Supergroup)
            {
                return ProcessTelegramUpdateResponse.Success();
            }

            // Handle commands
            if (IsCommand(message.Text, "/admin"))
            {
                var adminHandler = scope.ServiceProvider.GetRequiredService<AdminCommandHandler>();
                await adminHandler.HandleAsync(message, cancellationToken);
                return ProcessTelegramUpdateResponse.Success();
            }

            if (IsCommand(message.Text, "/summary"))
            {
                var hours = GenerateSummaryHandler.ParseHoursFromCommand(message.Text);
                _logger.LogInformation("[Webhook] [{Chat}] @{User} requested /summary for {Hours}h", chatName, userName, hours);

                var summaryHandler = scope.ServiceProvider.GetRequiredService<GenerateSummaryHandler>();
                var summaryRequest = new GenerateSummaryRequest { Message = message, Hours = hours };
                var response = await summaryHandler.HandleAsync(summaryRequest, cancellationToken);

                if (response.IsSuccess)
                    _logCollector.IncrementSummaries();

                return ProcessTelegramUpdateResponse.Success();
            }

            if (IsCommand(message.Text, "/search"))
            {
                _logger.LogInformation("[Webhook] [{Chat}] @{User} requested /search", chatName, userName);
                var searchHandler = scope.ServiceProvider.GetRequiredService<SearchHandler>();
                await searchHandler.HandleAsync(message, cancellationToken);
                return ProcessTelegramUpdateResponse.Success();
            }

            if (IsCommand(message.Text, "/ask"))
            {
                _logger.LogInformation("[Webhook] [{Chat}] @{User} requested /ask", chatName, userName);
                var askHandler = scope.ServiceProvider.GetRequiredService<AskHandler>();
                await askHandler.HandleAsync(message, cancellationToken);
                return ProcessTelegramUpdateResponse.Success();
            }

            if (IsCommand(message.Text, "/recall"))
            {
                _logger.LogInformation("[Webhook] [{Chat}] @{User} requested /recall", chatName, userName);
                var recallHandler = scope.ServiceProvider.GetRequiredService<RecallHandler>();
                await recallHandler.HandleAsync(message, cancellationToken);
                return ProcessTelegramUpdateResponse.Success();
            }

            // Save regular message
            var saveHandler = scope.ServiceProvider.GetRequiredService<SaveMessageHandler>();
            var saveRequest = new SaveMessageRequest { Message = message };
            var saveResponse = await saveHandler.HandleAsync(saveRequest, cancellationToken);

            if (saveResponse.IsSuccess)
            {
                _logCollector.IncrementMessages();
            }
            else
            {
                _logger.LogError("Failed to save message: {Error}", saveResponse.ErrorMessage);
            }

            return ProcessTelegramUpdateResponse.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Webhook] Error processing message {MessageId} in {Chat}", message.MessageId, chatName);
            return ProcessTelegramUpdateResponse.InternalError("Internal server error");
        }
    }

    private static bool IsCommand(string? text, string command)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        return text.StartsWith(command, StringComparison.OrdinalIgnoreCase);
    }
}