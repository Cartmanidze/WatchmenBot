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

        // Embedding Services (refactored architecture)
        services.AddScoped<WatchmenBot.Services.Embeddings.EmbeddingStorageService>();
        services.AddScoped<WatchmenBot.Services.Embeddings.PersonalSearchService>();
        services.AddScoped<WatchmenBot.Services.Embeddings.ContextWindowService>();
        services.AddScoped<EmbeddingService>(); // Core search service (delegates to specialized services)

        // Context Embedding Service (sliding window embeddings for conversation context)
        services.AddScoped<ContextEmbeddingService>();

        // Embedding Indexing Infrastructure (refactored pipeline)
        services.AddSingleton<WatchmenBot.Services.Indexing.IndexingMetrics>();
        services.AddScoped<WatchmenBot.Services.Indexing.IndexingOptions>(sp =>
            WatchmenBot.Services.Indexing.IndexingOptions.FromConfiguration(configuration));
        services.AddScoped<WatchmenBot.Services.Indexing.BatchProcessor>();
        services.AddScoped<WatchmenBot.Services.Indexing.IEmbeddingHandler, WatchmenBot.Services.Indexing.MessageEmbeddingHandler>();
        services.AddScoped<WatchmenBot.Services.Indexing.IEmbeddingHandler, WatchmenBot.Services.Indexing.ContextEmbeddingHandler>();
        services.AddScoped<WatchmenBot.Services.Indexing.EmbeddingOrchestrator>();

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

        // Profile System Infrastructure (refactored pipeline)
        services.AddSingleton<WatchmenBot.Services.Profile.ProfileMetrics>();
        services.AddScoped<WatchmenBot.Services.Profile.ProfileOptions>(sp =>
            WatchmenBot.Services.Profile.ProfileOptions.FromConfiguration(configuration));
        services.AddScoped<WatchmenBot.Services.Profile.FactExtractionHandler>();
        services.AddScoped<WatchmenBot.Services.Profile.ProfileGenerationHandler>();
        services.AddScoped<WatchmenBot.Services.Profile.ProfileOrchestrator>();

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

        // Admin Command Pattern Infrastructure
        services.AddSingleton<WatchmenBot.Features.Admin.Commands.AdminCommandRegistry>(sp =>
        {
            var registry = new WatchmenBot.Features.Admin.Commands.AdminCommandRegistry();

            // Monitoring commands
            registry.Register<WatchmenBot.Features.Admin.Commands.StatusCommand>("status");
            registry.Register<WatchmenBot.Features.Admin.Commands.ReportCommand>("report");
            registry.Register<WatchmenBot.Features.Admin.Commands.ChatsCommand>("chats");
            registry.Register<WatchmenBot.Features.Admin.Commands.IndexingCommand>("indexing");
            registry.Register<WatchmenBot.Features.Admin.Commands.HelpCommand>("help");

            // Debug commands
            registry.Register<WatchmenBot.Features.Admin.Commands.DebugCommand>("debug");

            // Settings commands
            registry.Register<WatchmenBot.Features.Admin.Commands.SetSummaryTimeCommand>("set_summary_time");
            registry.Register<WatchmenBot.Features.Admin.Commands.SetReportTimeCommand>("set_report_time");
            registry.Register<WatchmenBot.Features.Admin.Commands.SetTimezoneCommand>("set_timezone");

            // LLM commands
            registry.Register<WatchmenBot.Features.Admin.Commands.LlmListCommand>("llm");
            registry.Register<WatchmenBot.Features.Admin.Commands.LlmTestCommand>("llm_test");
            registry.Register<WatchmenBot.Features.Admin.Commands.LlmSetCommand>("llm_set");

            // LLM toggle commands (llm_on/llm_off) - using custom factory for bool parameter
            registry.Register("llm_on", sp => new WatchmenBot.Features.Admin.Commands.LlmToggleCommand(
                sp.GetRequiredService<ITelegramBotClient>(),
                sp.GetRequiredService<WatchmenBot.Services.Llm.LlmRouter>(),
                true,
                sp.GetRequiredService<ILogger<WatchmenBot.Features.Admin.Commands.LlmToggleCommand>>()));

            registry.Register("llm_off", sp => new WatchmenBot.Features.Admin.Commands.LlmToggleCommand(
                sp.GetRequiredService<ITelegramBotClient>(),
                sp.GetRequiredService<WatchmenBot.Services.Llm.LlmRouter>(),
                false,
                sp.GetRequiredService<ILogger<WatchmenBot.Features.Admin.Commands.LlmToggleCommand>>()));

            // Import/Export commands
            registry.Register<WatchmenBot.Features.Admin.Commands.ImportCommand>("import");

            // Prompt management commands
            registry.Register<WatchmenBot.Features.Admin.Commands.PromptsCommand>("prompts");
            registry.Register<WatchmenBot.Features.Admin.Commands.PromptCommand>("prompt");
            registry.Register<WatchmenBot.Features.Admin.Commands.PromptResetCommand>("prompt_reset");
            registry.Register<WatchmenBot.Features.Admin.Commands.PromptTagCommand>("prompt_tag");

            // User management commands
            registry.Register<WatchmenBot.Features.Admin.Commands.NamesCommand>("names");
            registry.Register<WatchmenBot.Features.Admin.Commands.RenameCommand>("rename");

            // Embedding management commands
            registry.Register<WatchmenBot.Features.Admin.Commands.ReindexCommand>("reindex");
            registry.Register<WatchmenBot.Features.Admin.Commands.ContextCommand>("context");
            registry.Register<WatchmenBot.Features.Admin.Commands.ContextReindexCommand>("context_reindex");

            return registry;
        });

        // Admin Commands (using Command Pattern)
        services.AddScoped<WatchmenBot.Features.Admin.Commands.StatusCommand>();
        services.AddScoped<WatchmenBot.Features.Admin.Commands.ReportCommand>();
        services.AddScoped<WatchmenBot.Features.Admin.Commands.ChatsCommand>();
        services.AddScoped<WatchmenBot.Features.Admin.Commands.IndexingCommand>();
        services.AddScoped<WatchmenBot.Features.Admin.Commands.HelpCommand>();
        services.AddScoped<WatchmenBot.Features.Admin.Commands.DebugCommand>();
        services.AddScoped<WatchmenBot.Features.Admin.Commands.SetSummaryTimeCommand>();
        services.AddScoped<WatchmenBot.Features.Admin.Commands.SetReportTimeCommand>();
        services.AddScoped<WatchmenBot.Features.Admin.Commands.SetTimezoneCommand>();
        services.AddScoped<WatchmenBot.Features.Admin.Commands.LlmListCommand>();
        services.AddScoped<WatchmenBot.Features.Admin.Commands.LlmTestCommand>();
        services.AddScoped<WatchmenBot.Features.Admin.Commands.LlmSetCommand>();
        services.AddScoped<WatchmenBot.Features.Admin.Commands.LlmToggleCommand>();
        services.AddScoped<WatchmenBot.Features.Admin.Commands.ImportCommand>();
        services.AddScoped<WatchmenBot.Features.Admin.Commands.PromptsCommand>();
        services.AddScoped<WatchmenBot.Features.Admin.Commands.PromptCommand>();
        services.AddScoped<WatchmenBot.Features.Admin.Commands.PromptResetCommand>();
        services.AddScoped<WatchmenBot.Features.Admin.Commands.PromptTagCommand>();
        services.AddScoped<WatchmenBot.Features.Admin.Commands.NamesCommand>();
        services.AddScoped<WatchmenBot.Features.Admin.Commands.RenameCommand>();
        services.AddScoped<WatchmenBot.Features.Admin.Commands.ReindexCommand>();
        services.AddScoped<WatchmenBot.Features.Admin.Commands.ContextCommand>();
        services.AddScoped<WatchmenBot.Features.Admin.Commands.ContextReindexCommand>();

        // Feature Handlers
        services.AddScoped<ProcessTelegramUpdateHandler>();
        services.AddScoped<SaveMessageHandler>();
        services.AddScoped<GenerateSummaryHandler>();
        services.AddScoped<AdminCommandHandler>();
        services.AddScoped<SetWebhookHandler>();
        services.AddScoped<DeleteWebhookHandler>();
        services.AddScoped<GetWebhookInfoHandler>();

        // Search Services (extracted from AskHandler refactoring)
        services.AddScoped<SearchStrategyService>();
        services.AddScoped<ContextBuilderService>();
        services.AddScoped<AnswerGeneratorService>();
        services.AddScoped<PersonalQuestionDetector>();
        services.AddScoped<DebugReportCollector>();
        services.AddScoped<ConfidenceGateService>();

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
