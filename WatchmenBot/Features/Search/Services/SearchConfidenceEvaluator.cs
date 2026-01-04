using WatchmenBot.Features.Search.Models;

namespace WatchmenBot.Features.Search.Services;

/// <summary>
/// Evaluates search confidence based on similarity scores and text match signals.
/// Also applies result adjustments (recency boost, news dump penalty).
/// </summary>
public class SearchConfidenceEvaluator
{
    // Thresholds for confidence evaluation
    private const double HighConfidenceThreshold = 0.5;
    private const double MediumConfidenceThreshold = 0.4;
    private const double LowConfidenceThreshold = 0.35;
    private const double MinConfidenceThreshold = 0.25;
    private const double SignificantGapThreshold = 0.05;
    private const double SmallGapThreshold = 0.03;

    // Result adjustment constants are defined in SearchConstants class
    // Note: Recency boost is now applied in SQL via time decay formula (see EmbeddingService, PersonalSearchService)

    /// <summary>
    /// Evaluate search confidence based on scores
    /// </summary>
    /// <param name="bestScore">Best similarity score from search results</param>
    /// <param name="gap">Gap between top-1 and top-5 scores</param>
    /// <param name="hasFullText">Whether full-text search found exact matches</param>
    /// <returns>Confidence level and explanation</returns>
    public (SearchConfidence confidence, string reason) Evaluate(double bestScore, double gap, bool hasFullText)
    {
        // If full-text found exact matches, that's a strong signal
        if (hasFullText)
        {
            if (bestScore >= HighConfidenceThreshold)
                return (SearchConfidence.High, "Точное совпадение слов + высокий similarity");
            if (bestScore >= LowConfidenceThreshold)
                return (SearchConfidence.Medium, "Точное совпадение слов");
            return (SearchConfidence.Low, "Слова найдены, но семантически далеко");
        }

        // Vector-only search thresholds
        // High: best >= 0.5 AND gap >= 0.05 (clear winner)
        if (bestScore >= HighConfidenceThreshold && gap >= SignificantGapThreshold)
            return (SearchConfidence.High, $"Сильное совпадение (sim={bestScore:F2}, gap={gap:F2})");

        // Medium: best >= 0.4 OR (best >= 0.35 AND gap >= 0.03)
        if (bestScore >= MediumConfidenceThreshold)
            return (SearchConfidence.Medium, $"Среднее совпадение (sim={bestScore:F2})");

        if (bestScore >= LowConfidenceThreshold && gap >= SmallGapThreshold)
            return (SearchConfidence.Medium, $"Есть выделяющийся результат (sim={bestScore:F2}, gap={gap:F2})");

        // Low: best >= 0.25
        if (bestScore >= MinConfidenceThreshold)
            return (SearchConfidence.Low, $"Слабое совпадение (sim={bestScore:F2})");

        // None: best < 0.25
        return (SearchConfidence.None, $"Нет релевантных совпадений (best sim={bestScore:F2})");
    }

    /// <summary>
    /// Calculate score gap between best and 5th result
    /// </summary>
    public double CalculateGap(List<SearchResult> results)
    {
        if (results.Count == 0)
            return 0;

        var best = results[0].Similarity;
        var fifth = results.Count >= 5 ? results[4].Similarity : results.Last().Similarity;
        return best - fifth;
    }

    /// <summary>
    /// Apply adjustments to search results: news dump penalty.
    /// Recency boost is now applied in SQL via time decay formula.
    /// Modifies the Similarity property of each result in-place.
    /// </summary>
    /// <param name="results">List of search results to adjust</param>
    public void ApplyAdjustments(List<SearchResult> results)
    {
        foreach (var r in results)
        {
            // News dump penalty
            if (r.IsNewsDump)
            {
                r.Similarity -= SearchConstants.NewsDumpPenalty;
            }
        }
    }

    /// <summary>
    /// Apply adjustments and re-sort results by similarity (descending), then by date.
    /// </summary>
    /// <param name="results">List of search results to adjust and sort</param>
    /// <returns>Sorted list of adjusted results</returns>
    public List<SearchResult> ApplyAdjustmentsAndSort(List<SearchResult> results)
    {
        ApplyAdjustments(results);

        return results
            .OrderByDescending(r => r.Similarity)
            .ThenByDescending(r => MetadataParser.ParseTimestamp(r.MetadataJson))
            .ToList();
    }
}
