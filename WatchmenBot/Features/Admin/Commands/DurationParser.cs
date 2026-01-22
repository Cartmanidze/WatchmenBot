using System.Text.RegularExpressions;

namespace WatchmenBot.Features.Admin.Commands;

/// <summary>
/// Utility for parsing duration strings like "30m", "24h", "7d", "1w"
/// </summary>
public static partial class DurationParser
{
    /// <summary>
    /// Parse duration string into TimeSpan
    /// Supported formats: 30m (minutes), 24h (hours), 7d (days), 1w (weeks)
    /// </summary>
    /// <returns>TimeSpan if parsed successfully, null otherwise</returns>
    public static TimeSpan? Parse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        var match = DurationRegex().Match(input.Trim().ToLowerInvariant());
        if (!match.Success)
            return null;

        if (!int.TryParse(match.Groups["value"].Value, out var value) || value <= 0)
            return null;

        var unit = match.Groups["unit"].Value;

        return unit switch
        {
            "m" => TimeSpan.FromMinutes(value),
            "h" => TimeSpan.FromHours(value),
            "d" => TimeSpan.FromDays(value),
            "w" => TimeSpan.FromDays(value * 7),
            _ => null
        };
    }

    /// <summary>
    /// Format TimeSpan to human-readable Russian string
    /// </summary>
    public static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalDays >= 7)
        {
            var weeks = (int)(duration.TotalDays / 7);
            return $"{weeks} {Pluralize(weeks, "неделю", "недели", "недель")}";
        }

        if (duration.TotalDays >= 1)
        {
            var days = (int)duration.TotalDays;
            return $"{days} {Pluralize(days, "день", "дня", "дней")}";
        }

        if (duration.TotalHours >= 1)
        {
            var hours = (int)duration.TotalHours;
            return $"{hours} {Pluralize(hours, "час", "часа", "часов")}";
        }

        var minutes = (int)duration.TotalMinutes;
        return $"{minutes} {Pluralize(minutes, "минуту", "минуты", "минут")}";
    }

    /// <summary>
    /// Format expiration date to human-readable string
    /// </summary>
    public static string FormatExpiration(DateTime? expiresAt)
    {
        if (!expiresAt.HasValue)
            return "навсегда";

        var remaining = expiresAt.Value - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero)
            return "истёк";

        return $"через {FormatDuration(remaining)}";
    }

    private static string Pluralize(int number, string one, string few, string many)
    {
        var n = Math.Abs(number) % 100;
        if (n is >= 11 and <= 19)
            return many;

        return (n % 10) switch
        {
            1 => one,
            >= 2 and <= 4 => few,
            _ => many
        };
    }

    [GeneratedRegex(@"^(?<value>\d+)(?<unit>[mhdw])$")]
    private static partial Regex DurationRegex();
}
