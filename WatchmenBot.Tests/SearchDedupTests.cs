using System.Reflection;
using WatchmenBot.Features.Search;
using WatchmenBot.Features.Search.Models;
using Xunit;

namespace WatchmenBot.Tests;

/// <summary>
/// Unit tests for SearchStrategyService deduplication logic.
/// Validates preference for original embeddings over question embeddings.
/// </summary>
public class SearchDedupTests
{
    private static readonly Type SearchStrategyType = typeof(SearchStrategyService);

    #region Basic Dedup Behavior

    [Fact]
    public void SelectPreferredResult_SingleResult_ReturnsSame()
    {
        // Arrange
        var result = CreateResult(messageId: 1, similarity: 0.9, isQuestion: false);
        var group = new[] { result }.GroupBy(r => r.MessageId).First();

        // Act
        var selected = InvokeSelectPreferredResult(group);

        // Assert
        Assert.Same(result, selected);
    }

    [Fact]
    public void SelectPreferredResult_PreferNonQuestion_OverHigherSimilarityQuestion()
    {
        // Arrange - question embedding has higher similarity but we prefer original
        var questionResult = CreateResult(messageId: 1, similarity: 0.95, isQuestion: true);
        var originalResult = CreateResult(messageId: 1, similarity: 0.85, isQuestion: false);
        var group = new[] { questionResult, originalResult }.GroupBy(r => r.MessageId).First();

        // Act
        var selected = InvokeSelectPreferredResult(group);

        // Assert
        Assert.False(selected.IsQuestionEmbedding);
        Assert.Equal(0.85, selected.Similarity);
    }

    [Fact]
    public void SelectPreferredResult_AllQuestions_ReturnsBestQuestion()
    {
        // Arrange - only question embeddings available
        var lowScore = CreateResult(messageId: 1, similarity: 0.7, isQuestion: true);
        var highScore = CreateResult(messageId: 1, similarity: 0.9, isQuestion: true);
        var group = new[] { lowScore, highScore }.GroupBy(r => r.MessageId).First();

        // Act
        var selected = InvokeSelectPreferredResult(group);

        // Assert
        Assert.True(selected.IsQuestionEmbedding);
        Assert.Equal(0.9, selected.Similarity);
    }

    #endregion

    #region Score Consistency

    [Fact]
    public void SelectPreferredResult_UsesSelectedResultScore()
    {
        // Arrange - verify score comes from selected result, not borrowed
        var questionResult = CreateResult(messageId: 1, similarity: 0.99, isQuestion: true);
        var originalResult = CreateResult(messageId: 1, similarity: 0.75, isQuestion: false);
        var group = new[] { questionResult, originalResult }.GroupBy(r => r.MessageId).First();

        // Act
        var selected = InvokeSelectPreferredResult(group);

        // Assert - score should be 0.75 (original's score), not 0.99 (question's score)
        Assert.Equal(0.75, selected.Similarity);
        Assert.False(selected.IsQuestionEmbedding);
    }

    [Fact]
    public void SelectPreferredResult_MultipleNonQuestions_ReturnsBestNonQuestion()
    {
        // Arrange - multiple original embeddings (e.g., different chunks)
        var chunk1 = CreateResult(messageId: 1, similarity: 0.8, isQuestion: false, chunkIndex: 0);
        var chunk2 = CreateResult(messageId: 1, similarity: 0.9, isQuestion: false, chunkIndex: 1);
        var question = CreateResult(messageId: 1, similarity: 0.95, isQuestion: true);
        var group = new[] { chunk1, chunk2, question }.GroupBy(r => r.MessageId).First();

        // Act
        var selected = InvokeSelectPreferredResult(group);

        // Assert - should return chunk2 (highest non-question)
        Assert.False(selected.IsQuestionEmbedding);
        Assert.Equal(0.9, selected.Similarity);
        Assert.Equal(1, selected.ChunkIndex);
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void SelectPreferredResult_IdenticalScores_PreferNonQuestion()
    {
        // Arrange
        var questionResult = CreateResult(messageId: 1, similarity: 0.85, isQuestion: true);
        var originalResult = CreateResult(messageId: 1, similarity: 0.85, isQuestion: false);
        var group = new[] { questionResult, originalResult }.GroupBy(r => r.MessageId).First();

        // Act
        var selected = InvokeSelectPreferredResult(group);

        // Assert
        Assert.False(selected.IsQuestionEmbedding);
    }

    [Fact]
    public void SelectPreferredResult_LegacyChunkIndexNegative_TreatedAsQuestion()
    {
        // Arrange - legacy convention: ChunkIndex < 0 means question embedding
        // But IsQuestionEmbedding flag takes precedence
        var result = CreateResult(messageId: 1, similarity: 0.9, isQuestion: true);
        result.ChunkIndex = -1; // Legacy marker
        var group = new[] { result }.GroupBy(r => r.MessageId).First();

        // Act
        var selected = InvokeSelectPreferredResult(group);

        // Assert
        Assert.True(selected.IsQuestionEmbedding);
    }

    #endregion

    #region Helpers

    private static SearchResult CreateResult(long messageId, double similarity, bool isQuestion, int chunkIndex = 0)
    {
        return new SearchResult
        {
            MessageId = messageId,
            Similarity = similarity,
            IsQuestionEmbedding = isQuestion,
            ChunkIndex = chunkIndex,
            ChunkText = $"Test message {messageId}"
        };
    }

    private static SearchResult InvokeSelectPreferredResult(IGrouping<long, SearchResult> group)
    {
        var method = SearchStrategyType.GetMethod(
            "SelectPreferredResult",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var result = method.Invoke(null, [group]);
        Assert.NotNull(result);

        return (SearchResult)result;
    }

    #endregion
}
