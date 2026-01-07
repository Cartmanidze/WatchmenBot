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
/// E2E tests for /ask command.
/// Tests the full pipeline: question → search → context → LLM → response.
/// </summary>
[Collection("Database")]
public class AskHandlerE2ETests(DatabaseFixture dbFixture)
{
    private readonly TestConfiguration _testConfig = new();

    #region Handler Tests (no API keys required)

    [Fact]
    public async Task HandleAsync_EmptyQuestion_SendsHelpText()
    {
        // Arrange
        var (handler, _, sentMessages) = CreateAskHandler();
        var message = CreateTestMessage(chatId: 12345L, userId: 67890L, text: "/ask");

        // Act
        await handler.HandleAsync(message, CancellationToken.None);

        // Assert
        Assert.Single(sentMessages);
        Assert.Contains("Вопрос по истории чата", sentMessages[0].Text);
        Assert.Contains("/ask", sentMessages[0].Text);
    }

    #endregion

    #region E2E Tests (require API keys)

    [Fact(Skip = "Requires API keys (OpenRouter + Jina)")]
    public async Task ProcessAsync_PersonalQuestion_ReturnsRelevantAnswer()
    {
        if (!_testConfig.HasOpenRouterKey) return;

        // Arrange
        const long chatId = 123456L;
        const long userId = 789L;
        await SeedPersonalMessagesAsync(chatId, userId, "testuser");

        var (service, botMock, sentMessages) = CreateProcessingService();
        var queueItem = CreateQueueItem(chatId, userId, "testuser", "какие языки программирования я использую?");

        // Act
        var result = await service.ProcessAsync(queueItem, CancellationToken.None);

        // Assert
        Assert.True(result.ResponseSent);
        Assert.NotEmpty(sentMessages);
        Assert.True(sentMessages.Last().Text.Length > 10);
        botMock.Verify(b => b.SendRequest(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact(Skip = "Requires API keys (OpenRouter + Jina)")]
    public async Task ProcessAsync_NoResults_SendsNotFoundMessage()
    {
        if (!_testConfig.HasOpenRouterKey) return;

        // Arrange - empty database, no seeding
        const long chatId = 123458L;
        const long userId = 791L;

        var (service, _, sentMessages) = CreateProcessingService();
        var queueItem = CreateQueueItem(chatId, userId, null, "completely random question with no matches");

        // Act
        await service.ProcessAsync(queueItem, CancellationToken.None);

        // Assert
        Assert.NotEmpty(sentMessages);
        Assert.Contains("не нашёл", sentMessages.Last().Text.ToLower());
    }

    [Fact(Skip = "Requires API keys (OpenRouter + Jina)")]
    public async Task ProcessAsync_BotDirectedQuestion_FindsRelevantAnswer()
    {
        if (!_testConfig.HasOpenRouterKey) return;

        // Arrange
        const long chatId = 123459L;
        const long userId = 792L;
        await SeedBotPurposeMessageAsync(chatId, userId, "testuser2");

        var (service, botMock, sentMessages) = CreateProcessingService();
        var queueItem = CreateQueueItem(chatId, userId, "testuser2", "для чего ты создан?");

        // Act
        var result = await service.ProcessAsync(queueItem, CancellationToken.None);

        // Assert
        Assert.True(result.ResponseSent);
        var response = sentMessages.Last().Text.ToLower();
        Assert.True(
            response.Contains("созда") || response.Contains("обрабатыва") ||
            response.Contains("вопрос") || response.Contains("цель"),
            $"Response should mention bot's purpose. Got: {response[..Math.Min(200, response.Length)]}");
    }

    /// <summary>
    /// Tests semantic gap: "цель существования" should find "создан чтобы обрабатывать".
    /// This verifies that expanded candidate pool allows cross-encoder to find relevant results.
    /// </summary>
    [Fact(Skip = "Requires API keys (OpenRouter + Jina)")]
    public async Task ProcessAsync_SemanticGapQuestion_FindsBotPurposeAmongNoise()
    {
        if (!_testConfig.HasOpenRouterKey) return;

        // Arrange - bot purpose message + noise messages about "life"
        const long chatId = 123460L;
        const long userId = 793L;
        await SeedBotPurposeWithNoiseAsync(chatId, userId, "testuser3");

        var (service, botMock, sentMessages) = CreateProcessingService();
        var queueItem = CreateQueueItem(chatId, userId, "testuser3",
            "ты разочарован из-за своей глупой цели существования?");

        // Act
        var result = await service.ProcessAsync(queueItem, CancellationToken.None);

        // Assert
        Assert.True(result.ResponseSent);
        Assert.NotEmpty(sentMessages);

        var response = sentMessages.Last().Text.ToLower();
        var hasBotRelevance = response.Contains("созда") || response.Contains("вопрос") ||
                              response.Contains("бот") || response.Contains("обрабат") ||
                              response.Contains("тупы");

        Assert.True(hasBotRelevance,
            $"Response should reference bot purpose, not just life philosophy. Got: {response[..Math.Min(200, response.Length)]}");

        botMock.Verify(b => b.SendRequest(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    #endregion

    #region Factory Methods

    private (AskHandler handler, Mock<ITelegramBotClient> botMock, List<SendMessageRequest> sentMessages) CreateAskHandler()
    {
        var (botMock, sentMessages) = CreateTelegramBotMock();
        var askQueue = new AskQueueService(dbFixture.ConnectionFactory!, NullLogger<AskQueueService>.Instance);
        var handler = new AskHandler(botMock.Object, askQueue, NullLogger<AskHandler>.Instance);
        return (handler, botMock, sentMessages);
    }

    private (AskProcessingService service, Mock<ITelegramBotClient> botMock, List<SendMessageRequest> sentMessages) CreateProcessingService()
    {
        var (botMock, sentMessages) = CreateTelegramBotMock();
        var connectionFactory = dbFixture.ConnectionFactory!;

        var llmRouter = CreateLlmRouter();
        var embeddingClient = CreateEmbeddingClient();

        // Memory services
        var genderDetection = new GenderDetectionService(NullLogger<GenderDetectionService>.Instance);
        var profileManagement = new ProfileManagementService(connectionFactory, genderDetection, NullLogger<ProfileManagementService>.Instance);
        var conversationMemory = new ConversationMemoryService(connectionFactory, NullLogger<ConversationMemoryService>.Instance);
        var llmExtraction = new LlmExtractionService(llmRouter, NullLogger<LlmExtractionService>.Instance);
        var relationshipService = new RelationshipService(connectionFactory, NullLogger<RelationshipService>.Instance);
        var memoryContextBuilder = new MemoryContextBuilder(profileManagement, conversationMemory, relationshipService);
        var memoryService = new LlmMemoryService(profileManagement, conversationMemory, llmExtraction, memoryContextBuilder, NullLogger<LlmMemoryService>.Instance);

        // Embedding services
        var questionGenerator = new QuestionGenerationService(llmRouter, NullLogger<QuestionGenerationService>.Instance);
        var embeddingStorage = new EmbeddingStorageService(embeddingClient, connectionFactory, questionGenerator, NullLogger<EmbeddingStorageService>.Instance);
        var contextWindowService = new ContextWindowService(connectionFactory, NullLogger<ContextWindowService>.Instance);
        var confidenceEvaluator = new SearchConfidenceEvaluator();
        var personalSearch = new PersonalSearchService(connectionFactory, embeddingClient, confidenceEvaluator, NullLogger<PersonalSearchService>.Instance);
        var embeddingService = new EmbeddingService(embeddingClient, connectionFactory, NullLogger<EmbeddingService>.Instance, embeddingStorage, personalSearch, contextWindowService);
        var contextEmbeddingService = new ContextEmbeddingService(embeddingClient, connectionFactory, NullLogger<ContextEmbeddingService>.Instance);

        // Cohere reranker (disabled in tests - empty API key)
        var cohereReranker = new CohereRerankService(new HttpClient(), apiKey: "", model: "rerank-v4.0-pro", NullLogger<CohereRerankService>.Instance);

        // Search services
        var ragFusionService = new RagFusionService(embeddingService, cohereReranker, NullLogger<RagFusionService>.Instance);
        var userAliasService = new UserAliasService(connectionFactory, NullLogger<UserAliasService>.Instance);
        var nicknameResolver = new NicknameResolverService(connectionFactory, userAliasService, llmRouter, NullLogger<NicknameResolverService>.Instance);
        var searchStrategy = new SearchStrategyService(embeddingService, contextEmbeddingService, ragFusionService, nicknameResolver, cohereReranker, NullLogger<SearchStrategyService>.Instance);
        var contextBuilder = new ContextBuilderService(embeddingService, NullLogger<ContextBuilderService>.Instance);

        // Settings
        var promptSettings = new PromptSettingsStore(connectionFactory, NullLogger<PromptSettingsStore>.Instance);
        var chatSettings = new ChatSettingsStore(connectionFactory, NullLogger<ChatSettingsStore>.Instance);
        var answerGenerator = new AnswerGeneratorService(llmRouter, promptSettings, chatSettings, NullLogger<AnswerGeneratorService>.Instance);

        // Ask services
        var intentClassifier = new IntentClassifier(llmRouter, NullLogger<IntentClassifier>.Instance);
        var debugCollector = new DebugReportCollector();
        var adminSettings = new AdminSettingsStore(connectionFactory, _testConfig.Configuration, NullLogger<AdminSettingsStore>.Instance);
        var debugService = new DebugService(botMock.Object, adminSettings, NullLogger<DebugService>.Instance);
        var confidenceGate = new ConfidenceGateService(botMock.Object, contextBuilder, debugCollector, debugService);

        var processingService = new AskProcessingService(
            botMock.Object, memoryService, debugService, searchStrategy, answerGenerator,
            intentClassifier, nicknameResolver, debugCollector, confidenceGate,
            NullLogger<AskProcessingService>.Instance);

        return (processingService, botMock, sentMessages);
    }

    private (Mock<ITelegramBotClient> botMock, List<SendMessageRequest> sentMessages) CreateTelegramBotMock()
    {
        var sentMessages = new List<SendMessageRequest>();
        var botMock = new Mock<ITelegramBotClient>();

        botMock.Setup(b => b.SendRequest(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SendMessageRequest req, CancellationToken _) =>
            {
                sentMessages.Add(req);
                return CreateTestMessage(chatId: req.ChatId.Identifier!.Value, userId: 0, text: req.Text);
            });

        return (botMock, sentMessages);
    }

    private LlmRouter CreateLlmRouter()
    {
        try { return _testConfig.CreateLlmRouter(); }
        catch (InvalidOperationException) { return new LlmRouter(NullLogger<LlmRouter>.Instance); }
    }

    private EmbeddingClient CreateEmbeddingClient()
    {
        try { return _testConfig.CreateEmbeddingClient(); }
        catch (InvalidOperationException)
        {
            return new EmbeddingClient(new HttpClient(), "fake-key", "", "", 1024, EmbeddingProvider.OpenAI, NullLogger<EmbeddingClient>.Instance);
        }
    }

    private static AskQueueItem CreateQueueItem(long chatId, long userId, string? username, string question) => new()
    {
        ChatId = chatId,
        ReplyToMessageId = 1001,
        Question = question,
        Command = "ask",
        AskerId = userId,
        AskerName = username ?? "Test",
        AskerUsername = username
    };

    private static Message CreateTestMessage(long chatId, long userId, string text, string? username = null) => new()
    {
        Chat = new Chat { Id = chatId },
        From = new User { Id = userId, Username = username, FirstName = "Test" },
        Text = text,
        Date = DateTime.UtcNow
    };

    #endregion

    #region Seed Methods

    private async Task SeedPersonalMessagesAsync(long chatId, long userId, string username)
    {
        using var connection = await dbFixture.ConnectionFactory!.CreateConnectionAsync();

        var messages = new[]
        {
            (Id: 1, Text: "Я работаю программистом уже 5 лет"),
            (Id: 2, Text: "Обожаю Python и TypeScript"),
            (Id: 3, Text: "Хочу изучить Rust в этом году")
        };

        await InsertMessagesAsync(connection, chatId, userId, messages);
        await InsertEmbeddingsAsync(connection, chatId, messages, username);
        await InsertUserProfileAsync(connection, chatId, userId, username, "Test User", @"[""Работает программистом 5 лет""]");
    }

    private async Task SeedBotPurposeMessageAsync(long chatId, long userId, string username)
    {
        using var connection = await dbFixture.ConnectionFactory!.CreateConnectionAsync();

        var messages = new[] { (Id: 100, Text: "ты создан чтобы обрабатывать самые тупые вопросы от меня") };

        await InsertMessagesAsync(connection, chatId, userId, messages);
        await InsertEmbeddingsAsync(connection, chatId, messages, username);
        await InsertUserProfileAsync(connection, chatId, userId, username, "Test User 2", "[]");
    }

    private async Task SeedBotPurposeWithNoiseAsync(long chatId, long userId, string username)
    {
        using var connection = await dbFixture.ConnectionFactory!.CreateConnectionAsync();

        var messages = new[]
        {
            (Id: 100, Text: "ты создан чтобы обрабатывать самые тупые вопросы от меня"),  // Target
            (Id: 101, Text: "ты такой наивный конечно"),                                    // Noise
            (Id: 102, Text: "ебанутая жизнь"),                                              // Noise
            (Id: 103, Text: "думаю щас жалеет"),                                            // Noise
            (Id: 104, Text: "Работать до смерти"),                                          // Noise
            (Id: 105, Text: "все стали такими достигаторами"),                              // Noise
            (Id: 106, Text: "Такова жизнь"),                                                // Noise
        };

        await InsertMessagesAsync(connection, chatId, userId, messages);
        await InsertEmbeddingsAsync(connection, chatId, messages, username);
        await InsertUserProfileAsync(connection, chatId, userId, username, "Gleb Bezrukov", "[]");
    }

    private static async Task InsertMessagesAsync(System.Data.IDbConnection connection, long chatId, long userId, (int Id, string Text)[] messages)
    {
        foreach (var msg in messages)
        {
            await connection.ExecuteAsync(
                "INSERT INTO messages (message_id, chat_id, from_user_id, text, date_utc) VALUES (@MsgId, @ChatId, @UserId, @Text, NOW() - INTERVAL '1 day')",
                new { ChatId = chatId, UserId = userId, MsgId = msg.Id, Text = msg.Text });
        }
    }

    private async Task InsertEmbeddingsAsync(System.Data.IDbConnection connection, long chatId, (int Id, string Text)[] messages, string username)
    {
        var embeddingClient = _testConfig.CreateEmbeddingClient();
        var texts = messages.Select(m => m.Text).ToArray();
        var embeddings = await embeddingClient.GetEmbeddingsAsync(texts, EmbeddingTask.RetrievalPassage, lateChunking: false, CancellationToken.None);
        var metadataJson = $"{{\"Username\": \"{username}\"}}";

        for (int i = 0; i < messages.Length; i++)
        {
            var vectorStr = string.Join(",", embeddings[i]);
            await connection.ExecuteAsync(
                $"INSERT INTO message_embeddings (chat_id, message_id, chunk_index, chunk_text, embedding, metadata) VALUES (@ChatId, @MessageId, 0, @ChunkText, '[{vectorStr}]'::vector, @Metadata::jsonb)",
                new { ChatId = chatId, MessageId = messages[i].Id, ChunkText = messages[i].Text, Metadata = metadataJson });
        }
    }

    private static async Task InsertUserProfileAsync(System.Data.IDbConnection connection, long chatId, long userId, string username, string displayName, string factsJson)
    {
        await connection.ExecuteAsync(
            "INSERT INTO user_profiles (user_id, chat_id, display_name, username, facts, interaction_count) VALUES (@UserId, @ChatId, @DisplayName, @Username, @Facts::jsonb, 1)",
            new { UserId = userId, ChatId = chatId, DisplayName = displayName, Username = username, Facts = factsJson });

        await connection.ExecuteAsync(
            "INSERT INTO user_aliases (chat_id, user_id, alias, alias_type, usage_count) VALUES (@ChatId, @UserId, @Username, 'username', 1), (@ChatId, @UserId, @DisplayName, 'display_name', 1)",
            new { ChatId = chatId, UserId = userId, Username = username, DisplayName = displayName });
    }

    #endregion
}
