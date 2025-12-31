using Telegram.Bot.Types;

namespace WatchmenBot.Features.Search;

/// <summary>
/// Static helper methods for AskHandler
/// </summary>
public static class AskHandlerHelpers
{
    /// <summary>
    /// Parse question from command text (removes /ask or /smart prefix)
    /// </summary>
    public static string ParseQuestion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var spaceIndex = text.IndexOf(' ');
        if (spaceIndex < 0)
            return string.Empty;

        return text[(spaceIndex + 1)..].Trim();
    }

    /// <summary>
    /// Get user's display name (FirstName LastName, or Username, or ID)
    /// </summary>
    public static string GetDisplayName(User? user)
    {
        if (user == null)
            return "Аноним";

        if (!string.IsNullOrWhiteSpace(user.FirstName))
        {
            return string.IsNullOrWhiteSpace(user.LastName)
                ? user.FirstName
                : $"{user.FirstName} {user.LastName}";
        }

        return !string.IsNullOrWhiteSpace(user.Username)
            ? user.Username
            : user.Id.ToString();
    }

    /// <summary>
    /// Parse timestamp from metadata JSON
    /// </summary>
    public static DateTimeOffset? ParseTimestamp(string? metadataJson)
    {
        if (string.IsNullOrEmpty(metadataJson))
            return null;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(metadataJson);
            if (doc.RootElement.TryGetProperty("DateUtc", out var dateEl))
                return dateEl.GetDateTimeOffset();
        }
        catch { }

        return null;
    }

    /// <summary>
    /// Rough token count estimate (~4 chars per token)
    /// </summary>
    public static int EstimateTokens(string text)
    {
        return text.Length / 4;
    }
}
