using WatchmenBot.Features.Summary;
using WatchmenBot.Tests.Builders;
using WatchmenBot.Tests.Fixtures;
using Xunit;

namespace WatchmenBot.Tests.E2E;

/// <summary>
/// E2E tests for /summary command.
/// Tests summary generation with various message counts and time periods.
/// </summary>
public class SummaryCommandTests : E2ETestBase
{
    public SummaryCommandTests(DatabaseFixture dbFixture) : base(dbFixture)
    {
    }

    #region Handler Tests (no API keys required)

    [Fact]
    public async Task HandleAsync_NoMessages_ReportsNoMessages()
    {
        // Arrange - empty database
        var handler = GetService<GenerateSummaryHandler>();
        var message = new MessageBuilder()
            .InGroupChat(-100123L)
            .From(67890L, "testuser", "Test")
            .WithText("/summary")
            .Build();

        var request = new GenerateSummaryRequest { Message = message, Hours = 24 };

        // Act
        var response = await handler.HandleAsync(request, CancellationToken.None);

        // Assert
        Assert.True(response.IsSuccess);
        Assert.Equal(0, response.MessageCount);
        AssertMessageSent("сообщений не найдено");
    }

    [Theory]
    [InlineData("/summary", 24)]
    [InlineData("/summary 48", 48)]
    [InlineData("/summary 12", 12)]
    [InlineData("/summary 1", 1)]
    [InlineData("/summary 48h", 48)]
    [InlineData("/summary 2d", 48)]
    [InlineData("/summary 2д", 48)]
    [InlineData("/summary 12ч", 12)]
    public void ParseHoursFromCommand_ParsesCorrectly(string command, int expectedHours)
    {
        // Act
        var hours = GenerateSummaryHandler.ParseHoursFromCommand(command);

        // Assert
        Assert.Equal(expectedHours, hours);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/summary")]
    [InlineData("/summary invalid")]
    [InlineData("/summary -5")]
    [InlineData("/summary 0")]
    public void ParseHoursFromCommand_InvalidInput_ReturnsDefault24(string? command)
    {
        // Act
        var hours = GenerateSummaryHandler.ParseHoursFromCommand(command);

        // Assert
        Assert.Equal(24, hours);
    }

    [Theory]
    [InlineData("/summary 1000")] // Too large (max 720)
    [InlineData("/summary 50d")]  // 50 days = 1200 hours > 720
    public void ParseHoursFromCommand_TooLarge_ReturnsDefault24(string command)
    {
        // Act
        var hours = GenerateSummaryHandler.ParseHoursFromCommand(command);

        // Assert
        Assert.Equal(24, hours);
    }

    #endregion

    #region Summary Generation Tests (require API keys)

    [SkippableFact]
    public async Task HandleAsync_WithMessages_GeneratesSummary()
    {
        // Skip if no API keys
        SkipIfNoLlmKey();

        // Arrange
        const long chatId = 200001L;
        const long userId = 67890L;

        await Seeder.SeedRecentMessagesAsync(chatId, 12, // Within last 12 hours
            (userId, "testuser", "Привет всем!"),
            (userId, "testuser", "Обсудили новый проект"),
            (userId, "testuser", "Решили использовать .NET 9"),
            (userId, "testuser", "Дедлайн через неделю"));

        var handler = GetService<GenerateSummaryHandler>();
        var message = new MessageBuilder()
            .InGroupChat(chatId)
            .From(userId, "testuser", "Test")
            .WithText("/summary")
            .Build();

        var request = new GenerateSummaryRequest { Message = message, Hours = 24 };

        // Act
        var response = await handler.HandleAsync(request, CancellationToken.None);

        // Assert
        Assert.True(response.IsSuccess);
        Assert.Equal(4, response.MessageCount);
        Assert.NotEmpty(SentMessages);

        // Should have generated some summary text
        var summaryText = LastMessageText;
        Assert.NotNull(summaryText);
        Assert.True(summaryText.Length > 50, "Summary should be substantial");
    }

    [SkippableFact]
    public async Task HandleAsync_MultipleUsers_IncludesAllUsers()
    {
        // Skip if no API keys
        SkipIfNoLlmKey();

        // Arrange
        const long chatId = 200002L;
        const long user1 = 101L;
        const long user2 = 102L;
        const long user3 = 103L;

        await Seeder.SeedRecentMessagesAsync(chatId, 12,
            (user1, "alice", "Всем привет, как дела?"),
            (user2, "bob", "Отлично! Работаю над новым проектом"),
            (user3, "charlie", "О, интересно! Расскажи подробнее"),
            (user2, "bob", "Делаем Telegram бота с AI"));

        var handler = GetService<GenerateSummaryHandler>();
        var message = new MessageBuilder()
            .InGroupChat(chatId)
            .From(user1, "alice", "Alice")
            .WithText("/summary")
            .Build();

        var request = new GenerateSummaryRequest { Message = message, Hours = 24 };

        // Act
        var response = await handler.HandleAsync(request, CancellationToken.None);

        // Assert
        Assert.True(response.IsSuccess);
        Assert.Equal(4, response.MessageCount);
    }

    [SkippableFact]
    public async Task HandleAsync_CustomHours_UsesSpecifiedPeriod()
    {
        // Skip if no API keys
        SkipIfNoLlmKey();

        // Arrange
        const long chatId = 200003L;
        const long userId = 67890L;

        // Seed messages within 6 hours
        await Seeder.SeedRecentMessagesAsync(chatId, 6,
            (userId, "testuser", "Сообщение в последние 6 часов"));

        var handler = GetService<GenerateSummaryHandler>();
        var message = new MessageBuilder()
            .InGroupChat(chatId)
            .From(userId, "testuser", "Test")
            .WithText("/summary 6")
            .Build();

        var request = new GenerateSummaryRequest { Message = message, Hours = 6 };

        // Act
        var response = await handler.HandleAsync(request, CancellationToken.None);

        // Assert
        Assert.True(response.IsSuccess);
        Assert.True(response.MessageCount > 0);
    }

    #endregion

    #region Edge Cases

    [Fact]
    public async Task HandleAsync_SendsTypingIndicator()
    {
        // Arrange
        const long chatId = 200010L;

        var handler = GetService<GenerateSummaryHandler>();
        var message = new MessageBuilder()
            .InGroupChat(chatId)
            .WithText("/summary")
            .Build();

        var request = new GenerateSummaryRequest { Message = message, Hours = 24 };

        // Act
        await handler.HandleAsync(request, CancellationToken.None);

        // Assert
        AssertTypingSent();
    }

    [Fact]
    public async Task HandleAsync_EmptyChat_ReportsCorrectTimeFrame()
    {
        // Arrange
        const long chatId = 200011L;

        var handler = GetService<GenerateSummaryHandler>();
        var message = new MessageBuilder()
            .InGroupChat(chatId)
            .WithText("/summary 48")
            .Build();

        var request = new GenerateSummaryRequest { Message = message, Hours = 48 };

        // Act
        await handler.HandleAsync(request, CancellationToken.None);

        // Assert
        AssertMessageSent("48 час");
    }

    #endregion
}
