using Hangfire;
using Hangfire.States;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Telegram.Bot.Requests;
using Telegram.Bot.Types.Enums;
using WatchmenBot.Features.Search.Services;
using WatchmenBot.Tests.Fixtures;
using Xunit;

namespace WatchmenBot.Tests.E2E;

/// <summary>
/// Base class for E2E tests.
/// Provides common setup, teardown, and assertion helpers.
/// </summary>
[Collection("E2E")]
public abstract class E2ETestBase : IAsyncLifetime
{
    protected WatchmenTestFixture Fixture { get; }
    protected TestDataSeeder Seeder { get; private set; } = null!;
    protected TestConfiguration TestConfig => Fixture.TestConfig;

    // Convenience shortcuts
    protected List<SendMessageRequest> SentMessages => Fixture.SentMessages;
    protected List<EditMessageTextRequest> EditedMessages => Fixture.EditedMessages;
    protected List<SendChatActionRequest> ChatActions => Fixture.ChatActions;
    protected Mock<IBackgroundJobClient> JobClientMock => Fixture.JobClientMock;

    protected E2ETestBase(DatabaseFixture dbFixture)
    {
        Fixture = new WatchmenTestFixture(dbFixture);
    }

    public virtual async Task InitializeAsync()
    {
        await Fixture.InitializeAsync();
        Seeder = new TestDataSeeder(
            Fixture.DbFixture.ConnectionFactory!,
            Fixture.GetService<EmbeddingClient>());
    }

    public virtual async Task DisposeAsync()
    {
        await Fixture.CleanDatabaseAsync();
        await Fixture.DisposeAsync();
    }

    #region Service Access

    /// <summary>
    /// Get a service from the DI container.
    /// </summary>
    protected T GetService<T>() where T : notnull =>
        Fixture.GetService<T>();

    /// <summary>
    /// Create a new scope for scoped services.
    /// </summary>
    protected IServiceScope CreateScope() =>
        Fixture.CreateScope();

    #endregion

    #region Assertion Helpers

    /// <summary>
    /// Assert that a message was sent containing the specified text.
    /// </summary>
    protected void AssertMessageSent(string containsText)
    {
        Assert.True(
            Fixture.MessageContains(containsText),
            $"Expected a message containing '{containsText}', but none found.\n" +
            $"Sent messages: {string.Join(", ", SentMessages.Select(m => $"'{m.Text[..Math.Min(50, m.Text.Length)]}...'"))}");
    }

    /// <summary>
    /// Assert that a message was sent matching the predicate.
    /// </summary>
    protected void AssertMessageSent(Func<SendMessageRequest, bool> predicate, string description = "matching predicate")
    {
        Assert.True(
            SentMessages.Any(predicate),
            $"Expected a message {description}, but none found.\n" +
            $"Sent messages count: {SentMessages.Count}");
    }

    /// <summary>
    /// Assert that no messages were sent.
    /// </summary>
    protected void AssertNoMessageSent()
    {
        Assert.Empty(SentMessages);
    }

    /// <summary>
    /// Assert that exactly N messages were sent.
    /// </summary>
    protected void AssertMessageCount(int expectedCount)
    {
        Assert.Equal(expectedCount, SentMessages.Count);
    }

    /// <summary>
    /// Assert that a typing indicator was sent.
    /// </summary>
    protected void AssertTypingSent()
    {
        Assert.Contains(ChatActions, a => a.Action == ChatAction.Typing);
    }

    /// <summary>
    /// Assert that a Hangfire job was scheduled.
    /// </summary>
    protected void AssertJobScheduled()
    {
        JobClientMock.Verify(
            j => j.Create(It.IsAny<Hangfire.Common.Job>(), It.IsAny<IState>()),
            Times.AtLeastOnce,
            "Expected a Hangfire job to be scheduled, but none was.");
    }

    /// <summary>
    /// Assert that no Hangfire jobs were scheduled.
    /// </summary>
    protected void AssertNoJobScheduled()
    {
        JobClientMock.Verify(
            j => j.Create(It.IsAny<Hangfire.Common.Job>(), It.IsAny<IState>()),
            Times.Never,
            "Expected no Hangfire jobs to be scheduled.");
    }

    /// <summary>
    /// Get the last sent message text, or null if none sent.
    /// </summary>
    protected string? LastMessageText => Fixture.LastSentMessageText;

    #endregion

    #region Skip Helpers

    /// <summary>
    /// Skip test if OpenRouter API key is not configured.
    /// </summary>
    protected void SkipIfNoLlmKey()
    {
        Skip.If(!TestConfig.HasOpenRouterKey,
            "OpenRouter API key not configured. Set OPENROUTER_API_KEY environment variable or configure in appsettings.Development.json");
    }

    /// <summary>
    /// Skip test if Embeddings API key is not configured.
    /// </summary>
    protected void SkipIfNoEmbeddingsKey()
    {
        Skip.If(!TestConfig.HasOpenAiKey,
            "Embeddings API key not configured. Set OPENAI_API_KEY environment variable or configure in appsettings.Development.json");
    }

    /// <summary>
    /// Skip test if any required API key is missing.
    /// </summary>
    protected void SkipIfNoApiKeys()
    {
        SkipIfNoLlmKey();
        SkipIfNoEmbeddingsKey();
    }

    #endregion
}

/// <summary>
/// Attribute to mark tests that require API keys.
/// These tests will be skipped if keys are not configured.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public class RequiresApiKeysAttribute : Attribute;
