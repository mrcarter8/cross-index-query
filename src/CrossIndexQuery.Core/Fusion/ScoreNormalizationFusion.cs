using CrossIndexQuery.Core.Retrieval;

namespace CrossIndexQuery.Core.Fusion;

/// <summary>
/// Rescales each stripe's scores to <c>[0,1]</c> using that stripe's own minimum and maximum,
/// then merges.
/// </summary>
/// <remarks>
/// <para>
/// This is the most commonly proposed fix for incomparable scores, and it is a trap. The
/// transformation is defined relative to the results the stripe happened to return, so the best
/// hit in <em>every</em> stripe normalizes to exactly 1.0 — including a stripe whose best hit is
/// irrelevant. Rather than correcting the imbalance it manufactures a tie at the top of the list
/// between a strong result and a weak one, then breaks that tie arbitrarily.
/// </para>
/// <para>
/// It gets worse as the stripes become more different, which is precisely the situation it is
/// reached for. A genre-striped corpus queried for something only one stripe covers is the
/// worst case: the strategy is guaranteed to promote the other stripe's best irrelevant document
/// into the top few positions.
/// </para>
/// <para>
/// It ships here so the harness can demonstrate the failure with numbers instead of arguing about
/// it, which is more persuasive than leaving it out.
/// </para>
/// </remarks>
public sealed class MinMaxNormalizationFusion : IFusionStrategy
{
    public string Name => "minmax-norm";

    public string Description =>
        "Rescale each index's scores to [0,1] independently. Included to demonstrate why this fails.";

    public bool Supports(RetrievalMode mode) => true;

    public ValueTask<IReadOnlyList<FusedDocument>> FuseAsync(
        FanOutResult fanOut, FusionContext context, CancellationToken cancellationToken = default)
    {
        List<FusedDocument> scored = [];

        foreach (StripeResultSet stripe in fanOut.Stripes)
        {
            if (stripe.Documents.Count == 0)
            {
                continue;
            }

            double min = stripe.Documents.Min(d => d.Score);
            double max = stripe.Documents.Max(d => d.Score);
            double range = max - min;

            foreach (ScoredDocument doc in stripe.Documents)
            {
                double normalized = range > double.Epsilon ? (doc.Score - min) / range : 1d;
                scored.Add(new FusedDocument(
                    doc,
                    normalized,
                    $"({doc.Score:F4}-{min:F4})/({max:F4}-{min:F4}) = {normalized:F4} in {doc.SourceIndex}"));
            }
        }

        return ValueTask.FromResult(FusionHelpers.RankAndTruncate(scored, context.TopK));
    }
}

/// <summary>
/// Standardizes each stripe's scores to zero mean and unit variance before merging.
/// </summary>
/// <remarks>
/// <para>
/// A more defensible relative of min-max normalization. Because it measures a document against the
/// spread of its own stripe rather than against that stripe's extremes, a stripe whose results are
/// uniformly mediocre produces z-scores clustered near zero instead of a spurious 1.0 — the
/// pathology that makes min-max unusable survives only in weakened form.
/// </para>
/// <para>
/// The remaining flaw is structural and cannot be fixed by rescaling: a z-score says how unusual a
/// document is <em>within its own stripe</em>, not how relevant it is. A stripe containing nothing
/// on topic still has a most-unusual member, and that document still receives a high z-score.
/// Expect it to beat min-max comfortably and still trail any strategy that uses a corpus-independent
/// signal.
/// </para>
/// </remarks>
public sealed class ZScoreNormalizationFusion : IFusionStrategy
{
    public string Name => "zscore-norm";

    public string Description =>
        "Standardize each index's scores to zero mean and unit variance, then merge.";

    public bool Supports(RetrievalMode mode) => true;

    public ValueTask<IReadOnlyList<FusedDocument>> FuseAsync(
        FanOutResult fanOut, FusionContext context, CancellationToken cancellationToken = default)
    {
        List<FusedDocument> scored = [];

        foreach (StripeResultSet stripe in fanOut.Stripes)
        {
            if (stripe.Documents.Count == 0)
            {
                continue;
            }

            double mean = stripe.Documents.Average(d => d.Score);
            double variance = stripe.Documents.Sum(d => Math.Pow(d.Score - mean, 2)) / stripe.Documents.Count;
            double stdDev = Math.Sqrt(variance);

            foreach (ScoredDocument doc in stripe.Documents)
            {
                double z = stdDev > double.Epsilon ? (doc.Score - mean) / stdDev : 0d;
                scored.Add(new FusedDocument(
                    doc,
                    z,
                    $"z=({doc.Score:F4}-{mean:F4})/{stdDev:F4} = {z:F4} in {doc.SourceIndex}"));
            }
        }

        return ValueTask.FromResult(FusionHelpers.RankAndTruncate(scored, context.TopK));
    }
}

/// <summary>
/// Merges on raw cosine similarity, taken from the vector leg's debug subscores.
/// </summary>
/// <remarks>
/// <para>
/// For vector and hybrid retrieval this is the strategy to reach for first, and it is close to
/// free. Cosine similarity is a function of two vectors and nothing else — no document frequency,
/// no average document length, no corpus at all. A similarity of 0.82 means the same thing in
/// every index that shares an embedding model, so the union can simply be sorted.
/// </para>
/// <para>
/// The similarity is read from the per-document subscores that Azure AI Search returns when vector
/// debug information is requested, so no additional query is needed even for hybrid, where the
/// headline score is an RRF value that has already discarded it.
/// </para>
/// <para>
/// The one precondition is absolute: every index must have been populated with the same embedding
/// model at the same dimensionality. Mixed embedding spaces produce plausible-looking similarities
/// drawn from unrelated geometries, which is why the sample embeds client-side and records the
/// model in the corpus manifest.
/// </para>
/// </remarks>
public sealed class VectorSimilarityFusion : IFusionStrategy
{
    public string Name => "vector-similarity";

    public string Description =>
        "Sort the union by raw cosine similarity. Corpus-independent, so it is genuinely comparable.";

    public bool Supports(RetrievalMode mode) => mode is RetrievalMode.Vector or RetrievalMode.Hybrid;

    public ValueTask<IReadOnlyList<FusedDocument>> FuseAsync(
        FanOutResult fanOut, FusionContext context, CancellationToken cancellationToken = default)
    {
        List<FusedDocument> scored = [];

        foreach (ScoredDocument doc in fanOut.AllDocuments)
        {
            // Pure vector queries expose similarity directly through @search.score, which is a
            // monotone transform of cosine; hybrid queries only expose it through the subscores.
            double? similarity = doc.VectorSimilarity
                ?? (fanOut.Mode == RetrievalMode.Vector ? doc.Score : null);

            if (similarity is not { } value)
            {
                continue;
            }

            scored.Add(new FusedDocument(
                doc, value, $"cosine={value:F4} from {doc.SourceIndex}"));
        }

        return ValueTask.FromResult(FusionHelpers.RankAndTruncate(scored, context.TopK));
    }
}

/// <summary>
/// Combines the two hybrid legs across all stripes: rank the pooled text leg and the pooled vector
/// leg globally, then apply Reciprocal Rank Fusion to those global ranks.
/// </summary>
/// <remarks>
/// <para>
/// This reconstructs, across indexes, what Azure AI Search does inside one. A hybrid query fuses
/// its text and vector legs by rank, but those ranks are local to the index, so the resulting
/// score cannot be compared with another index's. Splitting the legs apart again, pooling each one
/// across every stripe, and re-ranking produces exactly the global ranks a single index would have
/// used — and RRF over those is the same computation the service would have performed.
/// </para>
/// <para>
/// It is the most faithful reconstruction available without a single index, and the debug
/// subscores make it cost nothing extra: both legs arrive on the same response that a normal
/// hybrid query already returns.
/// </para>
/// <para>
/// The text leg's global pooling is still an approximation, since each stripe's BM25 came from its
/// own statistics. Ranking rather than scoring limits the damage — an ordering is wrong only where
/// the statistical distortion was large enough to reorder two documents, not everywhere the scores
/// differ.
/// </para>
/// </remarks>
public sealed class HybridLegFusion(int k = 60) : IFusionStrategy
{
    public string Name => "hybrid-legs";

    public string Description =>
        $"Pool the text and vector legs across indexes, re-rank each globally, then RRF (k={k}).";

    public bool Supports(RetrievalMode mode) => mode == RetrievalMode.Hybrid;

    public ValueTask<IReadOnlyList<FusedDocument>> FuseAsync(
        FanOutResult fanOut, FusionContext context, CancellationToken cancellationToken = default)
    {
        List<ScoredDocument> all = [.. fanOut.AllDocuments];

        Dictionary<string, int> textRanks = GlobalRanks(all, d => d.TextScore);
        Dictionary<string, int> vectorRanks = GlobalRanks(all, d => d.VectorSimilarity);

        List<FusedDocument> scored = [];

        foreach (ScoredDocument doc in all)
        {
            double score = 0;
            List<string> parts = [];

            if (textRanks.TryGetValue(doc.Id, out int textRank))
            {
                score += 1d / (k + textRank);
                parts.Add($"text#{textRank}");
            }

            if (vectorRanks.TryGetValue(doc.Id, out int vectorRank))
            {
                score += 1d / (k + vectorRank);
                parts.Add($"vector#{vectorRank}");
            }

            if (parts.Count == 0)
            {
                continue;
            }

            scored.Add(new FusedDocument(
                doc, score, $"{string.Join(" + ", parts)} = {score:F6} from {doc.SourceIndex}"));
        }

        return ValueTask.FromResult(FusionHelpers.RankAndTruncate(scored, context.TopK));
    }

    /// <summary>Ranks every document that has a value for one leg, best first, 1-based.</summary>
    private static Dictionary<string, int> GlobalRanks(
        IReadOnlyList<ScoredDocument> documents,
        Func<ScoredDocument, double?> selector)
    {
        Dictionary<string, int> ranks = new(StringComparer.Ordinal);
        int rank = 0;

        foreach (ScoredDocument doc in documents
            .Where(d => selector(d) is not null)
            .OrderByDescending(d => selector(d)!.Value)
            .ThenBy(d => d.Id, StringComparer.Ordinal))
        {
            ranks[doc.Id] = ++rank;
        }

        return ranks;
    }
}
