using CrossIndexQuery.Core.Retrieval;

namespace CrossIndexQuery.Core.Fusion;

/// <summary>A document after fusion, with the score that placed it and why.</summary>
/// <param name="Source">The winning per-stripe hit, carrying the original document and scores.</param>
/// <param name="FusedScore">Score assigned by the fusion strategy. Comparable only within one strategy.</param>
/// <param name="Explanation">Short human-readable account of how the score was derived.</param>
public sealed record FusedDocument(ScoredDocument Source, double FusedScore, string Explanation)
{
    public string Id => Source.Id;

    public string SourceIndex => Source.SourceIndex;
}

/// <summary>
/// Everything a strategy might need beyond the results themselves.
/// </summary>
/// <param name="TopK">Final result count to return.</param>
/// <param name="QueryVector">Query embedding, when the query had one.</param>
/// <param name="Statistics">
/// Global corpus statistics, when a sidecar has been built. Absent for strategies that must work
/// without one.
/// </param>
public sealed record FusionContext(
    int TopK,
    ReadOnlyMemory<float>? QueryVector = null,
    object? Statistics = null);

/// <summary>
/// Combines per-index result lists into one ranked list.
/// </summary>
/// <remarks>
/// <para>
/// Every strategy in this sample answers the same question — given two lists that were ranked by
/// two different yardsticks, what single order is closest to the order a single index would have
/// produced? They differ in what they assume about those yardsticks:
/// </para>
/// <list type="bullet">
///   <item><description>that the scores are directly comparable (they usually are not);</description></item>
///   <item><description>that only the ranks are trustworthy;</description></item>
///   <item><description>that the scores are comparable after rescaling;</description></item>
///   <item><description>that the missing corpus statistics can be recovered or supplied;</description></item>
///   <item><description>that a corpus-independent scorer can be applied over the union.</description></item>
/// </list>
/// <para>
/// The assumptions get progressively more expensive and progressively more correct, and the
/// evaluation harness exists to show where on that curve each one lands for your data.
/// </para>
/// </remarks>
public interface IFusionStrategy
{
    /// <summary>Stable identifier used on the command line and in results files.</summary>
    string Name { get; }

    /// <summary>One-line summary of the assumption this strategy makes.</summary>
    string Description { get; }

    /// <summary>Whether this strategy can operate on results from the given retrieval mode.</summary>
    bool Supports(RetrievalMode mode);

    /// <summary>
    /// Whether this strategy scores documents with the semantic reranker.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The harness measures a strategy by comparing its order against the single-index oracle's
    /// order for the same query. That comparison only isolates the cost of striping while both
    /// sides rank by the same function. A strategy that reranks changes the function, so scoring it
    /// against a BM25 oracle measures the difference between BM25 and a cross-encoder — which is
    /// large, expected, and nothing to do with striping.
    /// </para>
    /// <para>
    /// Strategies that declare this run only when the fan-out was itself issued with the semantic
    /// ranker, so the oracle is reranked too. It does not restrict use outside the harness: the
    /// <c>query</c> command applies any strategy to any run, because there is no baseline there to
    /// be inconsistent with.
    /// </para>
    /// </remarks>
    bool RequiresSemanticRanker => false;

    /// <summary>
    /// Whether this strategy performs its own retrieval instead of consuming the fan-out.
    /// </summary>
    /// <remarks>
    /// The harness charges a strategy the cost of the fan-out plus whatever it spent on its own
    /// account, which is right for everything that merges a fan-out it was given. A strategy that
    /// retrieves for itself never used that fan-out, and billing it for both would overstate its
    /// cost by exactly the work it declined. Since measured cost is a headline claim, that
    /// distinction is load-bearing rather than cosmetic.
    /// </remarks>
    bool PerformsOwnRetrieval => false;

    ValueTask<IReadOnlyList<FusedDocument>> FuseAsync(
        FanOutResult fanOut,
        FusionContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>Shared helpers for strategies that produce a score per document and then sort.</summary>
public static class FusionHelpers
{
    /// <summary>
    /// Collapses duplicates and orders by score.
    /// </summary>
    /// <remarks>
    /// Stripes are disjoint, so a document should appear once. The de-duplication is defensive:
    /// it keeps the fused list honest if someone points the sample at overlapping indexes, which
    /// is a legitimate topology this code should not silently mis-rank.
    /// </remarks>
    public static IReadOnlyList<FusedDocument> RankAndTruncate(
        IEnumerable<FusedDocument> scored,
        int topK)
    {
        Dictionary<string, FusedDocument> best = new(StringComparer.Ordinal);

        foreach (FusedDocument candidate in scored)
        {
            if (!best.TryGetValue(candidate.Id, out FusedDocument? existing)
                || candidate.FusedScore > existing.FusedScore)
            {
                best[candidate.Id] = candidate;
            }
        }

        return
        [
            .. best.Values
                .OrderByDescending(d => d.FusedScore)
                // Ties broken by document id so results are reproducible across runs.
                .ThenBy(d => d.Id, StringComparer.Ordinal)
                .Take(topK)
        ];
    }
}
