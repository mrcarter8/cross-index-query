using CrossIndexQuery.Core.Fusion;
using CrossIndexQuery.Core.Models;
using CrossIndexQuery.Core.Retrieval;
using Xunit;

namespace CrossIndexQuery.Tests;

/// <summary>
/// Tests the fusion arithmetic against hand-constructed fan-outs.
/// </summary>
/// <remarks>
/// <para>
/// Every strategy here is exercised on a fan-out built to have exactly the property the strategy
/// claims to handle. The interesting cases are the ones where a strategy is <em>supposed</em> to
/// produce a different answer from naive score sorting: those differences are the entire thesis of
/// the sample, so a silent regression that made two strategies agree would be worse than a crash.
/// </para>
/// </remarks>
public sealed class FusionStrategyTests
{
    private const string StripeA = "books-stripe-a";
    private const string StripeB = "books-stripe-b";

    /// <summary>
    /// A fan-out where stripe B's scores are inflated relative to stripe A's.
    /// </summary>
    /// <remarks>
    /// This is the situation the sample exists to address. Stripe B's top document scores 9.0 and
    /// stripe A's scores 3.0, but the ranks say both are their index's best match. Sorting on raw
    /// score therefore hands the whole top of the list to stripe B — not because its documents are
    /// better, but because its corpus statistics produce larger numbers.
    /// </remarks>
    private static FanOutResult SkewedFanOut() => new(
        Query: "test",
        Mode: RetrievalMode.Keyword,
        Stripes:
        [
            new StripeResultSet(StripeA, RetrievalMode.Keyword,
            [
                Doc("a1", StripeA, 1, 3.0),
                Doc("a2", StripeA, 2, 2.0),
                Doc("a3", StripeA, 3, 1.0),
            ], 3, TimeSpan.FromMilliseconds(10), 0.5),

            new StripeResultSet(StripeB, RetrievalMode.Keyword,
            [
                Doc("b1", StripeB, 1, 9.0),
                Doc("b2", StripeB, 2, 8.0),
                Doc("b3", StripeB, 3, 7.0),
            ], 3, TimeSpan.FromMilliseconds(12), 0.5),
        ],
        Failures: [],
        WallClock: TimeSpan.FromMilliseconds(14));

    private static ScoredDocument Doc(
        string id,
        string index,
        int rank,
        double score,
        double? textScore = null,
        double? similarity = null,
        double? reranker = null) =>
        new(new BookDocument { Id = id, Title = id }, index, rank, score, textScore, similarity, reranker);

    private static async Task<IReadOnlyList<FusedDocument>> FuseAsync(
        IFusionStrategy strategy,
        FanOutResult fanOut,
        int topK = 6) =>
        await strategy.FuseAsync(fanOut, new FusionContext(topK, null), TestContext.Current.CancellationToken);

    /// <summary>
    /// Demonstrates the failure the sample is about, as an executable claim rather than prose.
    /// </summary>
    [Fact]
    public async Task NaiveScoreFusion_LetsTheHigherScoringStripeMonopoliseTheTop()
    {
        IReadOnlyList<FusedDocument> fused = await FuseAsync(new NaiveScoreFusion(), SkewedFanOut());

        Assert.Equal(["b1", "b2", "b3", "a1", "a2", "a3"], fused.Select(d => d.Id));
    }

    [Fact]
    public async Task InterleaveFusion_AlternatesBetweenStripesRegardlessOfScore()
    {
        IReadOnlyList<FusedDocument> fused = await FuseAsync(new InterleaveFusion(), SkewedFanOut());

        // Round-robin by rank: each stripe contributes its rank-1 document, then its rank-2, and so
        // on. Which stripe leads is arbitrary, so only the pairing is asserted.
        Assert.Equal(["a1", "b1"], fused.Take(2).Select(d => d.Id).Order());
        Assert.Equal(["a2", "b2"], fused.Skip(2).Take(2).Select(d => d.Id).Order());
        Assert.Equal(["a3", "b3"], fused.Skip(4).Take(2).Select(d => d.Id).Order());
    }

    /// <summary>
    /// Reciprocal rank fusion discards score magnitude entirely, which is the point.
    /// </summary>
    /// <remarks>
    /// Because it reads only ranks, it is immune to the scale difference between the stripes. That
    /// immunity is also its cost: it cannot tell a stripe that genuinely holds better documents
    /// from one that does not, so it splits the top of the list evenly no matter how lopsided the
    /// true relevance is.
    /// </remarks>
    [Fact]
    public async Task GlobalRrfFusion_IgnoresScoreMagnitudeAndPairsEqualRanks()
    {
        IReadOnlyList<FusedDocument> fused = await FuseAsync(new GlobalRrfFusion(), SkewedFanOut());

        Assert.Equal(["a1", "b1"], fused.Take(2).Select(d => d.Id).Order());

        // Equal ranks in different stripes must receive identical fused scores.
        Assert.Equal(fused[0].FusedScore, fused[1].FusedScore, precision: 12);
    }

    [Fact]
    public async Task GlobalRrfFusion_ScoresFollowTheKnownFormula()
    {
        IReadOnlyList<FusedDocument> fused = await FuseAsync(new GlobalRrfFusion(k: 60), SkewedFanOut());

        // Each document appears in exactly one stripe, so its fused score is a single 1/(k+rank).
        FusedDocument first = fused.First(d => d.Id == "a1");
        Assert.Equal(1.0 / 61, first.FusedScore, precision: 12);

        FusedDocument third = fused.First(d => d.Id == "a3");
        Assert.Equal(1.0 / 63, third.FusedScore, precision: 12);
    }

    /// <summary>
    /// Min-max normalisation removes the scale difference the naive strategy fell for.
    /// </summary>
    /// <remarks>
    /// It rescales each stripe's scores onto [0, 1] independently, so the top of each stripe maps to
    /// 1 and the bottom to 0. That fixes the monopoly, but it does so by <em>assuming</em> both
    /// stripes' best documents are equally good — which is exactly as unfounded as assuming their
    /// raw scores were comparable. The sample includes it to show that a plausible fix can smuggle
    /// in an equally strong assumption.
    /// </remarks>
    [Fact]
    public async Task MinMaxNormalizationFusion_MapsEachStripeOntoTheSameRange()
    {
        IReadOnlyList<FusedDocument> fused = await FuseAsync(new MinMaxNormalizationFusion(), SkewedFanOut());

        Assert.Equal(["a1", "b1"], fused.Take(2).Select(d => d.Id).Order());
        Assert.Equal(1.0, fused[0].FusedScore, precision: 9);
        Assert.Equal(1.0, fused[1].FusedScore, precision: 9);

        Assert.Equal(["a3", "b3"], fused.TakeLast(2).Select(d => d.Id).Order());
        Assert.Equal(0.0, fused[^1].FusedScore, precision: 9);
    }

    /// <summary>
    /// A stripe whose scores are all identical must not produce a division by zero.
    /// </summary>
    /// <remarks>
    /// This is not a contrived case: a filtered query that matches a handful of documents equally
    /// well produces exactly this, and an unguarded min-max would emit NaN and silently sort the
    /// whole stripe to the bottom.
    /// </remarks>
    [Fact]
    public async Task MinMaxNormalizationFusion_HandlesAStripeWithNoScoreVariance()
    {
        var fanOut = new FanOutResult("test", RetrievalMode.Keyword,
        [
            new StripeResultSet(StripeA, RetrievalMode.Keyword,
                [Doc("a1", StripeA, 1, 5.0), Doc("a2", StripeA, 2, 5.0)],
                2, TimeSpan.Zero, 0),
            new StripeResultSet(StripeB, RetrievalMode.Keyword,
                [Doc("b1", StripeB, 1, 9.0), Doc("b2", StripeB, 2, 1.0)],
                2, TimeSpan.Zero, 0),
        ], [], TimeSpan.Zero);

        IReadOnlyList<FusedDocument> fused = await FuseAsync(new MinMaxNormalizationFusion(), fanOut);

        Assert.Equal(4, fused.Count);
        Assert.All(fused, d => Assert.False(double.IsNaN(d.FusedScore)));
    }

    /// <summary>
    /// Cosine similarity is already comparable across indexes, so no correction is required.
    /// </summary>
    /// <remarks>
    /// This is the one genuinely easy case in the sample. Vector scores are coordinates in a shared
    /// embedding space rather than statistics computed against a corpus, so a document's similarity
    /// does not depend on which index it happens to live in. Sorting on it directly is correct.
    /// </remarks>
    [Fact]
    public async Task VectorSimilarityFusion_SortsOnRawCosineAcrossStripes()
    {
        var fanOut = new FanOutResult("test", RetrievalMode.Vector,
        [
            new StripeResultSet(StripeA, RetrievalMode.Vector,
                [Doc("a1", StripeA, 1, 0.9, similarity: 0.91), Doc("a2", StripeA, 2, 0.8, similarity: 0.72)],
                2, TimeSpan.Zero, 0),
            new StripeResultSet(StripeB, RetrievalMode.Vector,
                [Doc("b1", StripeB, 1, 0.9, similarity: 0.84), Doc("b2", StripeB, 2, 0.8, similarity: 0.65)],
                2, TimeSpan.Zero, 0),
        ], [], TimeSpan.Zero);

        IReadOnlyList<FusedDocument> fused = await FuseAsync(new VectorSimilarityFusion(), fanOut);

        Assert.Equal(["a1", "b1", "a2", "b2"], fused.Select(d => d.Id));
    }

    /// <summary>
    /// The reranker score is absolute, so it is safe to compare across stripes directly.
    /// </summary>
    /// <remarks>
    /// A cross-encoder scores a query/document pair on its own merits and consults no corpus
    /// statistics, so unlike BM25 its output means the same thing in every index. That makes this
    /// the only strategy in the catalog whose cross-index comparison needs no correction at all.
    /// </remarks>
    [Fact]
    public async Task SemanticScoreFusion_OrdersByRerankerScoreAcrossStripes()
    {
        var fanOut = new FanOutResult("test", RetrievalMode.Keyword,
        [
            new StripeResultSet(StripeA, RetrievalMode.Keyword,
                [Doc("a1", StripeA, 1, 3.0, reranker: 2.10), Doc("a2", StripeA, 2, 2.0, reranker: 1.05)],
                2, TimeSpan.Zero, 0),
            new StripeResultSet(StripeB, RetrievalMode.Keyword,
                [Doc("b1", StripeB, 1, 9.0, reranker: 3.40), Doc("b2", StripeB, 2, 8.0, reranker: 0.90)],
                2, TimeSpan.Zero, 0),
        ], [], TimeSpan.Zero);

        IReadOnlyList<FusedDocument> fused = await FuseAsync(new SemanticScoreFusion(), fanOut);

        Assert.Equal(["b1", "a1", "a2", "b2"], fused.Select(d => d.Id));
    }

    /// <summary>
    /// A strategy must refuse to run rather than invent an answer when its input is missing.
    /// </summary>
    /// <remarks>
    /// Falling back silently would be worse than failing. The harness records a skipped strategy as
    /// absent, but a fallback would record it as a genuine measurement of something it never
    /// actually did, which would then be published as a result.
    /// </remarks>
    [Fact]
    public async Task SemanticScoreFusion_ThrowsWhenTheFanOutCarriesNoRerankerScores()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => FuseAsync(new SemanticScoreFusion(), SkewedFanOut()));
    }

    [Fact]
    public async Task Fusion_TruncatesToTheRequestedResultSize()
    {
        IReadOnlyList<FusedDocument> fused = await FuseAsync(new GlobalRrfFusion(), SkewedFanOut(), topK: 4);

        Assert.Equal(4, fused.Count);
    }

    [Fact]
    public async Task Fusion_ProducesNoDuplicatesWhenAStripeRepeatsADocument()
    {
        // The same document can legitimately appear in both stripes when the corpus is split by a
        // rule that does not perfectly partition, so de-duplication is a correctness requirement.
        var fanOut = new FanOutResult("test", RetrievalMode.Keyword,
        [
            new StripeResultSet(StripeA, RetrievalMode.Keyword,
                [Doc("shared", StripeA, 1, 3.0), Doc("a2", StripeA, 2, 2.0)], 2, TimeSpan.Zero, 0),
            new StripeResultSet(StripeB, RetrievalMode.Keyword,
                [Doc("shared", StripeB, 1, 9.0), Doc("b2", StripeB, 2, 8.0)], 2, TimeSpan.Zero, 0),
        ], [], TimeSpan.Zero);

        foreach (IFusionStrategy strategy in new IFusionStrategy[]
        {
            new NaiveScoreFusion(),
            new InterleaveFusion(),
            new GlobalRrfFusion(),
            new MinMaxNormalizationFusion(),
            new ZScoreNormalizationFusion(),
        })
        {
            IReadOnlyList<FusedDocument> fused = await FuseAsync(strategy, fanOut);

            Assert.Equal(
                fused.Select(d => d.Id).Distinct().Count(),
                fused.Count);
        }
    }

    [Fact]
    public async Task Fusion_ReturnsEmptyWhenEveryStripeReturnedNothing()
    {
        var fanOut = new FanOutResult("test", RetrievalMode.Keyword,
        [
            StripeResultSet.Empty(StripeA, RetrievalMode.Keyword),
            StripeResultSet.Empty(StripeB, RetrievalMode.Keyword),
        ], [], TimeSpan.Zero);

        Assert.Empty(await FuseAsync(new GlobalRrfFusion(), fanOut));
        Assert.Empty(await FuseAsync(new NaiveScoreFusion(), fanOut));
        Assert.Empty(await FuseAsync(new MinMaxNormalizationFusion(), fanOut));
    }

    /// <summary>
    /// A stripe that failed must not be treated as a stripe that legitimately had no matches.
    /// </summary>
    /// <remarks>
    /// The distinction matters for the results table: a partial fan-out should still return the
    /// documents it did retrieve, but the run has to remain identifiable as degraded rather than
    /// being averaged in as a normal low-recall result.
    /// </remarks>
    [Fact]
    public async Task Fusion_StillReturnsResultsWhenOneStripeFailed()
    {
        var fanOut = new FanOutResult("test", RetrievalMode.Keyword,
        [
            new StripeResultSet(StripeA, RetrievalMode.Keyword,
                [Doc("a1", StripeA, 1, 3.0)], 1, TimeSpan.Zero, 0),
        ],
        [new StripeFailure(StripeB, 503, "service unavailable")],
        TimeSpan.Zero);

        IReadOnlyList<FusedDocument> fused = await FuseAsync(new GlobalRrfFusion(), fanOut);

        Assert.Single(fused);
        Assert.Single(fanOut.Failures);
    }
}
