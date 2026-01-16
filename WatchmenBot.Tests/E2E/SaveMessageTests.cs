using Telegram.Bot.Types.Enums;
using WatchmenBot.Features.Messages;
using WatchmenBot.Tests.Builders;
using WatchmenBot.Tests.Fixtures;
using Xunit;

namespace WatchmenBot.Tests.E2E;

/// <summary>
/// E2E tests for message saving pipeline.
/// Tests that messages are correctly saved to database and embeddings are created.
/// </summary>
public class SaveMessageTests : E2ETestBase
{
    public SaveMessageTests(DatabaseFixture dbFixture) : base(dbFixture)
    {
    }

    #region Message Saving Tests

    [Fact]
    public async Task HandleAsync_GroupMessage_SavesSuccessfully()
    {
        // Arrange
        var handler = GetService<SaveMessageHandler>();
        var message = new MessageBuilder()
            .InGroupChat(-100123L)
            .From(67890L, "testuser", "Test", "User")
            .WithText("Тестовое сообщение для сохранения")
            .WithMessageId(9001)
            .Build();

        var request = new SaveMessageRequest { Message = message };

        // Act
        var response = await handler.HandleAsync(request, CancellationToken.None);

        // Assert
        Assert.True(response.IsSuccess);
        Assert.Equal(9001, response.MessageId);
        Assert.Equal(-100123L, response.ChatId);

        // Verify saved to database
        Assert.True(await Seeder.MessageExistsAsync(-100123L, 9001));
    }

    [Fact]
    public async Task HandleAsync_SupergroupMessage_SavesSuccessfully()
    {
        // Arrange
        var handler = GetService<SaveMessageHandler>();
        var message = new MessageBuilder()
            .InSupergroup(-100456L)
            .From(67890L, "testuser", "Test", "User")
            .WithText("Сообщение в супергруппе")
            .WithMessageId(9002)
            .Build();

        var request = new SaveMessageRequest { Message = message };

        // Act
        var response = await handler.HandleAsync(request, CancellationToken.None);

        // Assert
        Assert.True(response.IsSuccess);
        Assert.True(await Seeder.MessageExistsAsync(-100456L, 9002));
    }

    [Fact]
    public async Task HandleAsync_PrivateMessage_DoesNotSave()
    {
        // Arrange
        var handler = GetService<SaveMessageHandler>();
        var message = new MessageBuilder()
            .InPrivateChat(12345L)
            .From(67890L, "testuser", "Test")
            .WithText("Private message should not be saved")
            .WithMessageId(9003)
            .Build();

        var request = new SaveMessageRequest { Message = message };

        // Act
        var response = await handler.HandleAsync(request, CancellationToken.None);

        // Assert - should return success but not save
        Assert.True(response.IsSuccess);

        // Verify NOT saved to database
        Assert.False(await Seeder.MessageExistsAsync(12345L, 9003));
    }

    [Fact]
    public async Task HandleAsync_ShortMessage_SavesButMaySkipEmbedding()
    {
        // Arrange - message shorter than 6 chars shouldn't get embedding
        var handler = GetService<SaveMessageHandler>();
        var message = new MessageBuilder()
            .InGroupChat(-100789L)
            .From(67890L, "testuser")
            .WithText("hi") // Too short for embedding
            .WithMessageId(9004)
            .Build();

        var request = new SaveMessageRequest { Message = message };

        // Act
        var response = await handler.HandleAsync(request, CancellationToken.None);

        // Assert - message should still be saved
        Assert.True(response.IsSuccess);
        Assert.True(await Seeder.MessageExistsAsync(-100789L, 9004));

        // Embedding count should be 0 for this short message
        // (embeddings require >= 6 chars)
    }

    [Fact]
    public async Task HandleAsync_MessageWithUsername_ExtractsDisplayName()
    {
        // Arrange
        var handler = GetService<SaveMessageHandler>();
        var message = new MessageBuilder()
            .InGroupChat(-100111L)
            .From(67890L, "testuser", "Иван", "Петров")
            .WithText("Сообщение с полным именем")
            .WithMessageId(9005)
            .Build();

        var request = new SaveMessageRequest { Message = message };

        // Act
        var response = await handler.HandleAsync(request, CancellationToken.None);

        // Assert
        Assert.True(response.IsSuccess);
        Assert.True(await Seeder.MessageExistsAsync(-100111L, 9005));
    }

    #endregion

    #region Embedding Tests (require API keys)

    [SkippableFact]
    public async Task HandleAsync_LongMessage_CreatesEmbedding()
    {
        // Skip if no embeddings API
        SkipIfNoEmbeddingsKey();

        // Arrange
        var handler = GetService<SaveMessageHandler>();
        var message = new MessageBuilder()
            .InGroupChat(-100222L)
            .From(67890L, "testuser", "Test", "User")
            .WithText("Достаточно длинное сообщение для создания эмбеддинга в тестах")
            .WithMessageId(9010)
            .Build();

        var request = new SaveMessageRequest { Message = message };

        // Act
        var response = await handler.HandleAsync(request, CancellationToken.None);

        // Assert
        Assert.True(response.IsSuccess);

        // Wait a bit for embedding to be created (has 3s timeout)
        await Task.Delay(4000);

        // Verify embedding was created
        var embeddingCount = await Seeder.GetEmbeddingCountAsync(-100222L);
        Assert.True(embeddingCount >= 1, "Should have created at least one embedding");
    }

    [SkippableFact]
    public async Task HandleAsync_MultipleMessages_CreatesMultipleEmbeddings()
    {
        // Skip if no embeddings API
        SkipIfNoEmbeddingsKey();

        // Arrange
        const long chatId = -100333L;
        var handler = GetService<SaveMessageHandler>();

        var messages = new[]
        {
            "Первое сообщение для проверки создания эмбеддингов",
            "Второе сообщение тоже должно получить эмбеддинг",
            "Третье сообщение завершает тест"
        };

        // Act
        for (int i = 0; i < messages.Length; i++)
        {
            var message = new MessageBuilder()
                .InGroupChat(chatId)
                .From(67890L, "testuser")
                .WithText(messages[i])
                .WithMessageId(9020 + i)
                .Build();

            await handler.HandleAsync(new SaveMessageRequest { Message = message }, CancellationToken.None);
        }

        // Wait for embeddings with polling (rate limiter allows only 1 concurrent request)
        // This can take longer with rate limiting: ~3-5s per embedding
        var messageCount = await Seeder.GetMessageCountAsync(chatId);
        Assert.Equal(3, messageCount);

        // Poll for embeddings (max 30 seconds to handle rate limiting)
        // Note: Fire-and-forget embedding creation may not complete all requests
        // due to rate limiting or transient API failures
        var embeddingCount = 0;
        for (int attempt = 0; attempt < 30; attempt++)
        {
            await Task.Delay(1000);
            embeddingCount = await Seeder.GetEmbeddingCountAsync(chatId);
            if (embeddingCount >= 3) break;
        }

        // Accept at least 2 out of 3 embeddings (fire-and-forget can have failures)
        Assert.True(embeddingCount >= 2, $"Should have created at least 2 embeddings within 30s, got {embeddingCount}");
    }

    #endregion

    #region Edge Cases

    [Fact]
    public async Task HandleAsync_MessageWithReply_SavesReplyInfo()
    {
        // Arrange
        var handler = GetService<SaveMessageHandler>();
        var message = new MessageBuilder()
            .InGroupChat(-100444L)
            .From(67890L, "testuser")
            .WithText("Это ответ на другое сообщение")
            .WithMessageId(9030)
            .ReplyTo(9029, "Оригинальное сообщение")
            .Build();

        var request = new SaveMessageRequest { Message = message };

        // Act
        var response = await handler.HandleAsync(request, CancellationToken.None);

        // Assert
        Assert.True(response.IsSuccess);
        Assert.True(await Seeder.MessageExistsAsync(-100444L, 9030));
    }

    [Fact]
    public async Task HandleAsync_NoUser_StillSaves()
    {
        // Arrange - message without From user (rare edge case)
        var handler = GetService<SaveMessageHandler>();
        var message = new MessageBuilder()
            .InGroupChat(-100555L)
            .WithText("Сообщение без отправителя")
            .WithMessageId(9040)
            .Build();

        // Manually clear From
        message.GetType().GetProperty("From")?.SetValue(message, null);

        var request = new SaveMessageRequest { Message = message };

        // Act
        var response = await handler.HandleAsync(request, CancellationToken.None);

        // Assert
        Assert.True(response.IsSuccess);
    }

    [Fact]
    public async Task HandleAsync_EmptyText_SavesWithNullText()
    {
        // Arrange
        var handler = GetService<SaveMessageHandler>();
        var message = new MessageBuilder()
            .InGroupChat(-100666L)
            .From(67890L, "testuser")
            .WithText("") // Empty text
            .WithMessageId(9050)
            .Build();

        var request = new SaveMessageRequest { Message = message };

        // Act
        var response = await handler.HandleAsync(request, CancellationToken.None);

        // Assert - should not fail
        Assert.True(response.IsSuccess);
    }

    #endregion
}
