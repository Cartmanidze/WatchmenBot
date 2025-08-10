using System.Net.Http.Headers;
using System.Net;
using System.Text;
using System.Text.Json;

namespace WatchmenBot.Services
{
    public class KimiClient
    {
        private readonly HttpClient _httpClient;
        private readonly string _model;
        private readonly string _apiKey;
        private readonly string _baseUrl;

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        public KimiClient(HttpClient httpClient, string apiKey, string baseUrl, string model)
        {
            _httpClient = httpClient;
            _apiKey = apiKey;
            _baseUrl = string.IsNullOrWhiteSpace(baseUrl) ? "https://openrouter.ai/api" : baseUrl.TrimEnd('/');
            _model = string.IsNullOrWhiteSpace(model) ? "moonshotai/kimi-k2" : model;
        }

        public async Task<string> CreateDailySummaryAsync(string systemPrompt, string userPrompt, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1/chat/completions")
            {
                Version = HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            req.Headers.Add("HTTP-Referer", "https://local.tool");
            req.Headers.Add("X-Title", "WatchmenBot");

            var body = new
            {
                model = _model,
                temperature = 0.6,
                messages = new object[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt }
                }
            };

            req.Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");
            using var resp = await _httpClient.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var content = root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
            return content ?? string.Empty;
        }
    }
} 