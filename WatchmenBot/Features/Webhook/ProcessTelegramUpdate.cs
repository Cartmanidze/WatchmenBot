using System.Net;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using WatchmenBot.Extensions;
using WatchmenBot.Features.Admin;
using WatchmenBot.Features.Messages;
using WatchmenBot.Features.Search;
using WatchmenBot.Features.Summary;
using WatchmenBot.Features.Summary.Services;
using WatchmenBot.Features.Admin.Services;

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

public class ProcessTelegramUpdateHandler(
    IConfiguration configuration,
    IServiceProvider serviceProvider,
    ITelegramBotClient bot,
    SummaryQueueService summaryQueue,
    LogCollector logCollector,
    ILogger<ProcessTelegramUpdateHandler> logger)
{
    public async Task<ProcessTelegramUpdateResponse> HandleAsync(ProcessTelegramUpdateRequest request, CancellationToken cancellationToken)
    {
        // Security validation using extensions
        var secretValidation = request.Headers.ValidateSecretToken(configuration["Telegram:WebhookSecret"]);
        if (!secretValidation.IsValid)
        {
            logger.LogWarning("Unauthorized webhook request from {RemoteIpAddress}: {Reason}",
                request.RemoteIpAddress, secretValidation.Reason);
            return ProcessTelegramUpdateResponse.Unauthorized(secretValidation.Reason);
        }

        var ipValidation = request.RemoteIpAddress.ValidateIpRange(configuration.GetValue<bool>("Telegram:ValidateIpRange"));
        if (!ipValidation.IsValid)
        {
            logger.LogWarning("Webhook request from invalid IP: {RemoteIpAddress}", request.RemoteIpAddress);
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
            using var scope = serviceProvider.CreateScope();

            // Handle private messages (for admin commands and /smart)
            if (message.Chat.Type == ChatType.Private)
            {
                // Handle text commands or document uploads with caption
                var commandText = message.Text ?? message.Caption ?? "";
                if (IsCommand(commandText, "/admin"))
                {
                    var adminHandler = scope.ServiceProvider.GetRequiredService<AdminCommandHandler>();
                    await adminHandler.HandleAsync(message, cancellationToken);
                }
                else if (IsSmartCommand(commandText))
                {
                    logger.LogInformation("[Webhook] [PM] @{User} requested /smart", userName);
                    var askHandler = scope.ServiceProvider.GetRequiredService<AskHandler>();
                    await askHandler.HandleQuestionAsync(message, cancellationToken);
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
                logger.LogInformation("[Webhook] [{Chat}] @{User} requested /summary for {Hours}h", chatName, userName, hours);

                // Сразу отвечаем пользователю и запускаем генерацию в фоне
                // Это позволяет избежать nginx timeout (60 сек)
                await bot.SendMessage(
                    chatId: message.Chat.Id,
                    text: "Генерирую выжимку, подождите...",
                    replyParameters: new ReplyParameters { MessageId = message.MessageId },
                    cancellationToken: cancellationToken);

                // Добавляем в очередь для фоновой обработки
                summaryQueue.EnqueueFromMessage(message, hours);

                return ProcessTelegramUpdateResponse.Success();
            }

            if (IsCommand(message.Text, "/ask"))
            {
                logger.LogInformation("[Webhook] [{Chat}] @{User} requested /ask", chatName, userName);
                var askHandler = scope.ServiceProvider.GetRequiredService<AskHandler>();
                await askHandler.HandleAsync(message, cancellationToken);
                return ProcessTelegramUpdateResponse.Success();
            }

            if (IsSmartCommand(message.Text))
            {
                logger.LogInformation("[Webhook] [{Chat}] @{User} requested /smart", chatName, userName);
                var askHandler = scope.ServiceProvider.GetRequiredService<AskHandler>();
                await askHandler.HandleQuestionAsync(message, cancellationToken);
                return ProcessTelegramUpdateResponse.Success();
            }

            if (IsCommand(message.Text, "/truth"))
            {
                logger.LogInformation("[Webhook] [{Chat}] @{User} requested /truth", chatName, userName);
                var factCheckHandler = scope.ServiceProvider.GetRequiredService<FactCheckHandler>();
                await factCheckHandler.HandleAsync(message, cancellationToken);
                return ProcessTelegramUpdateResponse.Success();
            }

            // Save regular message first
            var saveHandler = scope.ServiceProvider.GetRequiredService<SaveMessageHandler>();
            var saveRequest = new SaveMessageRequest { Message = message };
            var saveResponse = await saveHandler.HandleAsync(saveRequest, cancellationToken);

            if (saveResponse.IsSuccess)
            {
                logCollector.IncrementMessages();
            }
            else
            {
                logger.LogError("Failed to save message: {Error}", saveResponse.ErrorMessage);
            }

            // Check for "это правда?" trigger (after saving the message)
            if (IsTruthQuestion(message.Text))
            {
                logger.LogInformation("[Webhook] [{Chat}] @{User} triggered truth check with '{Text}'",
                    chatName, userName, message.Text?.Trim());
                var factCheckHandler = scope.ServiceProvider.GetRequiredService<FactCheckHandler>();
                await factCheckHandler.HandleAsync(message, cancellationToken);
            }

            return ProcessTelegramUpdateResponse.Success();
        }
        catch (Exception ex)
        {
            // IMPORTANT: Always return Success for processing errors!
            // If we return an error, Telegram will retry the webhook infinitely.
            // Errors are logged but don't cause webhook retries.
            logger.LogError(ex, "[Webhook] Error processing message {MessageId} in {Chat}", message.MessageId, chatName);
            return ProcessTelegramUpdateResponse.Success(); // Don't cause Telegram retry
        }
    }

    private static bool IsCommand(string? text, string command)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        return text.StartsWith(command, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSmartCommand(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        // Match /smart but not /smartsomething (only /smart or /smart followed by space/@ for bot mentions)
        return text.StartsWith("/smart ", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("/smart@", StringComparison.OrdinalIgnoreCase)
            || text.Equals("/smart", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTruthQuestion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var normalized = text.Trim().ToLowerInvariant();

        // Various ways to ask "is this true?"
        return normalized.Contains("это правда?")
            || normalized.Contains("правда?")
            || normalized.Contains("это правда ли")
            || normalized.Contains("серьёзно?")
            || normalized.Contains("серьезно?")
            || normalized.Contains("не пиздишь?")
            || normalized.Contains("не врёшь?")
            || normalized.Contains("не врешь?")
            || normalized.Contains("реально?")
            || normalized.Equals("правда?")
            || normalized.Equals("серьёзно?")
            || normalized.Equals("серьезно?");
    }
}