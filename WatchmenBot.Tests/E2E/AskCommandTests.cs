using Dapper;
using WatchmenBot.Features.Search;
using WatchmenBot.Features.Search.Services;
using WatchmenBot.Tests.Builders;
using WatchmenBot.Tests.Fixtures;
using Xunit;

namespace WatchmenBot.Tests.E2E;

/// <summary>
/// E2E tests for /ask command pipeline.
/// Tests handler (no API keys) and full processing (requires API keys).
/// </summary>
public class AskCommandTests : E2ETestBase
{
    public AskCommandTests(DatabaseFixture dbFixture) : base(dbFixture)
    {
    }

    #region Handler Tests (no API keys required)

    [Fact]
    public async Task HandleAsync_EmptyQuestion_SendsHelpText()
    {
        // Arrange
        var handler = GetService<AskHandler>();
        var message = new MessageBuilder()
            .InGroupChat(-100123L)
            .From(67890L, "testuser", "Test")
            .WithText("/ask")
            .Build();

        // Act
        await handler.HandleAsync(message, CancellationToken.None);

        // Assert
        AssertMessageCount(1);
        AssertMessageSent("Вопрос по истории чата");
        AssertMessageSent("/ask");
    }

    [Theory]
    [InlineData("/ask")]
    [InlineData("/ask ")]
    [InlineData("/ask   ")]
    public async Task HandleAsync_BlankQuestion_SendsHelpText(string command)
    {
        // Arrange
        var handler = GetService<AskHandler>();
        var message = new MessageBuilder()
            .InGroupChat(-100123L)
            .WithText(command)
            .Build();

        // Act
        await handler.HandleAsync(message, CancellationToken.None);

        // Assert
        AssertMessageSent("Вопрос по истории чата");
        AssertNoJobScheduled();
    }

    [Fact]
    public async Task HandleAsync_ValidQuestion_EnqueuesJobAndSendsTyping()
    {
        // Arrange
        var handler = GetService<AskHandler>();
        var message = new MessageBuilder()
            .InGroupChat(-100123L)
            .From(67890L, "testuser", "Test")
            .WithText("/ask что обсуждали вчера?")
            .Build();

        // Act
        await handler.HandleAsync(message, CancellationToken.None);

        // Assert
        AssertJobScheduled();
        AssertTypingSent();
        AssertNoMessageSent(); // Only typing indicator, actual response comes from job
    }

    [Fact]
    public async Task HandleQuestionAsync_SmartCommand_EnqueuesJob()
    {
        // Arrange
        var handler = GetService<AskHandler>();
        var message = new MessageBuilder()
            .InGroupChat(-100123L)
            .From(67890L, "testuser", "Test")
            .WithText("/smart какая погода в Москве?")
            .Build();

        // Act
        await handler.HandleQuestionAsync(message, CancellationToken.None);

        // Assert
        AssertJobScheduled();
        AssertTypingSent();
    }

    [Fact]
    public async Task HandleQuestionAsync_EmptySmartQuestion_SendsSmartHelpText()
    {
        // Arrange
        var handler = GetService<AskHandler>();
        var message = new MessageBuilder()
            .InGroupChat(-100123L)
            .WithText("/smart")
            .Build();

        // Act
        await handler.HandleQuestionAsync(message, CancellationToken.None);

        // Assert
        AssertMessageSent("Умный поиск");
        AssertMessageSent("Perplexity");
        AssertNoJobScheduled();
    }

    #endregion

    #region Processing Tests (require API keys)

    [SkippableFact]
    public async Task ProcessAsync_PersonalQuestion_ReturnsRelevantAnswer()
    {
        // Skip if no API keys
        SkipIfNoApiKeys();

        // Arrange - use negative chatId like production groups
        const long chatId = -100001L;
        const long userId = 789L;

        // Seed messages in 3rd person style - better matches "who uses X?" questions
        await Seeder.SeedMessagesWithEmbeddingsAsync(chatId, userId, "testuser",
            "testuser использует Python для бэкенда",
            "testuser пишет фронтенд на TypeScript",
            "testuser планирует изучить Rust");
        await Seeder.SeedUserProfileAsync(chatId, userId, "testuser", "Test User",
            ["Программист, использует Python и TypeScript"]);

        // Verify data was seeded correctly
        var messageCount = await Seeder.GetMessageCountAsync(chatId);
        var embeddingCount = await Seeder.GetEmbeddingCountAsync(chatId);
        Assert.True(messageCount >= 3, $"Should have seeded 3 messages, got {messageCount}");
        Assert.True(embeddingCount >= 3, $"Should have seeded 3 embeddings, got {embeddingCount}");

        var service = GetService<AskProcessingService>();
        var queueItem = AskQueueItemBuilder.Create()
            .ForChat(chatId)
            // More general question without specific username to avoid personal search
            .WithQuestion("какие языки программирования используются в чате?")
            .FromUser(userId, "Test User", "testuser")
            .Build();

        // Act
        var result = await service.ProcessAsync(queueItem, CancellationToken.None);

        // Assert
        Assert.True(result.ResponseSent);
        Assert.NotEmpty(SentMessages);

        var response = LastMessageText!.ToLower();
        Assert.True(
            response.Contains("python") || response.Contains("typescript") || response.Contains("rust"),
            $"Response should mention programming languages. Got: {response[..Math.Min(200, response.Length)]}");
    }

    [SkippableFact]
    public async Task ProcessAsync_NoResults_SendsNotFoundMessage()
    {
        // Skip if no API keys
        SkipIfNoApiKeys();

        // Arrange - empty database, no seeding
        const long chatId = 100002L;

        var service = GetService<AskProcessingService>();
        var queueItem = AskQueueItemBuilder.Create()
            .ForChat(chatId)
            .WithQuestion("совершенно случайный вопрос без совпадений")
            .Build();

        // Act
        await service.ProcessAsync(queueItem, CancellationToken.None);

        // Assert
        Assert.NotEmpty(SentMessages);
        AssertMessageSent("не нашёл");
    }

    [SkippableFact]
    public async Task ProcessAsync_BotDirectedQuestion_FindsRelevantAnswer()
    {
        // Skip if no API keys
        SkipIfNoApiKeys();

        // Arrange
        const long chatId = 100003L;
        const long userId = 790L;

        await Seeder.SeedMessagesWithEmbeddingsAsync(chatId, userId, "testuser",
            "ты создан чтобы обрабатывать самые тупые вопросы от меня");
        await Seeder.SeedUserProfileAsync(chatId, userId, "testuser", "Test User");

        var service = GetService<AskProcessingService>();
        var queueItem = AskQueueItemBuilder.Create()
            .ForChat(chatId)
            .WithQuestion("для чего ты создан?")
            .FromUser(userId, "Test User", "testuser")
            .Build();

        // Act
        var result = await service.ProcessAsync(queueItem, CancellationToken.None);

        // Assert
        Assert.True(result.ResponseSent);
        var response = LastMessageText!.ToLower();

        Assert.True(
            response.Contains("созда") || response.Contains("обрабатыва") ||
            response.Contains("вопрос") || response.Contains("цель"),
            $"Response should mention bot's purpose. Got: {response[..Math.Min(200, response.Length)]}");
    }

    [SkippableFact]
    public async Task ProcessAsync_MultiUserConversation_FindsCorrectContext()
    {
        // Skip if no API keys
        SkipIfNoApiKeys();

        // Arrange - conversation between two users
        const long chatId = 100004L;
        const long user1 = 801L;
        const long user2 = 802L;

        await Seeder.SeedMessagesWithEmbeddingsAsync(chatId, user1, "alice",
            "Завтра едем на рыбалку",
            "Собираемся в 5 утра");
        await Seeder.SeedMessagesWithEmbeddingsAsync(chatId, user2, "bob",
            "Не забудь удочки",
            "Я возьму палатку");

        await Seeder.SeedUserProfileAsync(chatId, user1, "alice", "Alice");
        await Seeder.SeedUserProfileAsync(chatId, user2, "bob", "Bob");

        var service = GetService<AskProcessingService>();
        var queueItem = AskQueueItemBuilder.Create()
            .ForChat(chatId)
            .WithQuestion("во сколько собираемся на рыбалку?")
            .FromUser(user1, "Alice", "alice")
            .Build();

        // Act
        var result = await service.ProcessAsync(queueItem, CancellationToken.None);

        // Assert
        Assert.True(result.ResponseSent);
        var response = LastMessageText!.ToLower();

        Assert.True(
            response.Contains("5") || response.Contains("утра") || response.Contains("рыбалк"),
            $"Response should mention the time. Got: {response[..Math.Min(200, response.Length)]}");
    }

    #endregion
}
