using WatchmenBot.Features.Search;
using Xunit;

namespace WatchmenBot.Tests;

/// <summary>
/// Unit tests for QueryNormalizer.
/// Validates query cleanup for search and intent classification.
/// </summary>
public class QueryNormalizerTests
{
    #region Null and Empty Input

    [Fact]
    public void Normalize_NullInput_ReturnsEmpty()
    {
        var result = QueryNormalizer.Normalize(null);
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void Normalize_EmptyString_ReturnsEmpty()
    {
        var result = QueryNormalizer.Normalize("");
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void Normalize_WhitespaceOnly_ReturnsEmpty()
    {
        var result = QueryNormalizer.Normalize("   \t\n\r   ");
        Assert.Equal(string.Empty, result);
    }

    #endregion

    #region Whitespace Normalization

    [Fact]
    public void Normalize_TabsAndNewlines_ReplacedWithSpaces()
    {
        var result = QueryNormalizer.Normalize("hello\tworld\ntest");
        Assert.Equal("hello world test", result);
    }

    [Fact]
    public void Normalize_NonBreakingSpace_ReplacedWithSpace()
    {
        var result = QueryNormalizer.Normalize("hello\u00A0world");
        Assert.Equal("hello world", result);
    }

    [Fact]
    public void Normalize_MultipleSpaces_CollapsedToOne()
    {
        var result = QueryNormalizer.Normalize("hello    world   test");
        Assert.Equal("hello world test", result);
    }

    [Fact]
    public void Normalize_LeadingTrailingSpaces_Trimmed()
    {
        var result = QueryNormalizer.Normalize("   hello world   ");
        Assert.Equal("hello world", result);
    }

    #endregion

    #region Zero-Width Characters

    [Fact]
    public void Normalize_ZeroWidthSpace_Removed()
    {
        var result = QueryNormalizer.Normalize("hello\u200Bworld");
        Assert.Equal("helloworld", result);
    }

    [Fact]
    public void Normalize_ZeroWidthNonJoiner_Removed()
    {
        var result = QueryNormalizer.Normalize("hello\u200Cworld");
        Assert.Equal("helloworld", result);
    }

    [Fact]
    public void Normalize_ZeroWidthJoiner_Removed()
    {
        var result = QueryNormalizer.Normalize("hello\u200Dworld");
        Assert.Equal("helloworld", result);
    }

    [Fact]
    public void Normalize_BOM_Removed()
    {
        var result = QueryNormalizer.Normalize("\uFEFFhello world");
        Assert.Equal("hello world", result);
    }

    [Fact]
    public void Normalize_OnlyZeroWidthChars_ReturnsEmpty()
    {
        var result = QueryNormalizer.Normalize("\u200B\u200C\u200D\uFEFF");
        Assert.Equal(string.Empty, result);
    }

    #endregion

    #region Punctuation Normalization

    [Fact]
    public void Normalize_EnDash_ReplacedWithHyphen()
    {
        var result = QueryNormalizer.Normalize("2020\u20132024");
        Assert.Equal("2020-2024", result);
    }

    [Fact]
    public void Normalize_EmDash_ReplacedWithHyphen()
    {
        var result = QueryNormalizer.Normalize("word\u2014another");
        Assert.Equal("word-another", result);
    }

    [Fact]
    public void Normalize_SmartQuotes_ReplacedWithSimple()
    {
        var result = QueryNormalizer.Normalize("\u201Chello\u201D \u2018world\u2019");
        Assert.Equal("\"hello\" 'world'", result);
    }

    [Fact]
    public void Normalize_Ellipsis_ReplacedWithThreeDots()
    {
        var result = QueryNormalizer.Normalize("wait\u2026");
        Assert.Equal("wait...", result);
    }

    #endregion

    #region Emoji Handling

    [Fact]
    public void Normalize_EmojiPreserved_ByDefault()
    {
        var result = QueryNormalizer.Normalize("кто 🤡?");
        Assert.Contains("🤡", result);
    }

    [Fact]
    public void Normalize_EmojiRemoved_WhenFlagSet()
    {
        var result = QueryNormalizer.Normalize("кто 🤡?", removeEmoji: true);
        Assert.DoesNotContain("🤡", result);
        Assert.Equal("кто ?", result);
    }

    [Fact]
    public void Normalize_MultipleEmoji_PreserveWordBoundaries()
    {
        var result = QueryNormalizer.Normalize("test🔥word🎉end", removeEmoji: true);
        Assert.Equal("test word end", result);
    }

    [Fact]
    public void Normalize_OnlyEmoji_ReturnsEmpty_WhenRemoved()
    {
        var result = QueryNormalizer.Normalize("🤡🔥🎉", removeEmoji: true);
        Assert.Equal(string.Empty, result);
    }

    #endregion

    #region Real-World Queries

    [Fact]
    public void Normalize_CopyPastedText_CleanedUp()
    {
        // Simulating copy-paste from Word/Google Docs
        var input = "\u200B\u200BПочему\u00A0бот\u2014не\u00A0работает\u2026  ";
        var result = QueryNormalizer.Normalize(input);
        Assert.Equal("Почему бот-не работает...", result);
    }

    [Fact]
    public void Normalize_TelegramFormatted_Preserved()
    {
        // Telegram allows some formatting, ensure meaningful content preserved
        var result = QueryNormalizer.Normalize("что говорил @username о проекте?");
        Assert.Equal("что говорил @username о проекте?", result);
    }

    [Fact]
    public void Normalize_RussianWithPunctuation_Preserved()
    {
        var result = QueryNormalizer.Normalize("Когда будет готов проект, примерно?");
        Assert.Equal("Когда будет готов проект, примерно?", result);
    }

    #endregion
}
