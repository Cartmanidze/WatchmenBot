using System.Text.Json;

namespace WatchmenBot.Services.Memory;

/// <summary>
/// Static helper methods for memory services
/// </summary>
public static class MemoryHelpers
{
    /// <summary>
    /// Parse JSON array from string
    /// </summary>
    public static List<string> ParseJsonArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Get string array from JSON element
    /// </summary>
    public static List<string> GetJsonStringArray(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return [];

        return arr.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString() ?? "")
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
    }

    /// <summary>
    /// Merge two lists with deduplication and max limit
    /// </summary>
    public static List<string> MergeLists(List<string> existing, List<string> newItems, int maxItems)
    {
        var merged = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        foreach (var item in newItems)
        {
            if (!string.IsNullOrWhiteSpace(item))
                merged.Add(item);
        }
        return merged.Take(maxItems).ToList();
    }

    /// <summary>
    /// Truncate text to max length
    /// </summary>
    public static string TruncateText(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            return text;
        return text[..(maxLength - 3)] + "...";
    }

    /// <summary>
    /// Get human-readable time ago string
    /// </summary>
    public static string GetTimeAgo(DateTimeOffset time)
    {
        var diff = DateTimeOffset.UtcNow - time;
        if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes}м назад";
        if (diff.TotalHours < 24) return $"{(int)diff.TotalHours}ч назад";
        if (diff.TotalDays < 7) return $"{(int)diff.TotalDays}д назад";
        return time.ToString("dd.MM");
    }
}
