using Microsoft.OpenApi.Models;
using Telegram.Bot;
using WatchmenBot.Policies;
using WatchmenBot.Features.Admin;
using WatchmenBot.Features.Messages;
using WatchmenBot.Features.Search;
using WatchmenBot.Features.Summary;
using WatchmenBot.Features.Webhook;
using WatchmenBot.Infrastructure.Database;
using WatchmenBot.Services;
using WatchmenBot.Services.Llm;

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

        // LLM Router with multiple providers
        services.AddSingleton<LlmProviderFactory>();
        services.AddSingleton<LlmRouter>(sp =>
        {
            var factory = sp.GetRequiredService<LlmProviderFactory>();
            var logger = sp.GetRequiredService<ILogger<LlmRouter>>();
            var router = new LlmRouter(logger);

            var providersSection = configuration.GetSection("Llm:Providers");
            var providers = providersSection.Get<LlmProviderOptions[]>() ?? [];

            if (providers.Length == 0)
            {
                // Fallback to old OpenRouter config for backward compatibility
                var apiKey = configuration["OpenRouter:ApiKey"];
                var baseUrl = configuration["OpenRouter:BaseUrl"] ?? "https://openrouter.ai/api/v1";
                var model = configuration["OpenRouter:Model"] ?? "deepseek/deepseek-chat";

                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    var legacyOptions = new LlmProviderOptions
                    {
                        Name = "openrouter",
                        Type = "openrouter",
                        ApiKey = apiKey,
                        BaseUrl = baseUrl,
                        Model = model,
                        Priority = 1,
                        Tags = ["default"]
                    };
                    router.Register(factory.Create(legacyOptions), legacyOptions);
                }
            }
            else
            {
                foreach (var options in providers.Where(p => p.Enabled))
                {
                    router.Register(factory.Create(options), options);
                }
            }

            return router;
        });

        // Keep OpenRouterClient for backward compatibility (wraps LlmRouter)
        services.AddSingleton<OpenRouterClient>(sp =>
        {
            var router = sp.GetRequiredService<LlmRouter>();
            var logger = sp.GetRequiredService<ILogger<OpenRouterClient>>();
            return new OpenRouterClient(router, logger);
        });

        // Embedding Client (OpenAI or HuggingFace)
        services.AddHttpClient<EmbeddingClient>()
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.GZip |
                                       System.Net.DecompressionMethods.Deflate,
                PooledConnectionLifetime = TimeSpan.FromMinutes(2), // Prevent stale connections
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1)
            })
            .ConfigureHttpClient(client =>
            {
                client.Timeout = TimeSpan.FromSeconds(120); // Longer timeout for HuggingFace cold starts
            })
            .AddTypedClient<EmbeddingClient>((httpClient, serviceProvider) =>
            {
                var apiKey = configuration["Embeddings:ApiKey"] ?? string.Empty;
                var baseUrl = configuration["Embeddings:BaseUrl"] ?? string.Empty;
                var model = configuration["Embeddings:Model"] ?? string.Empty;
                var dimensions = configuration.GetValue<int>("Embeddings:Dimensions", 0);
                var providerStr = configuration["Embeddings:Provider"] ?? "openai";
                var logger = serviceProvider.GetRequiredService<ILogger<EmbeddingClient>>();

                // Parse provider
                var provider = providerStr.ToLowerInvariant() switch
                {
                    "huggingface" or "hf" => EmbeddingProvider.HuggingFace,
                    _ => EmbeddingProvider.OpenAI
                };

                // Embeddings are optional - if no API key, RAG will be disabled
                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    logger.LogWarning("Embeddings:ApiKey not configured. RAG features will be disabled.");
                }
                else
                {
                    logger.LogInformation("Embeddings configured: Provider={Provider}, Dimensions={Dimensions}",
                        provider, dimensions > 0 ? dimensions : (provider == EmbeddingProvider.HuggingFace ? 1024 : 1536));
                }

                return new EmbeddingClient(httpClient, apiKey, baseUrl, model, dimensions, provider, logger);
            });

        // Embedding Service for RAG
        services.AddScoped<EmbeddingService>();

        // RAG Fusion Service (multi-query search with RRF)
        services.AddScoped<RagFusionService>();

        // Rerank Service (LLM-based reranking)
        services.AddScoped<RerankService>();

        // LLM Memory Service (user profiles + conversation memory)
        services.AddScoped<LlmMemoryService>();

        // Smart Summary Service (uses embeddings for topic extraction)
        services.AddScoped<SmartSummaryService>();

        // Chat Import Services
        services.AddScoped<TelegramExportParser>();
        services.AddScoped<ChatImportService>();

        // Admin Services
        services.AddSingleton<LogCollector>();
        services.AddSingleton<AdminSettingsStore>();
        services.AddSingleton<PromptSettingsStore>();
        services.AddSingleton<DebugService>();

        // Usage tracking
        services.AddHttpClient<OpenRouterUsageService>();

        // Profile System Services
        services.AddSingleton<ProfileQueueService>();
        services.AddHostedService<ProfileWorkerService>();
        services.AddHostedService<ProfileGeneratorService>();

        // Summary Queue (background processing to avoid nginx timeout)
        services.AddSingleton<SummaryQueueService>();
        services.AddHostedService<BackgroundSummaryWorker>();

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
        services.AddScoped<AskHandler>();
        services.AddScoped<RecallHandler>();
        services.AddScoped<FactCheckHandler>();

        // Controllers with Telegram.Bot JSON support (snake_case)
        services.AddControllers()
            .AddJsonOptions(options =>
            {
                options.JsonSerializerOptions.PropertyNamingPolicy = new SnakeCaseNamingPolicy();
            });

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
