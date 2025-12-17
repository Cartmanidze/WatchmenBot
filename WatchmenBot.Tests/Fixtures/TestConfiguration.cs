using System.Net;
using System.Security.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using WatchmenBot.Services;
using Xunit;

namespace WatchmenBot.Tests.Fixtures;

public class TestConfiguration : IDisposable
{
    public IConfiguration Configuration { get; }
    public string? OpenRouterApiKey { get; }
    public string? OpenAiApiKey { get; }

    private readonly HttpClient _httpClient;
    private readonly HttpClientHandler _handler;

    public TestConfiguration()
    {
        Configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddJsonFile("..\\..\\..\\..\\WatchmenBot\\appsettings.Development.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        OpenRouterApiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY")
                           ?? Configuration["OpenRouter:ApiKey"];

        OpenAiApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
                       ?? Configuration["Embeddings:ApiKey"];

        _handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            UseProxy = false
        };

        _httpClient = new HttpClient(_handler, disposeHandler: false);
    }

    public bool HasOpenRouterKey => !string.IsNullOrWhiteSpace(OpenRouterApiKey);
    public bool HasOpenAiKey => !string.IsNullOrWhiteSpace(OpenAiApiKey);

    public OpenRouterClient CreateOpenRouterClient()
    {
        if (!HasOpenRouterKey)
            throw new InvalidOperationException("OpenRouter API key not configured");

        return new OpenRouterClient(
            _httpClient,
            OpenRouterApiKey!,
            Configuration["OpenRouter:BaseUrl"] ?? "https://openrouter.ai/api",
            Configuration["OpenRouter:Model"] ?? "deepseek/deepseek-chat",
            NullLogger<OpenRouterClient>.Instance);
    }

    public EmbeddingClient CreateEmbeddingClient()
    {
        if (!HasOpenAiKey)
            throw new InvalidOperationException("OpenAI API key not configured");

        return new EmbeddingClient(
            _httpClient,
            OpenAiApiKey!,
            Configuration["Embeddings:BaseUrl"] ?? "https://api.openai.com/v1",
            Configuration["Embeddings:Model"] ?? "text-embedding-3-small",
            Configuration.GetValue<int>("Embeddings:Dimensions", 1536),
            NullLogger<EmbeddingClient>.Instance);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _handler.Dispose();
    }
}

[CollectionDefinition("Integration")]
public class IntegrationTestCollection : ICollectionFixture<TestConfiguration>
{
}
