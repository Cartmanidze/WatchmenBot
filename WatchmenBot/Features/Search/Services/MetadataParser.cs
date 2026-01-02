using System.Text.Json;

namespace WatchmenBot.Features.Search.Services;

/// <summary>
/// Parses metadata JSON from search results
/// </summary>
public static class MetadataParser
{
    /// <summary>
    /// Parse timestamp from metadata JSON
    /// </summary>
    public static DateTimeOffset ParseTimestamp(string? metadataJson)
    {
        if (string.IsNullOrEmpty(metadataJson))
            return DateTimeOffset.MinValue;

        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            if (doc.RootElement.TryGetProperty("DateUtc", out var dateEl))
                return dateEl.GetDateTimeOffset();
        }
        catch
        {
            // Ignore parsing errors
        }

        return DateTimeOffset.MinValue;
    }

    /// <summary>
    /// Try to parse timestamp, returning null if not found or invalid
    /// </summary>
    public static DateTimeOffset? TryParseTimestamp(string? metadataJson)
    {
        var timestamp = ParseTimestamp(metadataJson);
        return timestamp == DateTimeOffset.MinValue ? null : timestamp;
    }

    /// <summary>
    /// Extract username from metadata JSON
    /// </summary>
    public static string? ParseUsername(string? metadataJson)
    {
        if (string.IsNullOrEmpty(metadataJson))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            if (doc.RootElement.TryGetProperty("Username", out var usernameEl))
                return usernameEl.GetString();
        }
        catch
        {
            // Ignore parsing errors
        }

        return null;
    }

    /// <summary>
    /// Extract display name from metadata JSON
    /// </summary>
    public static string? ParseDisplayName(string? metadataJson)
    {
        if (string.IsNullOrEmpty(metadataJson))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            if (doc.RootElement.TryGetProperty("DisplayName", out var displayNameEl))
                return displayNameEl.GetString();
        }
        catch
        {
            // Ignore parsing errors
        }

        return null;
    }
}
