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
using WatchmenBot.Features.Messages.Services;
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

    [Fact(Skip = "Requires API keys (OpenRouter + Jina)")]
    public async Task HandleAsync_PersonalQuestion_ReturnsAnswer()
    {
        // Skip if no API keys
        if (!_testConfig.HasOpenRouterKey)
        {
            return;
        }

        // Arrange - Setup test data
        const long chatId = 123456L;
        const long userId = 789L;
        const string username = "testuser";
        await SeedTestDataAsync(chatId, userId, username);

        // Arrange - Create processing service with all dependencies
        var (service, botMock, sentMessages) = CreateAskProcessingService();

        var queueItem = new AskQueueItem
        {
            ChatId = chatId,
            ReplyToMessageId = 1001,
            Question = "какие языки программирования я использую?",  // More direct question matching embeddings
            Command = "ask",
            AskerId = userId,
            AskerName = "Test",
            AskerUsername = username
        };

        // Act
        var result = await service.ProcessAsync(queueItem, CancellationToken.None);

        // Assert - Verify E2E flow completed
        Assert.True(result.ResponseSent, "Response should be sent");
        Assert.NotEmpty(sentMessages);

        var response = sentMessages.Last();
        Assert.NotNull(response.Text);
        Assert.True(response.Text.Length > 10, "Response should contain meaningful text");

        // Verify message was sent through Telegram API
        botMock.Verify(b => b.SendRequest(
            It.IsAny<SendMessageRequest>(),
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);

        // Log result for debugging
        Console.WriteLine($"[TEST] Result: Success={result.Success}, Confidence={result.Confidence}, Elapsed={result.ElapsedSeconds:F1}s");
        Console.WriteLine($"[TEST] Response length: {response.Text.Length} chars");
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

        var (service, _, sentMessages) = CreateAskProcessingService();

        var queueItem = new AskQueueItem
        {
            ChatId = chatId,
            ReplyToMessageId = 2001,
            Question = "о чём обсуждали Python?",
            Command = "ask",
            AskerId = userId,
            AskerName = "User",
            AskerUsername = "user2"
        };

        // Act
        var result = await service.ProcessAsync(queueItem, CancellationToken.None);

        // Assert
        Assert.True(result.Success,
            $"Processing should succeed. Confidence: {result.Confidence}, ResponseSent: {result.ResponseSent}, Elapsed: {result.ElapsedSeconds:F1}s");
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

        var (service, _, sentMessages) = CreateAskProcessingService();

        var queueItem = new AskQueueItem
        {
            ChatId = chatId,
            ReplyToMessageId = 4001,
            Question = "completely random question that has no matches",
            Command = "ask",
            AskerId = userId,
            AskerName = "Test",
            AskerUsername = null
        };

        // Act
        var result = await service.ProcessAsync(queueItem, CancellationToken.None);

        // Assert - Confidence gate should send "not found" message
        Assert.NotEmpty(sentMessages);
        var response = sentMessages.Last();
        Assert.Contains("не нашёл", response.Text.ToLower());
    }

    /// <summary>
    /// Create AskHandler (for testing only enqueue logic and help text)
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

        // Simple handler for enqueue logic only
        var askQueue = new AskQueueService(dbFixture.ConnectionFactory!, NullLogger<AskQueueService>.Instance);
        var handler = new AskHandler(
            botMock.Object,
            askQueue,
            NullLogger<AskHandler>.Instance);

        return (handler, botMock, sentMessages);
    }

    /// <summary>
    /// Create AskProcessingService with all dependencies for E2E testing
    /// </summary>
    private (AskProcessingService service, Mock<ITelegramBotClient> botMock, List<SendMessageRequest> sentMessages) CreateAskProcessingService()
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
        var genderDetection = new GenderDetectionService(
            NullLogger<GenderDetectionService>.Instance);
        var profileManagement = new ProfileManagementService(
            connectionFactory,
            genderDetection,
            NullLogger<ProfileManagementService>.Instance);

        var conversationMemory = new ConversationMemoryService(
            connectionFactory,
            NullLogger<ConversationMemoryService>.Instance);

        var llmExtraction = new LlmExtractionService(
            llmRouter,
            NullLogger<LlmExtractionService>.Instance);

        var relationshipService = new RelationshipService(
            connectionFactory,
            NullLogger<RelationshipService>.Instance);

        var memoryContextBuilder = new MemoryContextBuilder(
            profileManagement,
            conversationMemory,
            relationshipService);

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

        var contextWindowService = new ContextWindowService(
            connectionFactory,
            NullLogger<ContextWindowService>.Instance);

        var confidenceEvaluator = new SearchConfidenceEvaluator();

        var personalSearch = new PersonalSearchService(
            connectionFactory,
            embeddingClient,
            confidenceEvaluator,
            NullLogger<PersonalSearchService>.Instance);

        var embeddingService = new EmbeddingService(
            embeddingClient,
            connectionFactory,
            NullLogger<EmbeddingService>.Instance,
            embeddingStorage,
            personalSearch,
            contextWindowService);

        var contextEmbeddingService = new ContextEmbeddingService(
            embeddingClient,
            connectionFactory,
            NullLogger<ContextEmbeddingService>.Instance);

        // Cohere reranker with empty API key (disabled in tests)
        var cohereReranker = new CohereRerankService(
            new HttpClient(),
            apiKey: "", // Disabled - will gracefully skip reranking
            model: "rerank-v4.0-pro",
            NullLogger<CohereRerankService>.Instance);

        // Search services (no LLM dependency for RAG Fusion now)
        var ragFusionService = new RagFusionService(
            embeddingService,
            cohereReranker,
            NullLogger<RagFusionService>.Instance);

        var userAliasService = new UserAliasService(
            connectionFactory,
            NullLogger<UserAliasService>.Instance);

        var nicknameResolver = new NicknameResolverService(
            connectionFactory,
            userAliasService,
            llmRouter,
            NullLogger<NicknameResolverService>.Instance);

        var searchStrategy = new SearchStrategyService(
            embeddingService,
            contextEmbeddingService,
            ragFusionService,
            nicknameResolver,
            cohereReranker,
            NullLogger<SearchStrategyService>.Instance);

        var contextBuilder = new ContextBuilderService(
            embeddingService,
            NullLogger<ContextBuilderService>.Instance);

        var promptSettings = new PromptSettingsStore(
            connectionFactory,
            NullLogger<PromptSettingsStore>.Instance);

        var chatSettings = new ChatSettingsStore(
            connectionFactory,
            NullLogger<ChatSettingsStore>.Instance);

        var answerGenerator = new AnswerGeneratorService(
            llmRouter,
            promptSettings,
            chatSettings,
            NullLogger<AnswerGeneratorService>.Instance);

        // AskHandler helper services
        var intentClassifier = new IntentClassifier(
            llmRouter,
            NullLogger<IntentClassifier>.Instance);
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

        // Create AskProcessingService (core processing logic)
        var processingService = new AskProcessingService(
            botMock.Object,
            memoryService,
            debugService,
            searchStrategy,
            answerGenerator,
            intentClassifier,
            nicknameResolver,
            debugCollector,
            confidenceGate,
            NullLogger<AskProcessingService>.Instance);

        return (processingService, botMock, sentMessages);
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

        // Create REAL embeddings using Jina API for accurate test results
        var embeddingClient = _testConfig.CreateEmbeddingClient();
        var texts = new[]
        {
            "Я работаю программистом уже 5 лет",
            "Обожаю Python и TypeScript",
            "Хочу изучить Rust в этом году"
        };

        var embeddings = await embeddingClient.GetEmbeddingsAsync(
            texts,
            EmbeddingTask.RetrievalPassage,
            lateChunking: false,
            CancellationToken.None);

        var metadataJson = $"{{\"Username\": \"{username}\"}}";

        // Insert test embeddings with real vectors
        for (int i = 0; i < texts.Length; i++)
        {
            var vectorStr = string.Join(",", embeddings[i]);
            await connection.ExecuteAsync(
                $"""
                INSERT INTO message_embeddings (chat_id, message_id, chunk_index, chunk_text, embedding, metadata)
                VALUES (@ChatId, @MessageId, 0, @ChunkText, '[{vectorStr}]'::vector, @Metadata::jsonb)
                """,
                new { ChatId = chatId, MessageId = i + 1, ChunkText = texts[i], Metadata = metadataJson });
        }

        // Verify embeddings were created
        var count = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM message_embeddings WHERE chat_id = @ChatId",
            new { ChatId = chatId });
        Console.WriteLine($"[TEST] Created {count} embeddings in test database");

        // Insert user profile
        await connection.ExecuteAsync(
            """
            INSERT INTO user_profiles (user_id, chat_id, display_name, username, facts, interaction_count)
            VALUES (@UserId, @ChatId, 'Test User', @Username, '["Работает программистом 5 лет"]'::jsonb, 3)
            """,
            new { UserId = userId, ChatId = chatId, Username = username });

        // Insert user aliases for nickname resolution
        await connection.ExecuteAsync(
            """
            INSERT INTO user_aliases (chat_id, user_id, alias, alias_type, usage_count)
            VALUES
                (@ChatId, @UserId, @Username, 'username', 1),
                (@ChatId, @UserId, 'Test User', 'display_name', 1),
                (@ChatId, @UserId, 'Test', 'display_name', 1)
            """,
            new { ChatId = chatId, UserId = userId, Username = username });
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

    [Fact(Skip = "Requires API keys (OpenRouter + Jina)")]
    public async Task HandleAsync_BotDirectedQuestions_ReturnsRelevantAnswers()
    {
        // Skip if no API keys
        if (!_testConfig.HasOpenRouterKey)
        {
            return;
        }

        // Arrange - Setup test data with bot purpose message
        const long chatId = 123459L;
        const long userId = 792L;
        const string username = "testuser2";
        await SeedBotPurposeDataAsync(chatId, userId, username);

        // Arrange - Create processing service
        var (service, botMock, sentMessages) = CreateAskProcessingService();

        // Act - First question: "для чего ты создан?"
        var queueItem1 = new AskQueueItem
        {
            ChatId = chatId,
            ReplyToMessageId = 1001,
            Question = "для чего ты создан?",
            Command = "ask",
            AskerId = userId,
            AskerName = "Test",
            AskerUsername = username
        };

        var result1 = await service.ProcessAsync(queueItem1, CancellationToken.None);

        // Assert - First question should get relevant answer
        Assert.True(result1.ResponseSent, "Response should be sent for first question");
        Assert.NotEmpty(sentMessages);

        var response1 = sentMessages.Last();
        Assert.NotNull(response1.Text);
        Assert.True(response1.Text.Length > 10, "First response should contain meaningful text");

        // Check that response is relevant to bot's purpose
        var lowerResponse1 = response1.Text.ToLower();
        var hasRelevantKeywords = lowerResponse1.Contains("созда") ||
                                   lowerResponse1.Contains("обрабатыва") ||
                                   lowerResponse1.Contains("вопрос") ||
                                   lowerResponse1.Contains("цель");
        Assert.True(hasRelevantKeywords, $"First response should mention bot's purpose. Response: {response1.Text}");
        
        // Act - Second question: "ты разочарован из-за своей глупой цели существования?"
        var queueItem2 = new AskQueueItem
        {
            ChatId = chatId,
            ReplyToMessageId = 1002,
            Question = "ты разочарован из-за своей глупой цели существования?",
            Command = "ask",
            AskerId = userId,
            AskerName = "Test",
            AskerUsername = username
        };

        sentMessages.Clear(); // Clear previous messages
        var result2 = await service.ProcessAsync(queueItem2, CancellationToken.None);

        // Assert - Second question should get philosophical answer
        Assert.True(result2.ResponseSent, "Response should be sent for second question");
        Assert.NotEmpty(sentMessages);

        var response2 = sentMessages.Last();
        Assert.NotNull(response2.Text);
        Assert.True(response2.Text.Length > 10, "Second response should contain meaningful text");

        Console.WriteLine($"[TEST] Second question result: Success={result2.Success}, Confidence={result2.Confidence}");
        Console.WriteLine($"[TEST] Second response: {response2.Text.Substring(0, Math.Min(200, response2.Text.Length))}...");

        // Verify messages were sent through Telegram API
        botMock.Verify(b => b.SendRequest(
            It.IsAny<SendMessageRequest>(),
            It.IsAny<CancellationToken>()), Times.AtLeast(2));
    }

    /// <summary>
    /// Seed test data for bot-directed questions (bot's purpose)
    /// </summary>
    private async Task SeedBotPurposeDataAsync(long chatId, long userId, string username)
    {
        using var connection = await dbFixture.ConnectionFactory!.CreateConnectionAsync();

        // Insert message about bot's purpose
        await connection.ExecuteAsync(
            """
            INSERT INTO messages (message_id, chat_id, from_user_id, text, date_utc)
            VALUES
                (@MsgId1, @ChatId, @UserId, 'ты создан чтобы обрабатывать самые тупые вопросы от меня', NOW() - INTERVAL '1 day')
            """,
            new { ChatId = chatId, UserId = userId, MsgId1 = 100 });

        // Create REAL embeddings using Jina API for accurate semantic search
        var embeddingClient = _testConfig.CreateEmbeddingClient();
        var text = "ты создан чтобы обрабатывать самые тупые вопросы от меня";

        var embeddings = await embeddingClient.GetEmbeddingsAsync(
            new[] { text },
            EmbeddingTask.RetrievalPassage,
            lateChunking: false,
            CancellationToken.None);

        var metadataJson = $"{{\"Username\": \"{username}\"}}";

        // Insert embedding with real vector
        var vectorStr = string.Join(",", embeddings[0]);
        await connection.ExecuteAsync(
            $"""
            INSERT INTO message_embeddings (chat_id, message_id, chunk_index, chunk_text, embedding, metadata)
            VALUES (@ChatId, @MessageId, 0, @ChunkText, '[{vectorStr}]'::vector, @Metadata::jsonb)
            """,
            new { ChatId = chatId, MessageId = 100, ChunkText = text, Metadata = metadataJson });

        // Verify embeddings were created
        var count = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM message_embeddings WHERE chat_id = @ChatId",
            new { ChatId = chatId });
        Console.WriteLine($"[TEST] Created {count} bot purpose embeddings in test database");

        // Insert user profile
        await connection.ExecuteAsync(
            """
            INSERT INTO user_profiles (user_id, chat_id, display_name, username, facts, interaction_count)
            VALUES (@UserId, @ChatId, 'Test User 2', @Username, '[]'::jsonb, 1)
            """,
            new { UserId = userId, ChatId = chatId, Username = username });

        // Insert user aliases
        await connection.ExecuteAsync(
            """
            INSERT INTO user_aliases (chat_id, user_id, alias, alias_type, usage_count)
            VALUES
                (@ChatId, @UserId, @Username, 'username', 1),
                (@ChatId, @UserId, 'Test User 2', 'display_name', 1),
                (@ChatId, @UserId, 'Test', 'display_name', 1)
            """,
            new { ChatId = chatId, UserId = userId, Username = username });
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
