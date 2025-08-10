using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using WatchmenBot.Services;
using Xunit;

namespace WatchmenBot.Tests
{
    public class KimiClientIntegrationTests
    {
        [Fact]
        public async Task CreateDailySummary_Works_WhenApiKeyProvided()
        {
            var apiKey = Environment.GetEnvironmentVariable("KIMI_API_KEY");
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                var config = new ConfigurationBuilder()
                    .SetBasePath(AppContext.BaseDirectory)
                    .AddJsonFile("appsettings.Development.json", optional: true)
                    .AddJsonFile("..\\..\\..\\..\\WatchmenBot\\appsettings.Development.json", optional: true)
                    .Build();
                apiKey = config["Kimi:ApiKey"];
            }

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return;
            }

            using var http = new HttpClient();
            var client = new KimiClient(http, apiKey!, "https://openrouter.ai/api", "moonshotai/kimi-k2");
            var system = "Ты — Kimi2, кратко отвечай на русском";
            var user = "Суммируй: Привет! Это тест интеграции.";
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            var result = await client.CreateDailySummaryAsync(system, user, cts.Token);

            Assert.False(string.IsNullOrWhiteSpace(result));
        }
    }
} 