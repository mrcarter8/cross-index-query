using CrossIndexQuery.Core.Models;

namespace CrossIndexQuery.Core.Retrieval;

/// <summary>Which retrieval signal a query uses.</summary>
public enum RetrievalMode
{
    /// <summary>BM25 over the analyzed text fields. Scores depend on per-index corpus statistics.</summary>
    Keyword,

    /// <summary>Approximate nearest neighbour over the embedding. Scores are corpus-independent.</summary>
    Vector,

    /// <summary>Both legs, fused by the service with Reciprocal Rank Fusion.</summary>
    Hybrid,
}

/// <summary>
/// One document returned by one index, with every score the service was willing to disclose.
/// </summary>
/// <param name="Document">The document itself.</param>
/// <param name="SourceIndex">Index that returned it. Fusion strategies need this to attribute scores.</param>
/// <param name="Rank">1-based position within that index's result list.</param>
/// <param name="Score">
/// The headline <c>@search.score</c>. Its meaning depends on the mode: BM25 for keyword, a
/// transformed cosine for vector, and an RRF score for hybrid. Only the vector form is comparable
/// across indexes, which is the entire problem this sample exists to work around.
/// </param>
/// <param name="TextScore">
/// Raw BM25 for the text leg of a hybrid query, from the debug subscores. Available without
/// issuing a second query, which makes leg decomposition essentially free.
/// </param>
/// <param name="VectorSimilarity">
/// Raw cosine similarity for the vector leg. A property of the query and document vectors alone,
/// so it means the same thing in every index — the one score that can be merged naively and be right.
/// </param>
/// <param name="RerankerScore">
/// Semantic reranker score, 0-4. Produced by a cross-encoder that reads the query and the document
/// together and consults no corpus statistics, so it is also safe to compare across indexes.
/// </param>
public sealed record ScoredDocument(
    BookDocument Document,
    string SourceIndex,
    int Rank,
    double Score,
    double? TextScore = null,
    double? VectorSimilarity = null,
    double? RerankerScore = null)
{
    public string Id => Document.Id;
}

/// <summary>Everything one index returned for one query, plus what it cost.</summary>
public sealed record StripeResultSet(
    string IndexName,
    RetrievalMode Mode,
    IReadOnlyList<ScoredDocument> Documents,
    long? TotalCount,
    TimeSpan Elapsed,
    double ComputeUnits)
{
    public static StripeResultSet Empty(string indexName, RetrievalMode mode) =>
        new(indexName, mode, [], 0, TimeSpan.Zero, 0);
}

/// <summary>The full fan-out for one query: one result set per stripe.</summary>
public sealed record FanOutResult(
    string Query,
    RetrievalMode Mode,
    IReadOnlyList<StripeResultSet> Stripes,
    IReadOnlyList<StripeFailure> Failures,
    TimeSpan WallClock)
{
    /// <summary>
    /// Wall-clock cost of the fan-out, measured around the concurrent dispatch rather than summed.
    /// </summary>
    /// <remarks>
    /// This is what the user waits for, and it is close to the slowest stripe rather than the total
    /// of both — the practical argument that striping need not feel slower than a single index.
    /// </remarks>
    public TimeSpan Elapsed => WallClock;

    /// <summary>Billable cost of the fan-out, which <em>is</em> the sum — every stripe is charged.</summary>
    public double ComputeUnits => Stripes.Sum(s => s.ComputeUnits);

    /// <summary>Requests issued, which is what a per-operation bill counts.</summary>
    public int QueryCount => Stripes.Count;

    public IEnumerable<ScoredDocument> AllDocuments => Stripes.SelectMany(s => s.Documents);
}
