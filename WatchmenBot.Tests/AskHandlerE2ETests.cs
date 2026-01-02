using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Telegram.Bot;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;
using WatchmenBot.Features.Search;
using WatchmenBot.Features.Search.Services;
using WatchmenBot.Features.Llm.Services;
using WatchmenBot.Features.Admin.Services;
using WatchmenBot.Infrastructure.Settings;
using WatchmenBot.Features.Memory.Services;
using WatchmenBot.Tests.Fixtures;
using Xunit;

namespace WatchmenBot.Tests;

/// <summary>
/// End-to-end tests for /ask command
/// Tests the full flow: message → search → context → LLM → response
/// </summary>
[Collection("Database")]
public class AskHandlerE2ETests(DatabaseFixture dbFixture)
{
    private readonly TestConfiguration _testConfig = new();

    [Fact(Skip = "Requires API keys and Docker")]
    public async Task HandleAsync_PersonalQuestion_ReturnsAnswer()
    {
        // Skip if no API keys
        if (!_testConfig.HasOpenRouterKey)
        {
            return;
        }

        // Arrange - Setup test data
        var chatId = 123456L;
        var userId = 789L;
        var username = "testuser";
        await SeedTestDataAsync(chatId, userId, username);

        // Arrange - Create handler with all dependencies
        var (handler, botMock, sentMessages) = CreateAskHandler();

        var message = CreateTestMessage(
            messageId: 1001,
            chatId: chatId,
            userId: userId,
            username: username,
            firstName: "Test",
            text: "/ask что я говорил про работу?");

        // Act
        await handler.HandleAsync(message, CancellationToken.None);

        // Assert
        Assert.NotEmpty(sentMessages);
        var response = sentMessages.Last();
        Assert.Contains("программист", response.Text.ToLower(), StringComparison.OrdinalIgnoreCase);

        // Verify message was sent
        botMock.Verify(b => b.SendRequest(
            It.IsAny<SendMessageRequest>(),
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact(Skip = "Requires API keys and Docker")]
    public async Task HandleAsync_GeneralQuestion_SearchesContext()
    {
        // Skip if no API keys
        if (!_testConfig.HasOpenRouterKey)
        {
            return;
        }

        // Arrange
        var chatId = 123457L;
        var userId = 790L;
        await SeedGeneralChatDataAsync(chatId, userId);

        var (handler, _, sentMessages) = CreateAskHandler();

        var message = CreateTestMessage(
            messageId: 2001,
            chatId: chatId,
            userId: userId,
            username: "user2",
            firstName: "User",
            text: "/ask о чём обсуждали Python?");

        // Act
        await handler.HandleAsync(message, CancellationToken.None);

        // Assert
        Assert.NotEmpty(sentMessages);
        var response = sentMessages.Last();
        Assert.NotNull(response.Text);
        Assert.True(response.Text.Length > 0);
    }

    [Fact]
    public async Task HandleAsync_EmptyQuestion_SendsHelpText()
    {
        // Arrange
        var (handler, _, sentMessages) = CreateAskHandler();

        var message = CreateTestMessage(
            messageId: 3001,
            chatId: 12345L,
            userId: 67890L,
            username: null,
            firstName: "Test",
            text: "/ask");

        // Act
        await handler.HandleAsync(message, CancellationToken.None);

        // Assert
        Assert.NotEmpty(sentMessages);
        var response = sentMessages.Last();
        Assert.Contains("Вопрос по истории чата", response.Text);
        Assert.Contains("/ask", response.Text);
    }

    [Fact(Skip = "Requires API keys and Docker")]
    public async Task HandleAsync_NoResults_SendsNoFoundMessage()
    {
        // Skip if no API keys
        if (!_testConfig.HasOpenRouterKey)
        {
            return;
        }

        // Arrange
        const long chatId = 123458L;
        const long userId = 791L;
        // Don't seed any data

        var (handler, _, sentMessages) = CreateAskHandler();

        var message = CreateTestMessage(
            messageId: 4001,
            chatId: chatId,
            userId: userId,
            username: null,
            firstName: "Test",
            text: "/ask completely random question that has no matches");

        // Act
        await handler.HandleAsync(message, CancellationToken.None);

        // Assert
        Assert.NotEmpty(sentMessages);
        var response = sentMessages.Last();
        Assert.Contains("не нашёл", response.Text.ToLower());
    }

    /// <summary>
    /// Create AskHandler with all dependencies (real and mocked)
    /// </summary>
    private (AskHandler handler, Mock<ITelegramBotClient> botMock, List<SendMessageRequest> sentMessages) CreateAskHandler()
    {
        // Mock Telegram Bot
        var sentMessages = new List<SendMessageRequest>();
        var botMock = new Mock<ITelegramBotClient>();

        var localMessages = sentMessages; // Capture for lambda
        botMock.Setup(b => b.SendRequest(
                It.IsAny<SendMessageRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((SendMessageRequest req, CancellationToken ct) =>
            {
                localMessages.Add(req);
                return CreateTestMessage(
                    messageId: 9999,
                    chatId: req.ChatId.Identifier!.Value,
                    userId: 0,
                    username: null,
                    firstName: null,
                    text: req.Text);
            });

        // Real services
        var connectionFactory = dbFixture.ConnectionFactory!;

        // Try to create real services, but use mocks if API keys are missing
        LlmRouter llmRouter;
        EmbeddingClient embeddingClient;

        try
        {
            llmRouter = _testConfig.CreateLlmRouter();
        }
        catch (InvalidOperationException)
        {
            // No API key - create mock LlmRouter
            llmRouter = new LlmRouter(NullLogger<LlmRouter>.Instance);
        }

        try
        {
            embeddingClient = _testConfig.CreateEmbeddingClient();
        }
        catch (InvalidOperationException)
        {
            // No API key - create fake embedding client
            var httpClient = new HttpClient();
            embeddingClient = new EmbeddingClient(
                httpClient,
                "fake-key",
                "",
                "",
                1024,
                EmbeddingProvider.OpenAI,
                NullLogger<EmbeddingClient>.Instance);
        }

        // Memory services
        var profileManagement = new ProfileManagementService(
            connectionFactory,
            NullLogger<ProfileManagementService>.Instance);

        var conversationMemory = new ConversationMemoryService(
            connectionFactory,
            NullLogger<ConversationMemoryService>.Instance);

        var llmExtraction = new LlmExtractionService(
            llmRouter,
            NullLogger<LlmExtractionService>.Instance);

        var memoryContextBuilder = new MemoryContextBuilder(
            profileManagement,
            conversationMemory);

        var memoryService = new LlmMemoryService(
            profileManagement,
            conversationMemory,
            llmExtraction,
            memoryContextBuilder,
            NullLogger<LlmMemoryService>.Instance);

        // Embedding services
        var embeddingStorage = new EmbeddingStorageService(
            embeddingClient,
            connectionFactory,
            NullLogger<EmbeddingStorageService>.Instance);

        var personalSearch = new PersonalSearchService(
            connectionFactory,
            embeddingClient,
            NullLogger<PersonalSearchService>.Instance);

        var contextWindowService = new ContextWindowService(
            connectionFactory,
            NullLogger<ContextWindowService>.Instance);

        var confidenceEvaluator = new SearchConfidenceEvaluator();

        var embeddingService = new EmbeddingService(
            embeddingClient,
            connectionFactory,
            NullLogger<EmbeddingService>.Instance,
            embeddingStorage,
            personalSearch,
            contextWindowService,
            confidenceEvaluator);

        var contextEmbeddingService = new ContextEmbeddingService(
            embeddingClient,
            connectionFactory,
            NullLogger<ContextEmbeddingService>.Instance);

        // Search services
        var searchStrategy = new SearchStrategyService(
            embeddingService,
            contextEmbeddingService,
            NullLogger<SearchStrategyService>.Instance);

        var contextBuilder = new ContextBuilderService(
            embeddingService,
            NullLogger<ContextBuilderService>.Instance);

        var promptSettings = new PromptSettingsStore(
            connectionFactory,
            NullLogger<PromptSettingsStore>.Instance);

        var answerGenerator = new AnswerGeneratorService(
            llmRouter,
            promptSettings,
            NullLogger<AnswerGeneratorService>.Instance);

        // AskHandler helper services
        var personalDetector = new PersonalQuestionDetector();
        var debugCollector = new DebugReportCollector();

        var adminSettings = new AdminSettingsStore(
            connectionFactory,
            _testConfig.Configuration,
            NullLogger<AdminSettingsStore>.Instance);

        var debugService = new DebugService(
            botMock.Object,
            adminSettings,
            NullLogger<DebugService>.Instance);

        var confidenceGate = new ConfidenceGateService(
            botMock.Object,
            contextBuilder,
            debugCollector,
            debugService);

        // Create AskHandler
        var handler = new AskHandler(
            botMock.Object,
            memoryService,
            debugService,
            searchStrategy,
            answerGenerator,
            personalDetector,
            debugCollector,
            confidenceGate,
            NullLogger<AskHandler>.Instance);

        return (handler, botMock, sentMessages);
    }

    /// <summary>
    /// Seed test data for personal questions
    /// </summary>
    private async Task SeedTestDataAsync(long chatId, long userId, string username)
    {
        using var connection = await dbFixture.ConnectionFactory!.CreateConnectionAsync();

        // Insert test messages
        await connection.ExecuteAsync(
            """
            INSERT INTO messages (message_id, chat_id, from_user_id, text, date_utc)
            VALUES
                (@MsgId1, @ChatId, @UserId, 'Я работаю программистом уже 5 лет', NOW() - INTERVAL '1 day'),
                (@MsgId2, @ChatId, @UserId, 'Обожаю Python и TypeScript', NOW() - INTERVAL '12 hours'),
                (@MsgId3, @ChatId, @UserId, 'Хочу изучить Rust в этом году', NOW() - INTERVAL '6 hours')
            """,
            new { ChatId = chatId, UserId = userId, MsgId1 = 1, MsgId2 = 2, MsgId3 = 3 });

        // Insert test embeddings (fake vectors for testing)
        var fakeVector = string.Join(",", Enumerable.Repeat("0.1", 1024));

        var metadataJson = $"{{\"Username\": \"{username}\"}}";

        await connection.ExecuteAsync(
            $"""
            INSERT INTO message_embeddings (chat_id, message_id, chunk_index, chunk_text, embedding, metadata)
            VALUES
                (@ChatId, 1, 0, 'Я работаю программистом уже 5 лет', '[{fakeVector}]'::vector, @Metadata::jsonb),
                (@ChatId, 2, 0, 'Обожаю Python и TypeScript', '[{fakeVector}]'::vector, @Metadata::jsonb),
                (@ChatId, 3, 0, 'Хочу изучить Rust в этом году', '[{fakeVector}]'::vector, @Metadata::jsonb)
            """,
            new { ChatId = chatId, Metadata = metadataJson });

        // Insert user profile
        await connection.ExecuteAsync(
            """
            INSERT INTO user_profiles (user_id, chat_id, display_name, username, facts, interaction_count)
            VALUES (@UserId, @ChatId, 'Test User', @Username, '["Работает программистом 5 лет"]'::jsonb, 3)
            """,
            new { UserId = userId, ChatId = chatId, Username = username });
    }

    private async Task SeedGeneralChatDataAsync(long chatId, long userId)
    {
        using var connection = await dbFixture.ConnectionFactory!.CreateConnectionAsync();

        await connection.ExecuteAsync(
            """
            INSERT INTO messages (message_id, chat_id, from_user_id, text, date_utc)
            VALUES
                (@MsgId1, @ChatId, @UserId, 'Python отличный язык для анализа данных', NOW() - INTERVAL '2 hours'),
                (@MsgId2, @ChatId, @UserId, 'А ещё у него классный синтаксис', NOW() - INTERVAL '1 hour')
            """,
            new { ChatId = chatId, UserId = userId, MsgId1 = 10, MsgId2 = 11 });

        var fakeVector = string.Join(",", Enumerable.Repeat("0.1", 1024));

        await connection.ExecuteAsync(
            $"""
            INSERT INTO context_embeddings (chat_id, center_message_id, context_text, embedding)
            VALUES
                (@ChatId, 10, 'Python отличный язык для анализа данных. А ещё у него классный синтаксис', '[{fakeVector}]'::vector)
            """,
            new { ChatId = chatId });
    }

    /// <summary>
    /// Create test message (MessageId is not set since it's readonly and not needed for tests)
    /// </summary>
    private static Message CreateTestMessage(
        int messageId, // Kept for API compatibility but not used
        long chatId,
        long userId,
        string? username,
        string? firstName,
        string text)
    {
        return new Message
        {
            Chat = new Chat { Id = chatId },
            From = new User
            {
                Id = userId,
                Username = username,
                FirstName = firstName ?? "Test"
            },
            Text = text,
            Date = DateTime.UtcNow
        };
    }
}
