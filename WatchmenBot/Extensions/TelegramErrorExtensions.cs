using Telegram.Bot.Exceptions;

namespace WatchmenBot.Extensions;

/// <summary>
/// Extension methods for classifying Telegram API errors.
/// Used for automatic chat deactivation when bot is kicked or chat is deleted.
/// </summary>
public static class TelegramErrorExtensions
{
    /// <summary>
    /// Check if error indicates the bot was kicked from the chat.
    /// Error code 403: "Forbidden: bot was kicked from the supergroup chat"
    /// </summary>
    public static bool IsBotKickedError(this ApiRequestException ex)
    {
        return ex.ErrorCode == 403 &&
               (ex.Message.Contains("kicked", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("blocked", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("deactivated", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("forbidden", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Check if error indicates the chat no longer exists or is inaccessible.
    /// Error code 400: "Bad Request: chat not found" or "PEER_ID_INVALID"
    /// Note: CHAT_NOT_MODIFIED is NOT included — it means settings are already set, not a deleted chat.
    /// </summary>
    public static bool IsChatGoneError(this ApiRequestException ex)
    {
        return ex.ErrorCode == 400 &&
               (ex.Message.Contains("chat not found", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("PEER_ID_INVALID", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Check if error indicates chat is permanently inaccessible and should be deactivated.
    /// Combines kicked and chat-gone errors.
    /// </summary>
    public static bool ShouldDeactivateChat(this ApiRequestException ex)
    {
        return ex.IsBotKickedError() || ex.IsChatGoneError();
    }

    /// <summary>
    /// Get a human-readable reason for chat deactivation.
    /// Truncates to 255 characters to fit database column.
    /// </summary>
    public static string GetDeactivationReason(this ApiRequestException ex)
    {
        string reason;

        if (ex.IsBotKickedError())
        {
            reason = $"Bot kicked from chat (HTTP {ex.ErrorCode})";
        }
        else if (ex.IsChatGoneError())
        {
            reason = $"Chat no longer exists (HTTP {ex.ErrorCode})";
        }
        else
        {
            reason = $"Telegram API error {ex.ErrorCode}: {ex.Message}";
        }

        // Truncate to fit VARCHAR(255) column
        const int maxLength = 255;
        return reason.Length > maxLength ? reason[..(maxLength - 3)] + "..." : reason;
    }
}
