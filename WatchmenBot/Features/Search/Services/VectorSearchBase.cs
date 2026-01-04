using Dapper;
using WatchmenBot.Infrastructure.Database;

namespace WatchmenBot.Features.Search.Services;

/// <summary>
/// Base class for vector search services providing common hybrid search functionality.
/// Implements Template Method pattern for SQL generation and search execution.
/// </summary>
public abstract class VectorSearchBase
{
    protected readonly EmbeddingClient EmbeddingClient;
    protected readonly IDbConnectionFactory ConnectionFactory;

    protected VectorSearchBase(
        EmbeddingClient embeddingClient,
        IDbConnectionFactory connectionFactory)
    {
        EmbeddingClient = embeddingClient;
        ConnectionFactory = connectionFactory;
    }

    #region SQL Building - Static Helpers

    /// <summary>
    /// Build the similarity calculation SQL expression.
    /// Uses hybrid scoring: dense (vector) + sparse (BM25) + exact match + time decay.
    /// </summary>
    /// <param name="embeddingColumn">Column containing the embedding vector (e.g., "me.embedding")</param>
    /// <param name="textColumn">Column containing text for BM25 (e.g., "me.chunk_text")</param>
    /// <param name="dateColumn">Column containing date for time decay (e.g., "m.date_utc")</param>
    /// <param name="exactMatchSql">Pre-built exact match SQL expression</param>
    /// <param name="useHybrid">Whether to include BM25 component</param>
    public static string BuildSimilaritySql(
        string embeddingColumn,
        string textColumn,
        string dateColumn,
        string exactMatchSql,
        bool useHybrid)
    {
        var timeDecaySql = $"{SearchConstants.TimeDecayWeight} * EXP(-GREATEST(0, EXTRACT(EPOCH FROM (NOW() - {dateColumn})) / 86400.0) * LN(2) / {SearchConstants.TimeDecayHalfLifeDays})";

        if (useHybrid)
        {
            return $"""
                {SearchConstants.DenseWeight} * (1 - ({embeddingColumn} <=> @Embedding::vector))
                + {SearchConstants.SparseWeight} * COALESCE(
                    ts_rank_cd(
                        to_tsvector('russian', {textColumn}),
                        websearch_to_tsquery('russian', @SearchTerms),
                        32
                    ),
                    0
                )
                + {exactMatchSql}
                + {timeDecaySql}
                """;
        }

        return $"""
            (1 - ({embeddingColumn} <=> @Embedding::vector))
            + {exactMatchSql}
            + {timeDecaySql}
            """;
    }

    /// <summary>
    /// Build exact match boost SQL expression.
    /// Returns "0" if no words to match.
    /// </summary>
    /// <param name="textColumn">Column to search in (e.g., "me.chunk_text")</param>
    /// <param name="exactMatchWords">Words to match exactly</param>
    public static string BuildExactMatchSql(string textColumn, List<string> exactMatchWords)
    {
        if (exactMatchWords.Count == 0)
            return "0";

        var conditions = exactMatchWords
            .Select((_, i) => $"LOWER({textColumn}) LIKE @ExactWord{i}")
            .ToList();

        return $"CASE WHEN {string.Join(" OR ", conditions)} THEN {SearchConstants.ExactMatchBoost} ELSE 0 END";
    }

    /// <summary>
    /// Build common search parameters.
    /// </summary>
    public static DynamicParameters BuildSearchParameters(
        long chatId,
        float[] embedding,
        string? searchTerms,
        List<string> exactMatchWords,
        int limit)
    {
        var parameters = new DynamicParameters();
        parameters.Add("ChatId", chatId);
        parameters.Add("Embedding", "[" + string.Join(",", embedding) + "]");
        parameters.Add("SearchTerms", searchTerms);
        parameters.Add("Limit", limit);

        for (var i = 0; i < exactMatchWords.Count; i++)
        {
            parameters.Add($"ExactWord{i}", $"%{exactMatchWords[i].ToLowerInvariant()}%");
        }

        return parameters;
    }

    /// <summary>
    /// Extract search terms and exact match words from query.
    /// </summary>
    public static (string? searchTerms, List<string> exactMatchWords, bool useHybrid) ParseQuery(string? queryText)
    {
        if (string.IsNullOrWhiteSpace(queryText))
            return (null, [], false);

        var searchTerms = TextSearchHelpers.ExtractSearchTerms(queryText);
        var exactMatchWords = TextSearchHelpers.ExtractIlikeWords(queryText);
        var useHybrid = !string.IsNullOrWhiteSpace(searchTerms);

        return (searchTerms, exactMatchWords, useHybrid);
    }

    #endregion
}
