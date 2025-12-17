using WatchmenBot.Tests.Fixtures;
using Xunit;

namespace WatchmenBot.Tests;

[Collection("Integration")]
public class OpenRouterClientIntegrationTests
{
    private readonly TestConfiguration _config;

    public OpenRouterClientIntegrationTests(TestConfiguration config)
    {
        _config = config;
    }

    [Fact]
    public async Task ChatCompletion_Works_WhenApiKeyProvided()
    {
        if (!_config.HasOpenRouterKey)
            return; // Skip if no API key

        var client = _config.CreateOpenRouterClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var (credits, _) = await client.GetCreditsAsync(cts.Token);
        Assert.True(credits >= 0);

        var result = await client.ChatCompletionAsync(
            "Ты — краткий ассистент. Отвечай на русском в одно предложение.",
            "Что такое Telegram бот?",
            0.5,
            cts.Token);

        Assert.False(string.IsNullOrWhiteSpace(result));
    }

    [Fact]
    public async Task ChatCompletionWithContext_Works()
    {
        if (!_config.HasOpenRouterKey)
            return;

        var client = _config.CreateOpenRouterClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var ragContext = """
            [10:00] Иван: Всем привет, кто идёт на митап?
            [10:05] Мария: Я иду! Будет интересно.
            [10:10] Петр: Не могу, работа.
            """;

        var result = await client.ChatCompletionWithContextAsync(
            "Ты — ассистент. Отвечай на основе контекста.",
            "Кто идёт на митап?",
            ragContext,
            0.3,
            cts.Token);

        Assert.False(string.IsNullOrWhiteSpace(result));
    }
}
