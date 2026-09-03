using CrossIndexQuery.Core.Retrieval;

namespace CrossIndexQuery.Core.Fusion;

/// <summary>
/// Sorts the union by raw <c>@search.score</c>, exactly as the original multiple-search-services
/// sample does. This is the control condition, not a recommendation.
/// </summary>
/// <remarks>
/// <para>
/// The assumption is that a score of 8.2 from one index means the same thing as a score of 8.2
/// from another. For BM25 that is false whenever the two indexes hold different vocabulary,
/// because the inverse document frequency of every term and the average document length are both
/// computed per index. A term that is rare in one stripe and common in the other produces
/// systematically higher scores in the first, so its documents win positions they have not earned.
/// </para>
/// <para>
/// For hybrid it is worse. The score is a Reciprocal Rank Fusion value derived purely from ranks
/// within each index, so the top hit of a stripe containing nothing relevant scores identically to
/// the top hit of the stripe containing every relevant document.
/// </para>
/// <para>
/// It is included because it is what most people write first, and because the harness needs to put
/// a number on how much it costs.
/// </para>
/// </remarks>
public sealed class NaiveScoreFusion : IFusionStrategy
{
    public string Name => "naive-score";

    public string Description => "Sort the union by raw @search.score. The control: assumes scores are comparable.";

    public bool Supports(RetrievalMode mode) => true;

    public ValueTask<IReadOnlyList<FusedDocument>> FuseAsync(
        FanOutResult fanOut, FusionContext context, CancellationToken cancellationToken = default)
    {
        IEnumerable<FusedDocument> scored = fanOut.AllDocuments.Select(d =>
            new FusedDocument(d, d.Score, $"@search.score={d.Score:F4} from {d.SourceIndex}"));

        return ValueTask.FromResult(FusionHelpers.RankAndTruncate(scored, context.TopK));
    }
}

/// <summary>
/// Round-robins the stripes by rank: first hit of A, first hit of B, second of A, and so on.
/// </summary>
/// <remarks>
/// <para>
/// This throws the scores away entirely and trusts only each index's internal ordering, which is
/// the one thing that is always meaningful. That makes it immune to the score-comparability
/// problem, and it is a surprisingly strong baseline when both stripes are equally relevant.
/// </para>
/// <para>
/// Its weakness is the mirror image of its strength: it assumes the stripes contribute equally.
/// Ask a question that only one stripe can answer and it still awards half the positions to the
/// other, so every second result is filler.
/// </para>
/// </remarks>
public sealed class InterleaveFusion : IFusionStrategy
{
    public string Name => "interleave";

    public string Description => "Round-robin by rank. Ignores scores; assumes stripes contribute equally.";

    public bool Supports(RetrievalMode mode) => true;

    public ValueTask<IReadOnlyList<FusedDocument>> FuseAsync(
        FanOutResult fanOut, FusionContext context, CancellationToken cancellationToken = default)
    {
        List<FusedDocument> scored = [];

        for (int stripeIndex = 0; stripeIndex < fanOut.Stripes.Count; stripeIndex++)
        {
            StripeResultSet stripe = fanOut.Stripes[stripeIndex];

            for (int i = 0; i < stripe.Documents.Count; i++)
            {
                ScoredDocument doc = stripe.Documents[i];

                // Negated so lower ranks sort first. The fractional term is a stable tie-break
                // between stripes within the same round and never crosses a round boundary.
                double score = -(i + (stripeIndex / (double)Math.Max(fanOut.Stripes.Count, 1)));
                scored.Add(new FusedDocument(doc, score, $"rank {doc.Rank} in {doc.SourceIndex}"));
            }
        }

        return ValueTask.FromResult(FusionHelpers.RankAndTruncate(scored, context.TopK));
    }
}

/// <summary>
/// Reciprocal Rank Fusion over the per-stripe ranks: <c>score = sum(1 / (k + rank))</c>.
/// </summary>
/// <remarks>
/// <para>
/// The same algorithm Azure AI Search uses internally to combine the text and vector legs of a
/// hybrid query, applied one level up to combine indexes instead. Like interleaving it uses only
/// ranks, so it sidesteps score incomparability, but the reciprocal curve is steep: rank 1 is worth
/// far more than rank 2, and by rank 30 the differences are negligible.
/// </para>
/// <para>
/// That shape is what makes it better than interleaving. A stripe with a genuinely strong top hit
/// wins the top position outright rather than merely alternating, while the long tail of an
/// irrelevant stripe contributes almost nothing. It is the best strategy available when you have
/// no usable scores at all, and it needs no extra requests, no sidecar, and no configuration.
/// </para>
/// <para>
/// The constant <c>k = 60</c> is the value published with the original RRF paper and the value
/// Azure AI Search uses. It is kept configurable but changing it is rarely worthwhile.
/// </para>
/// </remarks>
public sealed class GlobalRrfFusion(int k = 60) : IFusionStrategy
{
    public string Name => "global-rrf";

    public string Description => $"Reciprocal Rank Fusion (k={k}) over per-stripe ranks. Rank-only, no extra cost.";

    public bool Supports(RetrievalMode mode) => true;

    public ValueTask<IReadOnlyList<FusedDocument>> FuseAsync(
        FanOutResult fanOut, FusionContext context, CancellationToken cancellationToken = default)
    {
        IEnumerable<FusedDocument> scored = fanOut.AllDocuments.Select(d =>
        {
            double score = 1d / (k + d.Rank);
            return new FusedDocument(d, score, $"1/({k}+{d.Rank}) = {score:F6} from {d.SourceIndex}");
        });

        return ValueTask.FromResult(FusionHelpers.RankAndTruncate(scored, context.TopK));
    }
}

/// <summary>
/// Allocates slots to each stripe in proportion to its share of the corpus, then fills each
/// allocation from that stripe's own ranking.
/// </summary>
/// <remarks>
/// <para>
/// The reasoning is statistical: if stripe A holds 70% of the documents then, absent other
/// information, roughly 70% of the true top ten should come from it. Quota merging encodes that
/// prior directly, which makes it robust when the stripes are very unevenly sized — the case where
/// interleaving is most obviously wrong.
/// </para>
/// <para>
/// The prior is only as good as its premise. It assumes relevance is spread uniformly across the
/// corpus, so a narrow query whose answers all sit in the smaller stripe is capped at that
/// stripe's quota no matter how good its results are. Useful as a component or a tie-breaker;
/// risky on its own.
/// </para>
/// </remarks>
public sealed class QuotaMergeFusion(IReadOnlyDictionary<string, int> documentCounts) : IFusionStrategy
{
    public string Name => "quota-merge";

    public string Description => "Allocate slots proportional to index size, then fill from each index's own ranking.";

    public bool Supports(RetrievalMode mode) => true;

    public ValueTask<IReadOnlyList<FusedDocument>> FuseAsync(
        FanOutResult fanOut, FusionContext context, CancellationToken cancellationToken = default)
    {
        long total = fanOut.Stripes.Sum(s => (long)Count(s.IndexName));
        if (total == 0)
        {
            return ValueTask.FromResult<IReadOnlyList<FusedDocument>>([]);
        }

        List<FusedDocument> merged = [];

        foreach (StripeResultSet stripe in fanOut.Stripes)
        {
            double share = Count(stripe.IndexName) / (double)total;
            int quota = Math.Max(1, (int)Math.Round(context.TopK * share));

            foreach (ScoredDocument doc in stripe.Documents.Take(quota))
            {
                merged.Add(new FusedDocument(
                    doc,
                    -doc.Rank,
                    $"quota {quota} ({share:P0} of corpus), rank {doc.Rank} in {doc.SourceIndex}"));
            }
        }

        return ValueTask.FromResult(FusionHelpers.RankAndTruncate(merged, context.TopK));
    }

    private int Count(string indexName) =>
        documentCounts.TryGetValue(indexName, out int value) ? value : 0;
}
