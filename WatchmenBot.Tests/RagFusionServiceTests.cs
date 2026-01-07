using System.Reflection;
using Xunit;

namespace WatchmenBot.Tests;

/// <summary>
/// Unit tests for RagFusionService query variation generation.
/// Demonstrates the semantic gap issue with bot-directed questions.
/// </summary>
public class RagFusionServiceTests
{
    private static readonly Type RagFusionType = typeof(WatchmenBot.Features.Search.Services.RagFusionService);

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

    #endregion
}
