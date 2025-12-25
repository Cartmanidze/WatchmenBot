using System.Net;
using System.Security.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using WatchmenBot.Services;
using WatchmenBot.Services.Llm;
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

        var router = CreateLlmRouter();
        return new OpenRouterClient(router, NullLogger<OpenRouterClient>.Instance);
    }

    public LlmRouter CreateLlmRouter()
    {
        if (!HasOpenRouterKey)
            throw new InvalidOperationException("OpenRouter API key not configured");

        var factory = new LlmProviderFactory(
            new TestHttpClientFactory(_httpClient),
            NullLoggerFactory.Instance);

        var router = new LlmRouter(NullLogger<LlmRouter>.Instance);

        var options = new LlmProviderOptions
        {
            Name = "openrouter",
            Type = "openrouter",
            ApiKey = OpenRouterApiKey!,
            BaseUrl = Configuration["OpenRouter:BaseUrl"] ?? "https://openrouter.ai/api/v1",
            Model = Configuration["OpenRouter:Model"] ?? "deepseek/deepseek-chat",
            Priority = 1,
            Tags = ["default"]
        };

        router.Register(factory.Create(options), options);
        return router;
    }

    private class TestHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;
        public TestHttpClientFactory(HttpClient client) => _client = client;
        public HttpClient CreateClient(string name) => _client;
    }

    public EmbeddingClient CreateEmbeddingClient()
    {
        if (!HasOpenAiKey)
            throw new InvalidOperationException("Embeddings API key not configured");

        var providerStr = Configuration["Embeddings:Provider"] ?? "openai";
        var provider = providerStr.ToLowerInvariant() switch
        {
            "huggingface" or "hf" => EmbeddingProvider.HuggingFace,
            _ => EmbeddingProvider.OpenAI
        };

        return new EmbeddingClient(
            _httpClient,
            OpenAiApiKey!,
            Configuration["Embeddings:BaseUrl"] ?? string.Empty,
            Configuration["Embeddings:Model"] ?? string.Empty,
            Configuration.GetValue<int>("Embeddings:Dimensions", 0),
            provider,
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
