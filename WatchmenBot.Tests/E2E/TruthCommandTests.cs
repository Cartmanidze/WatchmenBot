using WatchmenBot.Features.Search;
using WatchmenBot.Features.Search.Services;
using WatchmenBot.Tests.Builders;
using WatchmenBot.Tests.Fixtures;
using Xunit;

namespace WatchmenBot.Tests.E2E;

/// <summary>
/// E2E tests for /truth fact-checking command.
/// Tests handler and processing pipeline.
/// </summary>
public class TruthCommandTests : E2ETestBase
{
    public TruthCommandTests(DatabaseFixture dbFixture) : base(dbFixture)
    {
    }

    #region Handler Tests (no API keys required)

    [Fact]
    public async Task HandleAsync_ValidCommand_EnqueuesJobAndSendsAck()
    {
        // Arrange
        var handler = GetService<FactCheckHandler>();
        var message = new MessageBuilder()
            .InGroupChat(-100123L)
            .From(67890L, "testuser", "Test")
            .WithText("/truth")
            .Build();

        // Act
        await handler.HandleAsync(message, CancellationToken.None);

        // Assert
        AssertJobScheduled();
        AssertMessageSent("Проверяю последние 5 сообщений"); // Default is 5
    }

    [Fact]
    public async Task HandleAsync_WithCustomCount_UsesSpecifiedCount()
    {
        // Arrange
        var handler = GetService<FactCheckHandler>();
        var message = new MessageBuilder()
            .InGroupChat(-100123L)
            .From(67890L, "testuser", "Test")
            .WithText("/truth 10")
            .Build();

        // Act
        await handler.HandleAsync(message, CancellationToken.None);

        // Assert
        AssertJobScheduled();
        AssertMessageSent("Проверяю последние 10 сообщений");
    }

    [Fact]
    public async Task HandleAsync_CountTooLarge_CapsAt15()
    {
        // Arrange
        var handler = GetService<FactCheckHandler>();
        var message = new MessageBuilder()
            .InGroupChat(-100123L)
            .From(67890L, "testuser", "Test")
            .WithText("/truth 100")
            .Build();

        // Act
        await handler.HandleAsync(message, CancellationToken.None);

        // Assert
        AssertJobScheduled();
        AssertMessageSent("Проверяю последние 15 сообщений"); // Capped at max
    }

    [Theory]
    [InlineData("/truth 0")]
    [InlineData("/truth -5")]
    [InlineData("/truth invalid")]
    public async Task HandleAsync_InvalidCount_UsesDefault5(string command)
    {
        // Arrange
        var handler = GetService<FactCheckHandler>();
        var message = new MessageBuilder()
            .InGroupChat(-100123L)
            .WithText(command)
            .Build();

        // Act
        await handler.HandleAsync(message, CancellationToken.None);

        // Assert
        AssertJobScheduled();
        AssertMessageSent("5 сообщений");
    }

    [Fact]
    public async Task HandleAsync_RepliesToOriginalMessage()
    {
        // Arrange
        var handler = GetService<FactCheckHandler>();
        var message = new MessageBuilder()
            .InGroupChat(-100123L)
            .From(67890L, "testuser", "Test")
            .WithText("/truth")
            .WithMessageId(42)
            .Build();

        // Act
        await handler.HandleAsync(message, CancellationToken.None);

        // Assert
        Assert.Single(SentMessages);
        var request = SentMessages[0];
        Assert.NotNull(request.ReplyParameters);
        Assert.Equal(42, request.ReplyParameters.MessageId);
    }

    #endregion

    #region Processing Tests (require API keys)

    [SkippableFact]
    public async Task ProcessAsync_WithClaimMessages_GeneratesFactCheck()
    {
        // Skip if no API keys
        SkipIfNoLlmKey();

        // Arrange
        const long chatId = 300001L;
        const long userId = 67890L;

        // Seed messages with a claim that can be fact-checked
        await Seeder.SeedRecentMessagesAsync(chatId, 1,
            (userId, "testuser", "Эйфелева башня высотой 300 метров"),
            (userId, "testuser", "Она была построена в 1889 году"),
            (userId, "testuser", "Находится в Берлине"));

        var service = GetService<TruthProcessingService>();
        var queueItem = TruthQueueItemBuilder.Create()
            .ForChat(chatId)
            .WithMessageCount(3)
            .RequestedBy("testuser")
            .Build();

        // Act
        await service.ProcessAsync(queueItem, CancellationToken.None);

        // Assert - should send some response about facts
        Assert.NotEmpty(SentMessages);
        var response = LastMessageText!.ToLower();

        // Should contain some analysis
        Assert.True(response.Length > 50, "Fact-check should be substantial");
    }

    [SkippableFact]
    public async Task ProcessAsync_NoMessages_ReportsNoMessages()
    {
        // Skip if no API keys
        SkipIfNoLlmKey();

        // Arrange - empty chat
        const long chatId = 300002L;

        var service = GetService<TruthProcessingService>();
        var queueItem = TruthQueueItemBuilder.Create()
            .ForChat(chatId)
            .WithMessageCount(5)
            .Build();

        // Act
        await service.ProcessAsync(queueItem, CancellationToken.None);

        // Assert
        Assert.NotEmpty(SentMessages);
    }

    #endregion
}
