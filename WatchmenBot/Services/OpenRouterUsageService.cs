using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace WatchmenBot.Services;

/// <summary>
/// Service to fetch OpenRouter API usage and credits
/// </summary>
public class OpenRouterUsageService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<OpenRouterUsageService> _logger;

    public OpenRouterUsageService(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<OpenRouterUsageService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<UsageInfo?> GetUsageInfoAsync(CancellationToken ct = default)
    {
        try
        {
            var apiKey = _configuration["Llm:Providers:0:ApiKey"];
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                _logger.LogWarning("[Usage] No API key configured");
                return null;
            }

            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

            // Get credits info
            var response = await _httpClient.GetAsync("https://openrouter.ai/api/v1/auth/key", ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("[Usage] Failed to get key info: {Status}", response.StatusCode);
                return null;
            }

            var keyInfo = await response.Content.ReadFromJsonAsync<OpenRouterKeyResponse>(ct);
            if (keyInfo?.Data == null)
                return null;

            var credits = keyInfo.Data.Limit ?? 0;
            var used = keyInfo.Data.Usage ?? 0;
            var remaining = credits - used;

            // Estimate days remaining based on daily usage
            double? daysRemaining = null;
            if (keyInfo.Data.RateLimitInterval != null && used > 0)
            {
                // Calculate daily average from usage
                var dailyUsage = await GetDailyUsageAsync(apiKey, ct);
                if (dailyUsage > 0)
                {
                    daysRemaining = remaining / dailyUsage;
                }
            }

            return new UsageInfo
            {
                TotalCredits = credits,
                UsedCredits = used,
                RemainingCredits = remaining,
                EstimatedDaysRemaining = daysRemaining,
                IsUnlimited = keyInfo.Data.IsFreeTier == false && keyInfo.Data.Limit == null
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Usage] Failed to get usage info");
            return null;
        }
    }

    private async Task<double> GetDailyUsageAsync(string apiKey, CancellationToken ct)
    {
        try
        {
            // Get usage history for last 7 days
            var response = await _httpClient.GetAsync("https://openrouter.ai/api/v1/auth/key", ct);
            if (!response.IsSuccessStatusCode)
                return 0;

            var keyInfo = await response.Content.ReadFromJsonAsync<OpenRouterKeyResponse>(ct);

            // Simple estimate: total usage divided by days since first use
            // OpenRouter doesn't provide detailed history, so we estimate
            if (keyInfo?.Data?.Usage > 0)
            {
                // Assume account is 30 days old on average
                return keyInfo.Data.Usage.Value / 30.0;
            }

            return 0;
        }
        catch
        {
            return 0;
        }
    }
}

public class UsageInfo
{
    public double TotalCredits { get; set; }
    public double UsedCredits { get; set; }
    public double RemainingCredits { get; set; }
    public double? EstimatedDaysRemaining { get; set; }
    public bool IsUnlimited { get; set; }

    public string ToTelegramHtml()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("<b>💰 OpenRouter:</b>");

        if (IsUnlimited)
        {
            sb.AppendLine("• Лимит: безлимитный");
            sb.AppendLine($"• Потрачено: ${UsedCredits:F2}");
        }
        else
        {
            var percent = TotalCredits > 0 ? (RemainingCredits / TotalCredits * 100) : 0;
            sb.AppendLine($"• Осталось: ${RemainingCredits:F2} / ${TotalCredits:F2} ({percent:F0}%)");

            if (EstimatedDaysRemaining.HasValue)
            {
                if (EstimatedDaysRemaining.Value > 365)
                {
                    sb.AppendLine("• Хватит на: >1 года");
                }
                else if (EstimatedDaysRemaining.Value > 30)
                {
                    sb.AppendLine($"• Хватит на: ~{EstimatedDaysRemaining.Value / 30:F0} мес");
                }
                else
                {
                    sb.AppendLine($"• Хватит на: ~{EstimatedDaysRemaining.Value:F0} дней");
                }
            }

            // Warning if low
            if (percent < 20)
            {
                sb.AppendLine("⚠️ <b>Пора пополнить!</b>");
            }
        }

        return sb.ToString();
    }
}

// OpenRouter API response models
public class OpenRouterKeyResponse
{
    [JsonPropertyName("data")]
    public OpenRouterKeyData? Data { get; set; }
}

public class OpenRouterKeyData
{
    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("usage")]
    public double? Usage { get; set; }

    [JsonPropertyName("limit")]
    public double? Limit { get; set; }

    [JsonPropertyName("is_free_tier")]
    public bool? IsFreeTier { get; set; }

    [JsonPropertyName("rate_limit")]
    public OpenRouterRateLimit? RateLimit { get; set; }

    [JsonPropertyName("limit_remaining")]
    public double? LimitRemaining { get; set; }

    [JsonPropertyName("rate_limit_interval")]
    public string? RateLimitInterval { get; set; }
}

public class OpenRouterRateLimit
{
    [JsonPropertyName("requests")]
    public int? Requests { get; set; }

    [JsonPropertyName("interval")]
    public string? Interval { get; set; }
}
