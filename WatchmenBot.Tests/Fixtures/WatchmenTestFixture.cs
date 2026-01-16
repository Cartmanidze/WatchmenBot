using Dapper;
using Hangfire;
using Hangfire.States;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Telegram.Bot;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using WatchmenBot.Features.Admin.Services;
using WatchmenBot.Features.Llm.Services;
using WatchmenBot.Features.Memory.Services;
using WatchmenBot.Features.Messages;
using WatchmenBot.Features.Messages.Services;
using WatchmenBot.Features.Onboarding;
using WatchmenBot.Features.Profile.Services;
using WatchmenBot.Features.Search;
using WatchmenBot.Features.Search.Services;
using WatchmenBot.Features.Summary;
using WatchmenBot.Features.Summary.Services;
using WatchmenBot.Features.Webhook;
using WatchmenBot.Infrastructure.Database;
using WatchmenBot.Infrastructure.Settings;
using WatchmenBot.Tests.Builders;
using Xunit;

namespace WatchmenBot.Tests.Fixtures;

/// <summary>
/// Main fixture for E2E tests.
/// Creates full DI container with real services and real database (via Testcontainers).
/// Mocks only ITelegramBotClient (to capture messages) and IBackgroundJobClient (Hangfire).
/// </summary>
public class WatchmenTestFixture : IAsyncLifetime
{
    public IServiceProvider Services { get; private set; } = null!;
    public DatabaseFixture DbFixture { get; }
    public TestConfiguration TestConfig { get; }

    // Captured messages for assertions
    public List<SendMessageRequest> SentMessages { get; } = [];
    public List<EditMessageTextRequest> EditedMessages { get; } = [];
    public List<SendChatActionRequest> ChatActions { get; } = [];
    public List<DeleteMessageRequest> DeletedMessages { get; } = [];

    // Mocks for verification
    public Mock<ITelegramBotClient> BotMock { get; private set; } = null!;
    public Mock<IBackgroundJobClient> JobClientMock { get; private set; } = null!;

    public WatchmenTestFixture(DatabaseFixture dbFixture)
    {
        DbFixture = dbFixture;
        TestConfig = new TestConfiguration();
    }

    public async Task InitializeAsync()
    {
        BotMock = CreateTelegramBotMock();
        JobClientMock = CreateHangfireMock();

        var services = new ServiceCollection();

        // Logging (debug level for tests)
        services.AddLogging(b => b
            .AddDebug()
            .SetMinimumLevel(LogLevel.Debug));

        // Configuration
        var config = BuildTestConfiguration();
        services.AddSingleton<IConfiguration>(config);

        // Database (real Testcontainers PostgreSQL)
        services.AddSingleton(DbFixture.ConnectionFactory!);

        // Telegram Bot (mocked to capture sent messages)
        services.AddSingleton<ITelegramBotClient>(BotMock.Object);

        // Hangfire (mocked to verify job scheduling)
        services.AddSingleton<IBackgroundJobClient>(JobClientMock.Object);

        // LLM Router (real with API keys)
        if (TestConfig.HasOpenRouterKey)
        {
            services.AddSingleton(_ => TestConfig.CreateLlmRouter());
        }
        else
        {
            // Create a dummy router that will fail gracefully
            services.AddSingleton(_ => new LlmRouter(NullLogger<LlmRouter>.Instance));
        }

        // Embedding Client (real with API keys)
        if (TestConfig.HasOpenAiKey)
        {
            services.AddSingleton(_ => TestConfig.CreateEmbeddingClient());
        }
        else
        {
            // Tests requiring embeddings will skip
            services.AddSingleton(_ => new EmbeddingClient(
                new HttpClient(),
                string.Empty,
                string.Empty,
                string.Empty,
                0,
                EmbeddingProvider.OpenAI,
                NullLogger<EmbeddingClient>.Instance));
        }

        // Register all application services
        RegisterApplicationServices(services);

        Services = services.BuildServiceProvider();

        // Clean database before tests
        await CleanDatabaseAsync();
    }

    private void RegisterApplicationServices(IServiceCollection services)
    {
        // Memory Services
        services.AddSingleton<GenderDetectionService>();
        services.AddScoped<ProfileManagementService>();
        services.AddScoped<ConversationMemoryService>();
        services.AddScoped<LlmExtractionService>();
        services.AddScoped<RelationshipService>();
        services.AddScoped<RelationshipExtractionService>();
        services.AddScoped<MemoryContextBuilder>();
        services.AddScoped<LlmMemoryService>();

        // Embedding Services
        services.AddScoped<QuestionGenerationService>();
        services.AddScoped<EmbeddingStorageService>();
        services.AddScoped<ContextWindowService>();
        services.AddScoped<SearchConfidenceEvaluator>();
        services.AddScoped<PersonalSearchService>();
        services.AddScoped<EmbeddingService>();
        services.AddScoped<ContextEmbeddingService>();

        // Reranker (gracefully handles missing API key)
        services.AddSingleton(sp =>
        {
            var apiKey = TestConfig.Configuration["Reranker:ApiKey"] ?? "";
            return new CohereRerankService(
                new HttpClient(),
                apiKey,
                "rerank-v4.0-pro",
                NullLogger<CohereRerankService>.Instance);
        });

        // Search Services
        services.AddScoped<RagFusionService>();
        services.AddScoped<UserAliasService>();
        services.AddScoped<NicknameResolverService>();
        services.AddScoped<NicknameExtractionService>();
        services.AddScoped<SearchStrategyService>();
        services.AddScoped<ContextBuilderService>();

        // Settings Stores
        services.AddSingleton<AdminSettingsStore>();
        services.AddSingleton<PromptSettingsStore>();
        services.AddSingleton<ChatSettingsStore>();
        services.AddSingleton<DebugService>();
        services.AddSingleton<LogCollector>();

        // Ask Pipeline Services
        services.AddScoped<AnswerGeneratorService>();
        services.AddScoped<IntentClassifier>();
        services.AddScoped<DebugReportCollector>();
        services.AddScoped<ConfidenceGateService>();
        services.AddScoped<AskProcessingService>();

        // Command Handlers
        services.AddScoped<AskHandler>();
        services.AddScoped<StartCommandHandler>();
        services.AddScoped<FactCheckHandler>();
        services.AddScoped<GenerateSummaryHandler>();
        services.AddScoped<SaveMessageHandler>();
        services.AddScoped<ProcessTelegramUpdateHandler>();

        // Message Store
        services.AddScoped<MessageStore>();

        // Summary Services
        services.AddScoped<TopicExtractor>();
        services.AddScoped<SummaryContextBuilder>();
        services.AddScoped<SummaryStageExecutor>();
        services.AddScoped<ThreadDetector>();
        services.AddScoped<EventDetector>();
        services.AddScoped<QuoteMiner>();
        services.AddScoped<SmartSummaryService>();
        services.AddScoped<SummaryProcessingService>();

        // Truth/Fact-check Services
        services.AddScoped<TruthProcessingService>();

        // Queue Services (for direct testing without Hangfire)
        services.AddScoped<AskQueueService>();
        services.AddScoped<SummaryQueueService>();
        services.AddScoped<TruthQueueService>();

        // Profile Queue Service (required by SaveMessageHandler)
        services.AddSingleton<ProfileQueueService>();
    }

    private Mock<ITelegramBotClient> CreateTelegramBotMock()
    {
        var mock = new Mock<ITelegramBotClient>();

        // Capture SendMessageRequest
        mock.Setup(b => b.SendRequest(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SendMessageRequest req, CancellationToken _) =>
            {
                SentMessages.Add(req);
                return new MessageBuilder()
                    .InChat(req.ChatId.Identifier!.Value)
                    .WithText(req.Text)
                    .WithMessageId(1000 + SentMessages.Count)
                    .Build();
            });

        // Capture EditMessageTextRequest
        mock.Setup(b => b.SendRequest(It.IsAny<EditMessageTextRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((EditMessageTextRequest req, CancellationToken _) =>
            {
                EditedMessages.Add(req);
                return new MessageBuilder()
                    .InChat(req.ChatId.Identifier!.Value)
                    .WithText(req.Text)
                    .WithMessageId(req.MessageId)
                    .Build();
            });

        // Capture SendChatActionRequest
        mock.Setup(b => b.SendRequest(It.IsAny<SendChatActionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SendChatActionRequest req, CancellationToken _) =>
            {
                ChatActions.Add(req);
                return true;
            });

        // Capture DeleteMessageRequest
        mock.Setup(b => b.SendRequest(It.IsAny<DeleteMessageRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DeleteMessageRequest req, CancellationToken _) =>
            {
                DeletedMessages.Add(req);
                return true;
            });

        return mock;
    }

    private Mock<IBackgroundJobClient> CreateHangfireMock()
    {
        var mock = new Mock<IBackgroundJobClient>();

        // Return a fake job ID for any scheduled job
        mock.Setup(j => j.Create(It.IsAny<Hangfire.Common.Job>(), It.IsAny<IState>()))
            .Returns("test-job-id");

        return mock;
    }

    private IConfiguration BuildTestConfiguration()
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Admin:UserId"] = "12345",
                ["Admin:Username"] = "testadmin",
                ["Telegram:BotUsername"] = "TestBot",
                ["Telegram:WebhookSecret"] = "test-secret",
                ["ConnectionStrings:Default"] = DbFixture.ConnectionString
            })
            .Build();
    }

    /// <summary>
    /// Clean all test data from database.
    /// Call this between tests for isolation.
    /// </summary>
    public async Task CleanDatabaseAsync()
    {
        using var connection = await DbFixture.ConnectionFactory!.CreateConnectionAsync();
        await connection.ExecuteAsync(@"
            TRUNCATE TABLE
                conversation_memory,
                user_relationships,
                user_facts,
                user_aliases,
                user_profiles,
                context_embeddings,
                message_embeddings,
                message_queue,
                messages,
                chats
            CASCADE
        ");

        ClearCapturedMessages();
    }

    /// <summary>
    /// Clear captured messages without touching the database.
    /// </summary>
    public void ClearCapturedMessages()
    {
        SentMessages.Clear();
        EditedMessages.Clear();
        ChatActions.Clear();
        DeletedMessages.Clear();
    }

    /// <summary>
    /// Get a service from the DI container.
    /// </summary>
    public T GetService<T>() where T : notnull =>
        Services.GetRequiredService<T>();

    /// <summary>
    /// Create a new scope for scoped services.
    /// </summary>
    public IServiceScope CreateScope() =>
        Services.CreateScope();

    /// <summary>
    /// Get the last sent message text, or null if none sent.
    /// </summary>
    public string? LastSentMessageText =>
        SentMessages.LastOrDefault()?.Text;

    /// <summary>
    /// Check if any message was sent containing the specified text.
    /// </summary>
    public bool MessageContains(string text) =>
        SentMessages.Any(m => m.Text.Contains(text, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Check if a typing indicator was sent.
    /// </summary>
    public bool TypingIndicatorSent =>
        ChatActions.Any(a => a.Action == ChatAction.Typing);

    public Task DisposeAsync()
    {
        TestConfig.Dispose();
        return Task.CompletedTask;
    }
}

/// <summary>
/// Collection definition that combines Database and WatchmenTest fixtures.
/// Use [Collection("E2E")] on test classes.
/// </summary>
[CollectionDefinition("E2E")]
public class E2ETestCollection :
    ICollectionFixture<DatabaseFixture>;
