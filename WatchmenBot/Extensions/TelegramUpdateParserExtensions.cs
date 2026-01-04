using System.Text.Json;
using Telegram.Bot.Types;

namespace WatchmenBot.Extensions;

public static class TelegramUpdateParserExtensions
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static async Task<(Update? Update, string? ErrorMessage)> ParseTelegramUpdateAsync(
        this Stream requestBody, 
        CancellationToken cancellationToken)
    {
        try
        {
            using var reader = new StreamReader(requestBody);
            var body = await reader.ReadToEndAsync(cancellationToken);
            
            if (string.IsNullOrWhiteSpace(body))
            {
                return (null, "Empty request body");
            }

            var update = JsonSerializer.Deserialize<Update>(body, JsonOptions);
            return (update, null);
        }
        catch (JsonException ex)
        {
            return (null, $"Invalid JSON: {ex.Message}");
        }
        catch (Exception ex)
        {
            return (null, $"Failed to parse update: {ex.Message}");
        }
    }

    public static bool HasMessage(this Update update)
    {
        return update.Message is not null;
    }
}