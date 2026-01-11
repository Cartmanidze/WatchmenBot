using Microsoft.OpenApi.Models;
using Telegram.Bot;
using WatchmenBot.Policies;
using WatchmenBot.Features.Admin;
using WatchmenBot.Features.Admin.Commands;
using WatchmenBot.Features.Messages;
using WatchmenBot.Features.Messages.Services;
using WatchmenBot.Features.Search;
using WatchmenBot.Features.Search.Services;
using WatchmenBot.Features.Summary;
using WatchmenBot.Features.Summary.Services;
using WatchmenBot.Features.Webhook;
using WatchmenBot.Features.Onboarding;
using WatchmenBot.Infrastructure.Database;
using WatchmenBot.Infrastructure.Settings;
using WatchmenBot.Features.Admin.Services;
using WatchmenBot.Features.Webhook.Services;
using WatchmenBot.Features.Indexing.Services;
using WatchmenBot.Features.Llm.Services;
using WatchmenBot.Features.Memory.Services;
using WatchmenBot.Features.Profile.Services;

namespace WatchmenBot.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddWatchmenBotServices(this IServiceCollection services, IConfiguration configuration)
    {
        // Telegram Bot Client
        services.AddSingleton<ITelegramBotClient>(_ =>
        {
            var token = configuration["Telegram:BotToken"] ?? string.Empty;
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException("Telegram:BotToken is required");
            }
            return new TelegramBotClient(token);
        });

        // Database
        services.AddSingleton<IDbConnectionFactory>(_ =>
        {
            var connectionString = configuration.GetConnectionString("Default") ??
                                 configuration["Database:ConnectionString"] ??
                                 throw new InvalidOperationException("Database connection string is required");
            return new PostgreSqlConnectionFactory(connectionString);
        });

        services.AddScoped<MessageStore>();
        services.AddScoped<UserAliasService>();
        services.AddScoped<NicknameExtractionService>();
        services.AddScoped<RelationshipService>();
        services.AddScoped<RelationshipExtractionService>();
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
                var dimensions = configuration.GetValue("Embeddings:Dimensions", 0);
                var providerStr = configuration["Embeddings:Provider"] ?? "openai";
                var logger = serviceProvider.GetRequiredService<ILogger<EmbeddingClient>>();

                // Parse provider
                var provider = providerStr.ToLowerInvariant() switch
                {
                    "jina" => EmbeddingProvider.Jina,
                    "huggingface" or "hf" => EmbeddingProvider.HuggingFace,
                    _ => EmbeddingProvider.OpenAI
                };

                // Embeddings are optional - if no API key, RAG will be disabled
                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    logger.LogWarning("Embeddings:ApiKey not configured. RAG features will be disabled.");
                }

                return new EmbeddingClient(httpClient, apiKey, baseUrl, model, dimensions, provider, logger);
            });

        // Q→A Semantic Bridge (question generation for better search)
        services.AddScoped<QuestionGenerationService>();

        // Embedding Services (refactored architecture)
        services.AddScoped<EmbeddingStorageService>();
        services.AddScoped<PersonalSearchService>();
        services.AddScoped<ContextWindowService>();
        services.AddScoped<SearchConfidenceEvaluator>(); // Extracted from EmbeddingService
        services.AddScoped<EmbeddingService>(); // Core search service (delegates to specialized services)

        // Context Embedding Service (sliding window embeddings for conversation context)
        services.AddScoped<ContextEmbeddingService>();

        // Embedding Indexing Infrastructure (refactored pipeline)
        services.AddSingleton<IndexingMetrics>();
        services.AddScoped<IndexingOptions>(_ =>
            IndexingOptions.FromConfiguration(configuration));
        services.AddScoped<BatchProcessor>();
        services.AddScoped<IEmbeddingHandler, MessageEmbeddingHandler>();
        services.AddScoped<IEmbeddingHandler, ContextEmbeddingHandler>();
        services.AddScoped<EmbeddingOrchestrator>();

        // RAG Fusion Service (structural variations + vector/keyword search + reranking)
        services.AddScoped<RagFusionService>();

        // Rerank Service (LLM-based reranking)
        services.AddScoped<RerankService>();

        // Cohere Rerank Service (cross-encoder reranking)
        services.AddHttpClient<CohereRerankService>()
            .ConfigureHttpClient(client =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);
            })
            .AddTypedClient<CohereRerankService>((httpClient, serviceProvider) =>
            {
                var apiKey = configuration["Reranker:ApiKey"] ?? "";
                var model = configuration["Reranker:Model"] ?? "rerank-v4.0-pro";
                var enabled = configuration.GetValue("Reranker:Enabled", true);
                var logger = serviceProvider.GetRequiredService<ILogger<CohereRerankService>>();

                // If disabled or no API key, service will gracefully skip reranking
                if (!enabled)
                {
                    logger.LogInformation("[Rerank] Cohere reranker disabled in configuration");
                    apiKey = ""; // Force disabled
                }

                return new CohereRerankService(httpClient, apiKey, model, logger);
            });

        // LLM Memory Services (refactored architecture)
        services.AddSingleton<GenderDetectionService>(); // Stateless, can be singleton
        services.AddScoped<ProfileManagementService>();
        services.AddScoped<ConversationMemoryService>();
        services.AddScoped<LlmExtractionService>();
        services.AddScoped<MemoryContextBuilder>();
        services.AddScoped<LlmMemoryService>(); // Facade that delegates to specialized services

        // Smart Summary Services (refactored architecture)
        services.AddScoped<TopicExtractor>(); // Extracted from SmartSummaryService
        services.AddScoped<SummaryContextBuilder>(); // Extracted from SmartSummaryService
        services.AddScoped<SummaryStageExecutor>(); // Extracted from SmartSummaryService
        services.AddScoped<ThreadDetector>(); // Enhanced: activity segmentation & reply chains
        services.AddScoped<EventDetector>(); // Enhanced: LLM event/decision extraction
        services.AddScoped<QuoteMiner>(); // Enhanced: LLM quote mining
        services.AddScoped<SmartSummaryService>(); // Orchestrator (delegates to specialized services)

        // Chat Import Services
        services.AddScoped<TelegramExportParser>();
        services.AddScoped<ChatImportService>();

        // Admin Services
        services.AddSingleton<LogCollector>();
        services.AddSingleton<AdminSettingsStore>();
        services.AddSingleton<PromptSettingsStore>();
        services.AddSingleton<ChatSettingsStore>();
        services.AddSingleton<DebugService>();

        // Usage tracking
        services.AddHttpClient<OpenRouterUsageService>();

        // Profile System Infrastructure (refactored pipeline)
        services.AddSingleton<ProfileMetrics>();
        services.AddScoped<ProfileOptions>(_ =>
            ProfileOptions.FromConfiguration(configuration));
        services.AddScoped<FactExtractionHandler>();
        services.AddScoped<ProfileGenerationHandler>();
        services.AddScoped<ProfileOrchestrator>();

        // Profile System Services
        services.AddSingleton<ProfileQueueService>();
        services.AddHostedService<ProfileWorkerService>();
        services.AddHostedService<ProfileGeneratorService>();

        // PostgreSQL LISTEN/NOTIFY for real-time queue notifications
        services.AddSingleton<PostgresNotificationService>();
        services.AddHostedService(sp => sp.GetRequiredService<PostgresNotificationService>());

        // Summary Queue (background processing to avoid nginx timeout)
        services.AddSingleton<SummaryQueueService>();
        services.AddHostedService<BackgroundSummaryWorker>();

        // Ask Queue (background processing to avoid Telegram webhook timeout)
        services.AddSingleton<AskQueueService>();
        services.AddHostedService<BackgroundAskWorker>();

        // Truth Queue (background processing for /truth fact-checking)
        services.AddSingleton<TruthQueueService>();
        services.AddHostedService<BackgroundTruthWorker>();

        // Background Services
        services.AddHostedService<DailySummaryService>();
        services.AddHostedService<BackgroundEmbeddingService>();
        services.AddHostedService<TelegramPollingService>();
        services.AddSingleton<DailyLogReportService>();
        services.AddHostedService(sp => sp.GetRequiredService<DailyLogReportService>());

        // Admin Command Pattern Infrastructure
        services.AddSingleton<AdminCommandRegistry>(_ =>
        {
            var registry = new AdminCommandRegistry();

            // Monitoring commands
            registry.Register<StatusCommand>("status");
            registry.Register<ReportCommand>("report");
            registry.Register<ChatsCommand>("chats");
            registry.Register<IndexingCommand>("indexing");
            registry.Register<HelpCommand>("help");

            // Debug commands
            registry.Register<DebugCommand>("debug");

            // Settings commands
            registry.Register<SetSummaryTimeCommand>("set_summary_time");
            registry.Register<SetReportTimeCommand>("set_report_time");
            registry.Register<SetTimezoneCommand>("set_timezone");
            registry.Register<ModeCommand>("mode");

            // LLM commands
            registry.Register<LlmListCommand>("llm");
            registry.Register<LlmTestCommand>("llm_test");
            registry.Register<LlmSetCommand>("llm_set");

            // LLM toggle commands (llm_on/llm_off) - using custom factory for bool parameter
            registry.Register("llm_on", sp => new LlmToggleCommand(
                sp.GetRequiredService<ITelegramBotClient>(),
                sp.GetRequiredService<LlmRouter>(),
                true,
                sp.GetRequiredService<ILogger<LlmToggleCommand>>()));

            registry.Register("llm_off", sp => new LlmToggleCommand(
                sp.GetRequiredService<ITelegramBotClient>(),
                sp.GetRequiredService<LlmRouter>(),
                false,
                sp.GetRequiredService<ILogger<LlmToggleCommand>>()));

            // Import/Export commands
            registry.Register<ImportCommand>("import");

            // Prompt management commands
            registry.Register<PromptsCommand>("prompts");
            registry.Register<PromptCommand>("prompt");
            registry.Register<PromptResetCommand>("prompt_reset");
            registry.Register<PromptTagCommand>("prompt_tag");

            // User management commands
            registry.Register<NamesCommand>("names");
            registry.Register<RenameCommand>("rename");

            // Embedding management commands
            registry.Register<ReindexCommand>("reindex");
            registry.Register<ContextCommand>("context");
            registry.Register<ContextReindexCommand>("context_reindex");

            return registry;
        });

        // Admin Commands (using Command Pattern)
        services.AddScoped<StatusCommand>();
        services.AddScoped<ReportCommand>();
        services.AddScoped<ChatsCommand>();
        services.AddScoped<IndexingCommand>();
        services.AddScoped<HelpCommand>();
        services.AddScoped<DebugCommand>();
        services.AddScoped<SetSummaryTimeCommand>();
        services.AddScoped<SetReportTimeCommand>();
        services.AddScoped<SetTimezoneCommand>();
        services.AddScoped<ModeCommand>();
        services.AddScoped<LlmListCommand>();
        services.AddScoped<LlmTestCommand>();
        services.AddScoped<LlmSetCommand>();
        services.AddScoped<LlmToggleCommand>();
        services.AddScoped<ImportCommand>();
        services.AddScoped<PromptsCommand>();
        services.AddScoped<PromptCommand>();
        services.AddScoped<PromptResetCommand>();
        services.AddScoped<PromptTagCommand>();
        services.AddScoped<NamesCommand>();
        services.AddScoped<RenameCommand>();
        services.AddScoped<ReindexCommand>();
        services.AddScoped<ContextCommand>();
        services.AddScoped<ContextReindexCommand>();

        // Feature Handlers
        services.AddScoped<ProcessTelegramUpdateHandler>();
        services.AddScoped<SaveMessageHandler>();
        services.AddScoped<GenerateSummaryHandler>();
        services.AddScoped<AdminCommandHandler>();
        services.AddScoped<StartCommandHandler>();
        services.AddScoped<SetWebhookHandler>();
        services.AddScoped<DeleteWebhookHandler>();
        services.AddScoped<GetWebhookInfoHandler>();

        // Search Services (extracted from AskHandler refactoring)
        services.AddScoped<SearchStrategyService>();
        services.AddScoped<ContextBuilderService>();
        services.AddScoped<AnswerGeneratorService>();
        services.AddScoped<IntentClassifier>(); // LLM-based intent classification (replaces PersonalQuestionDetector)
        services.AddScoped<NicknameResolverService>(); // Resolves nicknames to actual usernames
        services.AddScoped<DebugReportCollector>();
        services.AddScoped<ConfidenceGateService>();

        // Ask Processing Service (core /ask logic, used by BackgroundAskWorker and tests)
        services.AddScoped<AskProcessingService>();

        // Search Handlers (embedding-based)
        services.AddScoped<AskHandler>();
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
