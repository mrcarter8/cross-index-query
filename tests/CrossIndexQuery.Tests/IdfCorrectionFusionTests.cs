using CrossIndexQuery.Core.Fusion;
using CrossIndexQuery.Core.Models;
using CrossIndexQuery.Core.Retrieval;
using CrossIndexQuery.Core.Statistics;
using Xunit;

namespace CrossIndexQuery.Tests;

/// <summary>
/// Tests the IDF correction, which is the sample's central technical claim.
/// </summary>
/// <remarks>
/// <para>
/// The claim is that BM25 scores from two indexes are incomparable because each index computes
/// inverse document frequency against its own document set, and that the incomparability can be
/// substantially undone by rescaling each stripe's scores by the ratio between the IDF the term
/// <em>would</em> have had over the whole corpus and the IDF the stripe actually used.
/// </para>
/// <para>
/// These tests pin that arithmetic down with document frequencies chosen so the right answer can be
/// computed by hand. A fake frequency provider is used rather than the live probe or the sidecar,
/// because the correction is what is under test here, not the two ways of obtaining its inputs.
/// </para>
/// </remarks>
public sealed class IdfCorrectionFusionTests
{
    private const string StripeA = "books-stripe-a";
    private const string StripeB = "books-stripe-b";

    /// <summary>Returns whatever frequencies the test hands it, so the arithmetic is isolated.</summary>
    private sealed class FakeFrequencyProvider(TermFrequencies frequencies) : IDocumentFrequencyProvider
    {
        public string Source => "fake";

        public ValueTask<TermFrequencies> GetAsync(
            IReadOnlyList<string> terms,
            IReadOnlyList<string> indexNames,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(frequencies);
    }

    private static ScoredDocument Doc(string id, string index, int rank, double score) =>
        new(new BookDocument { Id = id, Title = id }, index, rank, score, TextScore: score);

    private static FanOutResult FanOut(string query, params StripeResultSet[] stripes) =>
        new(query, RetrievalMode.Keyword, stripes, [], TimeSpan.Zero);

    private static async Task<IReadOnlyList<FusedDocument>> FuseAsync(
        TermFrequencies frequencies,
        FanOutResult fanOut,
        int topK = 10)
    {
        var strategy = new IdfCorrectionFusion(new FakeFrequencyProvider(frequencies));
        return await strategy.FuseAsync(
            fanOut, new FusionContext(topK, null), TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The correction must reverse an ordering that raw BM25 got backwards.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The term "dragon" is deliberately lopsided: it appears in 2,000 of stripe A's 5,000 documents
    /// but only 10 of stripe B's 5,000. Stripe B therefore treats it as a rare, highly informative
    /// term and awards its match a large BM25 score, while stripe A treats it as common and awards
    /// a small one — even though, over the corpus as a whole, "dragon" appears in 2,010 of 10,000
    /// documents and is not rare at all.
    /// </para>
    /// <para>
    /// A naive merge reads stripe B's inflated score at face value and puts its document first. The
    /// correction deflates stripe B toward the corpus-wide IDF and lifts stripe A toward it, which
    /// is enough to reverse the order. This is precisely the failure the sample is written to
    /// demonstrate, so if this assertion ever flips, the thesis is broken rather than the test.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Correction_ReversesAnOrderingThatRawScoresGotBackwards()
    {
        var frequencies = new TermFrequencies(
            GlobalDocumentCount: 10_000,
            LocalDocumentCounts: new Dictionary<string, int> { [StripeA] = 5_000, [StripeB] = 5_000 },
            GlobalDocumentFrequency: new Dictionary<string, int> { ["dragon"] = 2_010 },
            LocalDocumentFrequency: new Dictionary<string, IReadOnlyDictionary<string, int>>
            {
                [StripeA] = new Dictionary<string, int> { ["dragon"] = 2_000 },
                [StripeB] = new Dictionary<string, int> { ["dragon"] = 10 },
            });

        FanOutResult fanOut = FanOut("dragon",
            new StripeResultSet(StripeA, RetrievalMode.Keyword, [Doc("a1", StripeA, 1, 2.0)], 1, TimeSpan.Zero, 0),
            new StripeResultSet(StripeB, RetrievalMode.Keyword, [Doc("b1", StripeB, 1, 6.0)], 1, TimeSpan.Zero, 0));

        // Raw scores say B wins by a factor of three.
        IReadOnlyList<FusedDocument> naive = await new NaiveScoreFusion()
            .FuseAsync(fanOut, new FusionContext(10, null), TestContext.Current.CancellationToken);
        Assert.Equal("b1", naive[0].Id);

        // Corrected for the IDF each stripe actually used, A wins.
        IReadOnlyList<FusedDocument> corrected = await FuseAsync(frequencies, fanOut);
        Assert.Equal("a1", corrected[0].Id);
    }

    /// <summary>
    /// Where a term is distributed evenly, the correction must be close to a no-op.
    /// </summary>
    /// <remarks>
    /// This is the guard against the correction being an indiscriminate reshuffle. Striping is
    /// harmful only in so far as it distorts the statistics; where a term happens to split evenly
    /// the two stripes already agree with the corpus, and a correction that moved results anyway
    /// would be introducing error rather than removing it.
    /// </remarks>
    [Fact]
    public async Task Correction_IsNearlyNeutralWhenTheTermIsDistributedEvenly()
    {
        var frequencies = new TermFrequencies(
            GlobalDocumentCount: 10_000,
            LocalDocumentCounts: new Dictionary<string, int> { [StripeA] = 5_000, [StripeB] = 5_000 },
            GlobalDocumentFrequency: new Dictionary<string, int> { ["mystery"] = 1_000 },
            LocalDocumentFrequency: new Dictionary<string, IReadOnlyDictionary<string, int>>
            {
                [StripeA] = new Dictionary<string, int> { ["mystery"] = 500 },
                [StripeB] = new Dictionary<string, int> { ["mystery"] = 500 },
            });

        FanOutResult fanOut = FanOut("mystery",
            new StripeResultSet(StripeA, RetrievalMode.Keyword, [Doc("a1", StripeA, 1, 4.0)], 1, TimeSpan.Zero, 0),
            new StripeResultSet(StripeB, RetrievalMode.Keyword, [Doc("b1", StripeB, 1, 5.0)], 1, TimeSpan.Zero, 0));

        IReadOnlyList<FusedDocument> corrected = await FuseAsync(frequencies, fanOut);

        // Both stripes saw an identical term distribution, so both get an identical factor and the
        // original order survives.
        Assert.Equal(["b1", "a1"], corrected.Select(d => d.Id));
        Assert.Equal(corrected[0].FusedScore / 5.0, corrected[1].FusedScore / 4.0, precision: 9);
    }

    /// <summary>
    /// The scale factor itself must match the documented formula.
    /// </summary>
    /// <remarks>
    /// With a single query term the weighted mean collapses to the bare ratio
    /// <c>IDF_global / IDF_local</c>, which makes the expected factor computable directly from the
    /// same helper the implementation uses. Asserting the number rather than only the ordering is
    /// what makes this a test of the arithmetic instead of a test of a comparison.
    /// </remarks>
    [Fact]
    public async Task Correction_AppliesTheGlobalOverLocalIdfRatio()
    {
        const int globalN = 10_000;
        const int localN = 5_000;
        const int globalDf = 2_010;
        const int localDfA = 2_000;

        var frequencies = new TermFrequencies(
            GlobalDocumentCount: globalN,
            LocalDocumentCounts: new Dictionary<string, int> { [StripeA] = localN },
            GlobalDocumentFrequency: new Dictionary<string, int> { ["dragon"] = globalDf },
            LocalDocumentFrequency: new Dictionary<string, IReadOnlyDictionary<string, int>>
            {
                [StripeA] = new Dictionary<string, int> { ["dragon"] = localDfA },
            });

        FanOutResult fanOut = FanOut("dragon",
            new StripeResultSet(StripeA, RetrievalMode.Keyword, [Doc("a1", StripeA, 1, 2.0)], 1, TimeSpan.Zero, 0));

        IReadOnlyList<FusedDocument> corrected = await FuseAsync(frequencies, fanOut);

        double expectedFactor =
            CorpusStatistics.Idf(globalDf, globalN) / CorpusStatistics.Idf(localDfA, localN);

        Assert.Equal(2.0 * expectedFactor, corrected[0].FusedScore, precision: 9);
    }

    /// <summary>
    /// A term absent from a stripe must not contribute to that stripe's correction.
    /// </summary>
    /// <remarks>
    /// Its local IDF would be the maximum possible, so including it would apply an enormous
    /// deflation derived from a term that had no influence on the score being corrected. Skipping
    /// it is the only defensible choice: the stripe's score says nothing about a term it never
    /// matched.
    /// </remarks>
    [Fact]
    public async Task Correction_IgnoresTermsAbsentFromTheStripe()
    {
        var withAbsentTerm = new TermFrequencies(
            GlobalDocumentCount: 10_000,
            LocalDocumentCounts: new Dictionary<string, int> { [StripeA] = 5_000 },
            GlobalDocumentFrequency: new Dictionary<string, int> { ["dragon"] = 2_010, ["quokka"] = 3 },
            LocalDocumentFrequency: new Dictionary<string, IReadOnlyDictionary<string, int>>
            {
                [StripeA] = new Dictionary<string, int> { ["dragon"] = 2_000, ["quokka"] = 0 },
            });

        var withoutAbsentTerm = new TermFrequencies(
            GlobalDocumentCount: 10_000,
            LocalDocumentCounts: new Dictionary<string, int> { [StripeA] = 5_000 },
            GlobalDocumentFrequency: new Dictionary<string, int> { ["dragon"] = 2_010 },
            LocalDocumentFrequency: new Dictionary<string, IReadOnlyDictionary<string, int>>
            {
                [StripeA] = new Dictionary<string, int> { ["dragon"] = 2_000 },
            });

        var stripe = new StripeResultSet(
            StripeA, RetrievalMode.Keyword, [Doc("a1", StripeA, 1, 2.0)], 1, TimeSpan.Zero, 0);

        IReadOnlyList<FusedDocument> withTerm =
            await FuseAsync(withAbsentTerm, FanOut("dragon quokka", stripe));
        IReadOnlyList<FusedDocument> withoutTerm =
            await FuseAsync(withoutAbsentTerm, FanOut("dragon", stripe));

        Assert.Equal(withoutTerm[0].FusedScore, withTerm[0].FusedScore, precision: 9);
    }

    /// <summary>
    /// A query that tokenizes to nothing must fall back rather than divide by zero.
    /// </summary>
    /// <remarks>
    /// Punctuation-only and stopword-only queries reach production search boxes constantly, and
    /// there is no correction to apply when there are no terms to correct against.
    /// </remarks>
    [Fact]
    public async Task Correction_FallsBackToRawScoresWhenTheQueryHasNoTerms()
    {
        var frequencies = new TermFrequencies(
            GlobalDocumentCount: 10_000,
            LocalDocumentCounts: new Dictionary<string, int> { [StripeA] = 5_000 },
            GlobalDocumentFrequency: new Dictionary<string, int>(),
            LocalDocumentFrequency: new Dictionary<string, IReadOnlyDictionary<string, int>>());

        FanOutResult fanOut = FanOut("   !!!   ",
            new StripeResultSet(StripeA, RetrievalMode.Keyword,
                [Doc("a1", StripeA, 1, 3.0), Doc("a2", StripeA, 2, 1.0)], 2, TimeSpan.Zero, 0));

        IReadOnlyList<FusedDocument> fused = await FuseAsync(frequencies, fanOut);

        Assert.Equal(["a1", "a2"], fused.Select(d => d.Id));
        Assert.Equal(3.0, fused[0].FusedScore, precision: 9);
    }

    /// <summary>
    /// An unknown index must be left alone rather than scaled by a fabricated factor.
    /// </summary>
    [Fact]
    public async Task Correction_LeavesAStripeUntouchedWhenItsFrequenciesAreMissing()
    {
        var frequencies = new TermFrequencies(
            GlobalDocumentCount: 10_000,
            LocalDocumentCounts: new Dictionary<string, int> { [StripeA] = 5_000 },
            GlobalDocumentFrequency: new Dictionary<string, int> { ["dragon"] = 2_010 },
            LocalDocumentFrequency: new Dictionary<string, IReadOnlyDictionary<string, int>>
            {
                [StripeA] = new Dictionary<string, int> { ["dragon"] = 2_000 },
            });

        FanOutResult fanOut = FanOut("dragon",
            new StripeResultSet(StripeB, RetrievalMode.Keyword, [Doc("b1", StripeB, 1, 7.5)], 1, TimeSpan.Zero, 0));

        IReadOnlyList<FusedDocument> fused = await FuseAsync(frequencies, fanOut);

        Assert.Equal(7.5, fused[0].FusedScore, precision: 9);
    }

    /// <summary>
    /// The correction is defined for keyword and hybrid results, and refuses pure vector results.
    /// </summary>
    /// <remarks>
    /// There is nothing to correct in a cosine similarity: it is a position in a shared embedding
    /// space rather than a statistic computed against a corpus, so it is already comparable across
    /// indexes. Declaring that honestly keeps the strategy out of the vector-only results table
    /// instead of contributing a meaningless row to it.
    /// </remarks>
    [Fact]
    public void Correction_DeclaresSupportForKeywordAndHybridOnly()
    {
        var frequencies = new TermFrequencies(
            0, new Dictionary<string, int>(), new Dictionary<string, int>(),
            new Dictionary<string, IReadOnlyDictionary<string, int>>());

        var strategy = new IdfCorrectionFusion(new FakeFrequencyProvider(frequencies));

        Assert.True(strategy.Supports(RetrievalMode.Keyword));
        Assert.True(strategy.Supports(RetrievalMode.Hybrid));
        Assert.False(strategy.Supports(RetrievalMode.Vector));
    }
}
