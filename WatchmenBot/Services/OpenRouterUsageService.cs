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

            var limit = keyInfo.Data.Limit;
            var used = keyInfo.Data.Usage ?? 0;
            var remaining = keyInfo.Data.LimitRemaining;

            return new UsageInfo
            {
                TotalCredits = limit ?? 0,
                UsedCredits = used,
                RemainingCredits = remaining ?? 0,
                HasLimit = limit.HasValue,
                HasRemainingInfo = remaining.HasValue
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Usage] Failed to get usage info");
            return null;
        }
    }
}

public class UsageInfo
{
    public double TotalCredits { get; set; }
    public double UsedCredits { get; set; }
    public double RemainingCredits { get; set; }
    public bool HasLimit { get; set; }
    public bool HasRemainingInfo { get; set; }

    public string ToTelegramHtml()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("<b>💰 OpenRouter:</b>");

        if (HasLimit && HasRemainingInfo)
        {
            // Has credit limit
            var percent = TotalCredits > 0 ? (RemainingCredits / TotalCredits * 100) : 0;
            sb.AppendLine($"• Осталось: ${RemainingCredits:F2} / ${TotalCredits:F2} ({percent:F0}%)");

            if (percent < 20)
            {
                sb.AppendLine("⚠️ <b>Пора пополнить!</b>");
            }
        }
        else if (HasRemainingInfo)
        {
            // Pay-as-you-go with remaining balance
            sb.AppendLine($"• Баланс: ${RemainingCredits:F2}");
            sb.AppendLine($"• Потрачено: ${UsedCredits:F2}");

            if (RemainingCredits < 1)
            {
                sb.AppendLine("⚠️ <b>Пора пополнить!</b>");
            }
        }
        else
        {
            // Only usage info available
            sb.AppendLine($"• Потрачено: ${UsedCredits:F2}");
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
