namespace WatchmenBot.Features.Search;

/// <summary>
/// Shared constants for hybrid search across all search services.
/// Centralizes scoring weights, time decay, and boost values.
/// </summary>
public static class SearchConstants
{
    #region Hybrid Search Weights

    /// <summary>
    /// Weight for semantic (vector) similarity component.
    /// Combined with SparseWeight should equal 1.0.
    /// </summary>
    public const double DenseWeight = 0.5;

    /// <summary>
    /// Weight for keyword (BM25/ts_rank) component.
    /// Combined with DenseWeight should equal 1.0.
    /// </summary>
    public const double SparseWeight = 0.5;

    #endregion

    #region Time Decay

    /// <summary>
    /// Half-life for time decay in days.
    /// After this many days, a message gets 50% of the recency boost.
    /// </summary>
    public const double TimeDecayHalfLifeDays = 30.0;

    /// <summary>
    /// Maximum boost for fresh messages (0-1 scale).
    /// Applied via exponential decay formula in SQL.
    /// </summary>
    public const double TimeDecayWeight = 0.1;

    #endregion

    #region Exact Match Boost

    /// <summary>
    /// Boost applied when query words appear exactly in text.
    /// Helps with slang, profanity, and specific terms that embeddings may miss.
    /// </summary>
    public const double ExactMatchBoost = 0.15;

    #endregion

    #region Penalties

    /// <summary>
    /// Penalty applied to news dump messages (long copy-pasted news articles).
    /// These are less likely to be relevant answers.
    /// </summary>
    public const double NewsDumpPenalty = 0.05;

    #endregion
}
