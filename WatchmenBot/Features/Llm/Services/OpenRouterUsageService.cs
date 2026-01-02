using System.Text.Json.Serialization;

namespace WatchmenBot.Features.Llm.Services;

/// <summary>
/// Service to fetch OpenRouter API usage and credits
/// </summary>
public class OpenRouterUsageService(
    HttpClient httpClient,
    IConfiguration configuration,
    ILogger<OpenRouterUsageService> logger)
{
    public async Task<UsageInfo?> GetUsageInfoAsync(CancellationToken ct = default)
    {
        try
        {
            var apiKey = configuration["Llm:Providers:0:ApiKey"];
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                logger.LogWarning("[Usage] No API key configured");
                return null;
            }

            httpClient.DefaultRequestHeaders.Clear();
            httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

            // Get credits balance from /api/v1/credits
            var creditsResponse = await httpClient.GetAsync("https://openrouter.ai/api/v1/credits", ct);
            if (!creditsResponse.IsSuccessStatusCode)
            {
                logger.LogWarning("[Usage] Failed to get credits: {Status}", creditsResponse.StatusCode);
                return null;
            }

            var credits = await creditsResponse.Content.ReadFromJsonAsync<OpenRouterCreditsResponse>(ct);
            if (credits?.Data == null)
                return null;

            var total = credits.Data.TotalCredits ?? 0;
            var used = credits.Data.TotalUsage ?? 0;
            var balance = total - used;

            return new UsageInfo
            {
                TotalCredits = total,
                UsedCredits = used,
                Balance = balance
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Usage] Failed to get usage info");
            return null;
        }
    }
}

// Response from /api/v1/credits
public class OpenRouterCreditsResponse
{
    [JsonPropertyName("data")]
    public OpenRouterCreditsData? Data { get; set; }
}

public class OpenRouterCreditsData
{
    [JsonPropertyName("total_credits")]
    public double? TotalCredits { get; set; }

    [JsonPropertyName("total_usage")]
    public double? TotalUsage { get; set; }
}

public class UsageInfo
{
    public double TotalCredits { get; set; }
    public double UsedCredits { get; set; }
    public double Balance { get; set; }

    public string ToTelegramHtml()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("<b>💰 OpenRouter:</b>");
        sb.AppendLine($"• Баланс: ${Balance:F2}");
        sb.AppendLine($"• Потрачено: ${UsedCredits:F2}");
        sb.AppendLine($"• Пополнено: ${TotalCredits:F2}");

        if (Balance < 1)
        {
            sb.AppendLine("⚠️ <b>Пора пополнить!</b>");
        }
        else if (Balance < 5)
        {
            sb.AppendLine("💡 Скоро закончится");
        }

        return sb.ToString();
    }
}
