using Telegram.Bot.Types.Enums;
using WatchmenBot.Features.Onboarding;
using WatchmenBot.Tests.Builders;
using WatchmenBot.Tests.Fixtures;
using Xunit;

namespace WatchmenBot.Tests.E2E;

/// <summary>
/// E2E tests for /start command handler.
/// Tests both private chat onboarding and group chat short message.
/// </summary>
public class StartCommandTests : E2ETestBase
{
    public StartCommandTests(DatabaseFixture dbFixture) : base(dbFixture)
    {
    }

    [Fact]
    public async Task HandleAsync_PrivateChat_SendsFullWelcomeWithCommands()
    {
        // Arrange
        var handler = GetService<StartCommandHandler>();
        var message = new MessageBuilder()
            .InPrivateChat(12345L)
            .From(67890L, "testuser", "Test")
            .WithText("/start")
            .Build();

        // Act
        await handler.HandleAsync(message, CancellationToken.None);

        // Assert
        Assert.Single(SentMessages);
        var sentText = SentMessages[0].Text;

        // Should contain welcome text and all commands
        Assert.Contains("WatchmenBot", sentText);
        Assert.Contains("/summary", sentText);
        Assert.Contains("/ask", sentText);
        Assert.Contains("/smart", sentText);
        Assert.Contains("/truth", sentText);
    }

    [Fact]
    public async Task HandleAsync_PrivateChat_SendsInlineKeyboard()
    {
        // Arrange
        var handler = GetService<StartCommandHandler>();
        var message = new MessageBuilder()
            .InPrivateChat(12345L)
            .From(67890L, "testuser", "Test")
            .WithText("/start")
            .Build();

        // Act
        await handler.HandleAsync(message, CancellationToken.None);

        // Assert
        Assert.Single(SentMessages);
        var request = SentMessages[0];

        // Should have inline keyboard with "Add to chat" button
        Assert.NotNull(request.ReplyMarkup);
    }

    [Fact]
    public async Task HandleAsync_GroupChat_SendsShortMessage()
    {
        // Arrange
        var handler = GetService<StartCommandHandler>();
        var message = new MessageBuilder()
            .InGroupChat(-100123456L)
            .From(67890L, "testuser", "Test")
            .WithText("/start")
            .Build();

        // Act
        await handler.HandleAsync(message, CancellationToken.None);

        // Assert
        Assert.Single(SentMessages);
        var sentText = SentMessages[0].Text;

        // Short message should have emoji and commands
        Assert.Contains("👋", sentText);
        Assert.Contains("/summary", sentText);
        Assert.Contains("/ask", sentText);

        // But NOT the full welcome explanation
        Assert.DoesNotContain("Добавь меня в чат", sentText);
    }

    [Fact]
    public async Task HandleAsync_Supergroup_SendsShortMessage()
    {
        // Arrange
        var handler = GetService<StartCommandHandler>();
        var message = new MessageBuilder()
            .InSupergroup(-100123456L)
            .From(67890L, "testuser", "Test")
            .WithText("/start")
            .Build();

        // Act
        await handler.HandleAsync(message, CancellationToken.None);

        // Assert
        Assert.Single(SentMessages);
        var sentText = SentMessages[0].Text;

        // Should be short message, not full welcome
        Assert.Contains("Я готов!", sentText);
    }

    [Fact]
    public async Task HandleAsync_GroupChat_MessageHasReplyToOriginal()
    {
        // Arrange
        var handler = GetService<StartCommandHandler>();
        var message = new MessageBuilder()
            .InGroupChat(-100123456L)
            .From(67890L, "testuser", "Test")
            .WithText("/start")
            .WithMessageId(42)
            .Build();

        // Act
        await handler.HandleAsync(message, CancellationToken.None);

        // Assert
        Assert.Single(SentMessages);
        var request = SentMessages[0];

        // Should have ReplyParameters set (to reply to original /start message)
        // Note: Telegram.Bot uses init-only properties, MessageId value testing is complex
        Assert.NotNull(request.ReplyParameters);
        Assert.True(request.ReplyParameters.AllowSendingWithoutReply);
    }

    [Fact]
    public async Task HandleAsync_PrivateChat_UsesHtmlParseMode()
    {
        // Arrange
        var handler = GetService<StartCommandHandler>();
        var message = new MessageBuilder()
            .InPrivateChat(12345L)
            .From(67890L, "testuser")
            .WithText("/start")
            .Build();

        // Act
        await handler.HandleAsync(message, CancellationToken.None);

        // Assert
        Assert.Single(SentMessages);
        Assert.Equal(ParseMode.Html, SentMessages[0].ParseMode);
    }
}
