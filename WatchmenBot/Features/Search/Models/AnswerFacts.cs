namespace WatchmenBot.Features.Search.Models;

/// <summary>
/// Extracted facts from context for anti-hallucination answer generation.
/// Stage 1 output: structured facts that can be verified against source context.
/// </summary>
public class AnswerFacts
{
    /// <summary>
    /// Verified facts extracted from context
    /// </summary>
    public List<ExtractedFact> Facts { get; set; } = [];

    /// <summary>
    /// Information that was asked about but NOT found in context
    /// </summary>
    public List<string> NotFound { get; set; } = [];

    /// <summary>
    /// Whether sufficient facts were found to answer the question
    /// </summary>
    public bool HasSufficientInfo => Facts.Count > 0;
}

/// <summary>
/// A single verified fact extracted from context
/// </summary>
public class ExtractedFact
{
    /// <summary>
    /// The factual claim (e.g., "Вася работает программистом")
    /// </summary>
    public required string Claim { get; set; }

    /// <summary>
    /// Source reference (e.g., "сообщение от Васи", "упоминание от Пети")
    /// </summary>
    public string? Source { get; set; }

    /// <summary>
    /// Confidence in this fact (high/medium/low)
    /// </summary>
    public string Confidence { get; set; } = "medium";
}