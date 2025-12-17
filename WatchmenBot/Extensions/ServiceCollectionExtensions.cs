using System.Security.Authentication;
using Microsoft.OpenApi.Models;
using Telegram.Bot;
using WatchmenBot.Features.Admin;
using WatchmenBot.Features.Messages;
using WatchmenBot.Features.Search;
using WatchmenBot.Features.Summary;
using WatchmenBot.Features.Webhook;
using WatchmenBot.Infrastructure.Database;
using WatchmenBot.Services;

namespace WatchmenBot.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddWatchmenBotServices(this IServiceCollection services, IConfiguration configuration)
    {
        // Telegram Bot Client
        services.AddSingleton<ITelegramBotClient>(sp =>
        {
            var token = configuration["Telegram:BotToken"] ?? string.Empty;
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException("Telegram:BotToken is required");
            }
            return new TelegramBotClient(token);
        });

        // Database
        services.AddSingleton<IDbConnectionFactory>(sp =>
        {
            var connectionString = configuration.GetConnectionString("Default") ??
                                 configuration["Database:ConnectionString"] ??
                                 throw new InvalidOperationException("Database connection string is required");
            return new PostgreSqlConnectionFactory(connectionString);
        });

        services.AddScoped<MessageStore>();
        services.AddHostedService<DatabaseInitializer>();

        // Health checks
        services.AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>("postgresql");

        // OpenRouter Client (DeepSeek V3.2 via OpenRouter)
        services.AddHttpClient<OpenRouterClient>()
            .ConfigurePrimaryHttpMessageHandler(sp =>
            {
                var useProxy = configuration.GetValue<bool>("OpenRouter:UseProxy", true);
                return new HttpClientHandler
                {
                    AutomaticDecompression = System.Net.DecompressionMethods.GZip |
                                           System.Net.DecompressionMethods.Deflate,
                    SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    UseProxy = useProxy
                };
            })
            .AddTypedClient<OpenRouterClient>((httpClient, serviceProvider) =>
            {
                var apiKey = configuration["OpenRouter:ApiKey"] ?? string.Empty;
                var baseUrl = configuration["OpenRouter:BaseUrl"] ?? "https://openrouter.ai/api";
                var model = configuration["OpenRouter:Model"] ?? "deepseek/deepseek-chat";
                var logger = serviceProvider.GetRequiredService<ILogger<OpenRouterClient>>();

                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    throw new InvalidOperationException("OpenRouter:ApiKey is required");
                }

                return new OpenRouterClient(httpClient, apiKey, baseUrl, model, logger);
            });

        // Embedding Client (OpenAI text-embedding-3-small)
        services.AddHttpClient<EmbeddingClient>()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.GZip |
                                       System.Net.DecompressionMethods.Deflate,
                SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
            })
            .AddTypedClient<EmbeddingClient>((httpClient, serviceProvider) =>
            {
                var apiKey = configuration["Embeddings:ApiKey"] ?? string.Empty;
                var baseUrl = configuration["Embeddings:BaseUrl"] ?? "https://api.openai.com/v1";
                var model = configuration["Embeddings:Model"] ?? "text-embedding-3-small";
                var dimensions = configuration.GetValue<int>("Embeddings:Dimensions", 1536);
                var logger = serviceProvider.GetRequiredService<ILogger<EmbeddingClient>>();

                // Embeddings are optional - if no API key, RAG will be disabled
                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    serviceProvider.GetRequiredService<ILogger<EmbeddingClient>>()
                        .LogWarning("Embeddings:ApiKey not configured. RAG features will be disabled.");
                }

                return new EmbeddingClient(httpClient, apiKey, baseUrl, model, dimensions, logger);
            });

        // Embedding Service for RAG
        services.AddScoped<EmbeddingService>();

        // Smart Summary Service (uses embeddings for topic extraction)
        services.AddScoped<SmartSummaryService>();

        // Admin Services
        services.AddSingleton<LogCollector>();
        services.AddScoped<AdminSettingsStore>();

        // Background Services
        services.AddHostedService<DailySummaryService>();
        services.AddHostedService<BackgroundEmbeddingService>();
        services.AddHostedService<TelegramPollingService>();
        services.AddSingleton<DailyLogReportService>();
        services.AddHostedService(sp => sp.GetRequiredService<DailyLogReportService>());

        // Feature Handlers
        services.AddScoped<ProcessTelegramUpdateHandler>();
        services.AddScoped<SaveMessageHandler>();
        services.AddScoped<GenerateSummaryHandler>();
        services.AddScoped<AdminCommandHandler>();
        services.AddScoped<SetWebhookHandler>();
        services.AddScoped<DeleteWebhookHandler>();
        services.AddScoped<GetWebhookInfoHandler>();

        // Search Handlers (embedding-based)
        services.AddScoped<SearchHandler>();
        services.AddScoped<AskHandler>();
        services.AddScoped<RecallHandler>();

        // Controllers
        services.AddControllers();

        // Swagger / OpenAPI
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "WatchmenBot API",
                Version = "v1",
                Description = "Telegram bot for group chat analytics with AI-powered daily summaries and RAG support"
            });
        });

        return services;
    }
}
