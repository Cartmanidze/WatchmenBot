using Dapper;
using WatchmenBot.Infrastructure.Database;

namespace WatchmenBot.Services;

public class AdminSettingsStore
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AdminSettingsStore> _logger;

    // Default settings
    private const string DefaultSummaryTime = "21:00";
    private const string DefaultReportTime = "10:00";
    private const string DefaultTimezone = "+06:00"; // Omsk

    public AdminSettingsStore(
        IDbConnectionFactory connectionFactory,
        IConfiguration configuration,
        ILogger<AdminSettingsStore> logger)
    {
        _connectionFactory = connectionFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<string> GetSummaryTimeAsync()
    {
        return await GetSettingAsync("summary_time")
               ?? _configuration["DailySummary:TimeOfDay"]
               ?? DefaultSummaryTime;
    }

    public async Task SetSummaryTimeAsync(string time)
    {
        await SetSettingAsync("summary_time", time);
        _logger.LogInformation("[Admin] Summary time changed to {Time}", time);
    }

    public async Task<string> GetReportTimeAsync()
    {
        return await GetSettingAsync("report_time")
               ?? _configuration["Admin:ReportTime"]
               ?? DefaultReportTime;
    }

    public async Task SetReportTimeAsync(string time)
    {
        await SetSettingAsync("report_time", time);
        _logger.LogInformation("[Admin] Report time changed to {Time}", time);
    }

    public async Task<TimeSpan> GetTimezoneOffsetAsync()
    {
        var offsetStr = await GetSettingAsync("timezone_offset")
                        ?? _configuration["Admin:TimezoneOffset"]
                        ?? DefaultTimezone;

        if (TimeSpan.TryParse(offsetStr.TrimStart('+'), out var offset))
            return offset;

        return TimeSpan.FromHours(6); // Default Omsk
    }

    public async Task SetTimezoneOffsetAsync(string offset)
    {
        await SetSettingAsync("timezone_offset", offset);
        _logger.LogInformation("[Admin] Timezone offset changed to {Offset}", offset);
    }

    public long GetAdminUserId()
    {
        return _configuration.GetValue<long>("Admin:UserId", 0);
    }

    public string? GetAdminUsername()
    {
        return _configuration["Admin:Username"];
    }

    public bool IsAdmin(long userId, string? username)
    {
        var adminUserId = GetAdminUserId();
        var adminUsername = GetAdminUsername();

        if (adminUserId > 0 && userId == adminUserId)
            return true;

        if (!string.IsNullOrEmpty(adminUsername) &&
            username?.Equals(adminUsername, StringComparison.OrdinalIgnoreCase) == true)
            return true;

        return false;
    }

    private async Task<string?> GetSettingAsync(string key)
    {
        try
        {
            using var connection = await _connectionFactory.CreateConnectionAsync();
            return await connection.QuerySingleOrDefaultAsync<string>(
                "SELECT value FROM admin_settings WHERE key = @Key",
                new { Key = key });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get setting {Key}", key);
            return null;
        }
    }

    private async Task SetSettingAsync(string key, string value)
    {
        try
        {
            using var connection = await _connectionFactory.CreateConnectionAsync();
            await connection.ExecuteAsync(
                """
                INSERT INTO admin_settings (key, value, updated_at)
                VALUES (@Key, @Value, NOW())
                ON CONFLICT (key) DO UPDATE SET
                    value = EXCLUDED.value,
                    updated_at = NOW()
                """,
                new { Key = key, Value = value });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set setting {Key}", key);
            throw;
        }
    }

    public async Task<Dictionary<string, string>> GetAllSettingsAsync()
    {
        var result = new Dictionary<string, string>
        {
            ["summary_time"] = await GetSummaryTimeAsync(),
            ["report_time"] = await GetReportTimeAsync(),
            ["timezone_offset"] = (await GetTimezoneOffsetAsync()).ToString(@"hh\:mm")
        };
        return result;
    }
}
