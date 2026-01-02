namespace WatchmenBot.Features.Search.Models;

/// <summary>
/// Confidence level for search results
/// </summary>
public enum SearchConfidence
{
    /// <summary>No matches — don't feed to LLM</summary>
    None = 0,

    /// <summary>Weak matches — warn user</summary>
    Low = 1,

    /// <summary>Medium matches — can use with caveat</summary>
    Medium = 2,

    /// <summary>Good matches — confident answer</summary>
    High = 3
}
