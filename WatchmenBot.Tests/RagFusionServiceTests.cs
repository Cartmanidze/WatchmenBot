using System.Reflection;
using WatchmenBot.Features.Search.Models;
using WatchmenBot.Features.Search.Services;
using Xunit;

namespace WatchmenBot.Tests;

/// <summary>
/// Unit tests for RagFusionService query variation generation.
/// Demonstrates the semantic gap issue with bot-directed questions.
/// </summary>
public class RagFusionServiceTests
{
    private static readonly Type RagFusionType = typeof(RagFusionService);
    private static readonly Type FusedSearchResultType = typeof(FusedSearchResult);

    /// <summary>
    /// Verifies current behavior: bot-directed questions generate limited variations
    /// that don't include bot-specific terms like "бот", "создан", "Chat Norris".
    /// </summary>
    [Fact]
    public void GenerateStructuralVariations_BotDirectedQuestion_DoesNotGenerateBotTerms()
    {
        // Arrange
        var query = "ты разочарован из-за своей глупой цели существования?";

        // Act
        var result = InvokeGenerateStructuralVariations(query);

        // Assert - current behavior: only word permutations, no bot-specific terms
        Assert.NotNull(result);
        Assert.True(result.Count <= 3);
        Assert.DoesNotContain(result, v => v.Contains("бот"));
        Assert.DoesNotContain(result, v => v.Contains("создан"));
        Assert.DoesNotContain(result, v => v.Contains("Chat Norris"));
    }

    /// <summary>
    /// Direct bot question "для чего ты создан?" extracts "создан" which matches stored messages.
    /// </summary>
    [Fact]
    public void GenerateStructuralVariations_DirectBotQuestion_ContainsCreatedTerm()
    {
        // Arrange
        var query = "для чего ты создан?";

        // Act
        var result = InvokeGenerateStructuralVariations(query);

        // Assert - "создан" should be in variations
        Assert.NotNull(result);
        Assert.Contains(result, v => v.Contains("создан"));
    }

    /// <summary>
    /// Verifies keyword extraction for hybrid search.
    /// </summary>
    [Fact]
    public void ExtractKeywords_BotDirectedQuestion_ExtractsSignificantWords()
    {
        // Arrange
        var query = "ты разочарован из-за своей глупой цели существования?";
        var variations = new List<string> { "разочарован своей", "своей разочарован" };

        // Act
        var result = InvokeExtractKeywords(query, variations);

        // Assert - significant words extracted, but no semantic expansion
        Assert.NotNull(result);
        Assert.Contains("разочарован", result);
        Assert.Contains("глупой", result);
        Assert.Contains("цели", result);
        Assert.Contains("существования", result);

        // These would help find "ты создан чтобы обрабатывать..." but are NOT extracted
        Assert.DoesNotContain("создан", result);
        Assert.DoesNotContain("обрабат", result);
    }

    /// <summary>
    /// Verifies significant word extraction filters stop words correctly.
    /// </summary>
    [Fact]
    public void ExtractSignificantWords_BotDirectedQuestion_FiltersStopWords()
    {
        // Arrange
        var query = "ты разочарован из-за своей глупой цели существования?";

        // Act
        var result = InvokeExtractSignificantWords(query.ToLowerInvariant());

        // Assert
        Assert.NotNull(result);
        Assert.DoesNotContain(result, w => w == "ты");  // Too short (<3 chars)
        Assert.Contains("разочарован", result);
        Assert.Contains("глупой", result);
        Assert.Contains("цели", result);
        Assert.Contains("существования", result);
    }

    #region SelectBetterResult Tests

    /// <summary>
    /// Verifies that SelectBetterResult prefers non-question over question embedding.
    /// This is critical: when RRF merges duplicates, we want original text, not Q→A bridge.
    /// </summary>
    [Fact]
    public void SelectBetterResult_PrefersNonQuestion_OverQuestion()
    {
        // Arrange
        var originalResult = new SearchResult
        {
            MessageId = 42,
            Similarity = 0.7, // Lower similarity
            IsQuestionEmbedding = false
        };

        var questionResult = new SearchResult
        {
            MessageId = 42,
            Similarity = 0.9, // Higher similarity but it's a question embedding
            IsQuestionEmbedding = true
        };

        // Act
        var selected = InvokeSelectBetterResult(originalResult, questionResult);

        // Assert - should prefer original even though question has higher similarity
        Assert.False(selected.IsQuestionEmbedding);
        Assert.Equal(0.7, selected.Similarity);
    }

    /// <summary>
    /// Verifies that SelectBetterResult keeps existing non-question over new question.
    /// </summary>
    [Fact]
    public void SelectBetterResult_KeepsExistingNonQuestion_WhenCandidateIsQuestion()
    {
        // Arrange
        var existing = new SearchResult
        {
            MessageId = 42,
            Similarity = 0.5, // Even lower
            IsQuestionEmbedding = false
        };

        var candidate = new SearchResult
        {
            MessageId = 42,
            Similarity = 0.95, // Much higher
            IsQuestionEmbedding = true
        };

        // Act
        var selected = InvokeSelectBetterResult(existing, candidate);

        // Assert - keeps existing non-question
        Assert.False(selected.IsQuestionEmbedding);
        Assert.Equal(0.5, selected.Similarity);
    }

    /// <summary>
    /// Verifies that SelectBetterResult uses similarity when both are same type.
    /// </summary>
    [Fact]
    public void SelectBetterResult_UsesSimilarity_WhenBothAreNonQuestion()
    {
        // Arrange
        var existing = new SearchResult
        {
            MessageId = 42,
            Similarity = 0.6,
            IsQuestionEmbedding = false
        };

        var candidate = new SearchResult
        {
            MessageId = 42,
            Similarity = 0.8,
            IsQuestionEmbedding = false
        };

        // Act
        var selected = InvokeSelectBetterResult(existing, candidate);

        // Assert - uses higher similarity
        Assert.Equal(0.8, selected.Similarity);
    }

    /// <summary>
    /// Verifies that SelectBetterResult uses similarity when both are question embeddings.
    /// </summary>
    [Fact]
    public void SelectBetterResult_UsesSimilarity_WhenBothAreQuestion()
    {
        // Arrange
        var existing = new SearchResult
        {
            MessageId = 42,
            Similarity = 0.7,
            IsQuestionEmbedding = true
        };

        var candidate = new SearchResult
        {
            MessageId = 42,
            Similarity = 0.9,
            IsQuestionEmbedding = true
        };

        // Act
        var selected = InvokeSelectBetterResult(existing, candidate);

        // Assert - uses higher similarity
        Assert.True(selected.IsQuestionEmbedding);
        Assert.Equal(0.9, selected.Similarity);
    }

    #endregion

    #region IsQuestionEmbedding Preservation Tests

    /// <summary>
    /// Verifies that FusedSearchResult correctly inherits IsQuestionEmbedding from SearchResult.
    /// This is critical for deduplication: we need to distinguish Q→A bridge embeddings from originals.
    /// </summary>
    [Fact]
    public void FusedSearchResult_InheritsIsQuestionEmbedding_FromBaseClass()
    {
        // Arrange - FusedSearchResult inherits from SearchResult
        var fusedResult = new FusedSearchResult
        {
            MessageId = 123,
            Similarity = 0.85,
            IsQuestionEmbedding = true, // Set the flag
            FusedScore = 0.9
        };

        // Act - cast to SearchResult (as happens in dedup logic)
        SearchResult asSearchResult = fusedResult;

        // Assert - flag must be preserved through cast
        Assert.True(asSearchResult.IsQuestionEmbedding);
    }

    /// <summary>
    /// Verifies that when creating FusedSearchResult from SearchResult,
    /// the IsQuestionEmbedding flag is copied correctly.
    /// </summary>
    [Fact]
    public void FusedSearchResult_ManualCopy_PreservesIsQuestionEmbedding()
    {
        // Arrange - simulating what RRF fusion does
        var originalSearchResult = new SearchResult
        {
            MessageId = 123,
            ChunkText = "Test message",
            Similarity = 0.8,
            IsQuestionEmbedding = true // This is a Q→A bridge embedding
        };

        // Act - create FusedSearchResult copying all properties (as in ApplyRrfFusion)
        var fusedResult = new FusedSearchResult
        {
            MessageId = originalSearchResult.MessageId,
            ChatId = originalSearchResult.ChatId,
            ChunkIndex = originalSearchResult.ChunkIndex,
            ChunkText = originalSearchResult.ChunkText,
            MetadataJson = originalSearchResult.MetadataJson,
            Similarity = originalSearchResult.Similarity,
            Distance = originalSearchResult.Distance,
            IsNewsDump = originalSearchResult.IsNewsDump,
            IsQuestionEmbedding = originalSearchResult.IsQuestionEmbedding, // Must copy this!
            FusedScore = 0.5,
            MatchedQueryCount = 1,
            MatchedQueryIndices = [0]
        };

        // Assert
        Assert.True(fusedResult.IsQuestionEmbedding);
        Assert.Equal(originalSearchResult.MessageId, fusedResult.MessageId);
    }

    /// <summary>
    /// Verifies that mixing question and non-question embeddings works correctly
    /// when creating FusedSearchResult. Each result should keep its own flag.
    /// </summary>
    [Fact]
    public void FusedSearchResult_MixedQuestionFlags_EachPreservesOwnFlag()
    {
        // Arrange
        var questionResult = new SearchResult
        {
            MessageId = 100,
            IsQuestionEmbedding = true
        };

        var originalResult = new SearchResult
        {
            MessageId = 101,
            IsQuestionEmbedding = false
        };

        // Act - create fused results
        var fusedQuestion = new FusedSearchResult
        {
            MessageId = questionResult.MessageId,
            IsQuestionEmbedding = questionResult.IsQuestionEmbedding,
            FusedScore = 0.9
        };

        var fusedOriginal = new FusedSearchResult
        {
            MessageId = originalResult.MessageId,
            IsQuestionEmbedding = originalResult.IsQuestionEmbedding,
            FusedScore = 0.8
        };

        // Assert - each keeps its own flag
        Assert.True(fusedQuestion.IsQuestionEmbedding);
        Assert.False(fusedOriginal.IsQuestionEmbedding);
    }

    /// <summary>
    /// Verifies that when deduplicating FusedSearchResults cast to SearchResult,
    /// the IsQuestionEmbedding flag is accessible for preference logic.
    /// </summary>
    [Fact]
    public void DeduplicateFusedResults_PreservesIsQuestionEmbedding_ForPreferenceLogic()
    {
        // Arrange - same message ID, one is question embedding, one is original
        var questionFused = new FusedSearchResult
        {
            MessageId = 42,
            Similarity = 0.95,
            IsQuestionEmbedding = true,
            FusedScore = 0.95
        };

        var originalFused = new FusedSearchResult
        {
            MessageId = 42,
            Similarity = 0.85,
            IsQuestionEmbedding = false,
            FusedScore = 0.85
        };

        // Act - cast to SearchResult list (as happens before dedup)
        var asSearchResults = new List<SearchResult> { questionFused, originalFused };

        // Group by message ID (as in DeduplicateByMessageId)
        var grouped = asSearchResults.GroupBy(r => r.MessageId).First();

        // Find best non-question (as SelectPreferredResult does)
        var bestNonQuestion = grouped
            .Where(r => !r.IsQuestionEmbedding)
            .OrderByDescending(r => r.Similarity)
            .FirstOrDefault();

        // Assert - should find the original despite lower similarity
        Assert.NotNull(bestNonQuestion);
        Assert.False(bestNonQuestion.IsQuestionEmbedding);
        Assert.Equal(0.85, bestNonQuestion.Similarity);
    }

    #endregion

    #region Reflection Helpers

    private static List<string> InvokeGenerateStructuralVariations(string query)
    {
        var method = RagFusionType.GetMethod("GenerateStructuralVariations", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return method.Invoke(null, [query, null]) as List<string> ?? [];
    }

    private static string InvokeExtractKeywords(string query, List<string> variations)
    {
        var method = RagFusionType.GetMethod("ExtractKeywords", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return method.Invoke(null, [query, variations]) as string ?? "";
    }

    private static List<string> InvokeExtractSignificantWords(string text)
    {
        var method = RagFusionType.GetMethod("ExtractSignificantWords", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return method.Invoke(null, [text]) as List<string> ?? [];
    }

    private static SearchResult InvokeSelectBetterResult(SearchResult existing, SearchResult candidate)
    {
        var method = RagFusionType.GetMethod("SelectBetterResult", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return method.Invoke(null, [existing, candidate]) as SearchResult ?? existing;
    }

    #endregion
}
