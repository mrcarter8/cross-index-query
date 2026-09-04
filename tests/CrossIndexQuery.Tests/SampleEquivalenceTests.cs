using CrossIndexQuery.Core.Fusion;
using CrossIndexQuery.Core.Models;
using CrossIndexQuery.Core.Retrieval;
using CrossIndexQuery.Core.Statistics;
using CrossIndexQuery.Samples;
using Xunit;

namespace CrossIndexQuery.Tests;

/// <summary>
/// Asserts that the code in <c>samples/</c> produces the same ranking as the strategies the
/// benchmark actually measures.
/// </summary>
/// <remarks>
/// <para>
/// A sample repository has a specific way of going wrong: the measured implementation and the
/// pasteable one drift apart, and the published numbers stop describing the code a reader is being
/// handed. Nothing about that is visible — both halves compile, both run, and the sample keeps
/// looking authoritative while quietly ceasing to be what was benchmarked.
/// </para>
/// <para>
/// These tests close that gap. They run the sample's implementation and the harness's strategy over
/// one fixture and require identical output. If either side changes without the other, the build
/// fails and the discrepancy is found by whoever introduced it rather than by a reader.
/// </para>
/// </remarks>
public sealed class SampleEquivalenceTests
{
    private const string StripeA = "stripe-a";
    private const string StripeB = "stripe-b";

    /// <summary>
    /// A corpus where the two stripes disagree sharply about how rare "dragon" is.
    /// </summary>
    /// <remarks>
    /// Stripe A holds it in 400 of 1,000 documents, stripe B in 5 of 1,000. That is the distortion
    /// the whole sample is about, so a fixture without it would let a broken correction pass.
    /// </remarks>
    private static CorpusStatistics Statistics()
    {
        var statistics = new CorpusStatistics
        {
            DocumentCount = 2000,
            AverageDocumentLength = 10,
            DocumentFrequencies = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["dragon"] = 405,
                ["ancient"] = 200,
            },
            PerIndexDocumentCounts = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                [StripeA] = 1000,
                [StripeB] = 1000,
            },
            PerIndexAverageDocumentLength = new Dictionary<string, double>(StringComparer.Ordinal)
            {
                [StripeA] = 10,
                [StripeB] = 10,
            },
        };

        statistics.PerIndexDocumentFrequencies[StripeA] = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["dragon"] = 400,
            ["ancient"] = 150,
        };

        statistics.PerIndexDocumentFrequencies[StripeB] = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["dragon"] = 5,
            ["ancient"] = 50,
        };

        return statistics;
    }

    /// <summary>The same statistics expressed in the shape the sample declares.</summary>
    private static Pattern1QueryOnly.CorpusStats SampleStatistics()
    {
        CorpusStatistics source = Statistics();

        return new Pattern1QueryOnly.CorpusStats
        {
            DocumentCount = source.DocumentCount,
            AverageDocumentLength = source.AverageDocumentLength,
            DocumentFrequencies = new Dictionary<string, int>(source.DocumentFrequencies, StringComparer.Ordinal),
            PerIndexDocumentCounts = new Dictionary<string, int>(source.PerIndexDocumentCounts, StringComparer.Ordinal),
            PerIndexDocumentFrequencies = source.PerIndexDocumentFrequencies.ToDictionary(
                kv => kv.Key,
                kv => new Dictionary<string, int>(kv.Value, StringComparer.Ordinal),
                StringComparer.Ordinal),
        };
    }

    private static FanOutResult FanOut()
    {
        ScoredDocument Doc(string id, string index, int rank, double score, string title, string blurb) =>
            new(
                new BookDocument { Id = id, Title = title, Blurb = blurb, Authors = [] },
                index,
                rank,
                score);

        // The raw scores are consistent with each stripe's own view of the vocabulary, because that
        // is what makes this fixture a real instance of the problem rather than an arbitrary one.
        // Stripe A holds "dragon" in 400 of 1,000 documents, so it treats the term as ordinary and
        // scores it modestly. Stripe B holds it in 5, judges it highly informative, and scores its
        // one dragon document far higher — despite that document being no better.
        StripeResultSet a = new(
            StripeA,
            RetrievalMode.Keyword,
            [
                Doc("a1", StripeA, 1, 8.0, "Dragon Rider", "A dragon and its rider cross an ancient realm."),
                Doc("a2", StripeA, 2, 6.5, "Ancient Fire", "An ancient dragon sleeps beneath the mountain."),
            ],
            TotalCount: 2,
            Elapsed: TimeSpan.FromMilliseconds(10),
            ComputeUnits: 0.0001);

        StripeResultSet b = new(
            StripeB,
            RetrievalMode.Keyword,
            [
                Doc("b1", StripeB, 1, 12.0, "The Dragon Letters", "A dragon appears in an ancient manuscript."),
                Doc("b2", StripeB, 2, 4.0, "Ancient Roads", "Ancient roads wind through the province."),
            ],
            TotalCount: 2,
            Elapsed: TimeSpan.FromMilliseconds(12),
            ComputeUnits: 0.0001);

        return new FanOutResult("dragon ancient", RetrievalMode.Keyword, [a, b], [], TimeSpan.FromMilliseconds(12));
    }

    private static List<Pattern1QueryOnly.Hit> SampleHits() =>
        [.. FanOut().AllDocuments.Select(d =>
            new Pattern1QueryOnly.Hit(d.Id, d.SourceIndex, d.Rank, d.Score))];

    private static Dictionary<string, string> SampleDocumentText() =>
        FanOut().AllDocuments.ToDictionary(
            d => d.Id,
            d => $"{d.Document.Title} {d.Document.Blurb}",
            StringComparer.Ordinal);

    private static async Task<List<string>> RankAsync(IFusionStrategy strategy, int topK = 4)
    {
        IReadOnlyList<FusedDocument> fused = await strategy
            .FuseAsync(FanOut(), new FusionContext(topK), TestContext.Current.CancellationToken)
            .ConfigureAwait(false);

        return [.. fused.Select(f => f.Id)];
    }

    /// <summary>
    /// The IDF correction shown in the sample ranks identically to the measured strategy.
    /// </summary>
    [Fact]
    public async Task Pattern1IdfCorrectionMatchesTheMeasuredStrategy()
    {
        List<string> measured = await RankAsync(
            new IdfCorrectionFusion(new SidecarDocumentFrequencyProvider(Statistics())));

        List<string> sample = [.. Pattern1QueryOnly
            .MergeWithIdfCorrection(SampleHits(), ["dragon", "ancient"], SampleStatistics(), topK: 4)
            .Select(h => h.Id)];

        Assert.Equal(measured, sample);
    }

    /// <summary>
    /// The client-side BM25 recomputation shown in the sample ranks identically to the measured
    /// strategy — the one carrying the study's largest reported gain.
    /// </summary>
    [Fact]
    public async Task Pattern1GlobalBm25MatchesTheMeasuredStrategy()
    {
        List<string> measured = await RankAsync(new GlobalBm25Fusion(Statistics()));

        List<string> sample = [.. Pattern1QueryOnly
            .MergeWithGlobalBm25(
                SampleHits(), SampleDocumentText(), ["dragon", "ancient"], SampleStatistics(), topK: 4)
            .Select(h => h.Id)];

        Assert.Equal(measured, sample);
    }

    [Fact]
    public async Task Pattern1NaiveMergeMatchesTheMeasuredStrategy()
    {
        List<string> measured = await RankAsync(new NaiveScoreFusion());

        List<string> sample = [.. Pattern1QueryOnly
            .MergeNaively(SampleHits(), topK: 4)
            .Select(h => h.Id)];

        Assert.Equal(measured, sample);
    }

    [Fact]
    public async Task Pattern1RankMergeMatchesTheMeasuredStrategy()
    {
        List<string> measured = await RankAsync(new GlobalRrfFusion());

        List<string> sample = [.. Pattern1QueryOnly
            .MergeByRank(SampleHits(), topK: 4)
            .Select(h => h.Id)];

        Assert.Equal(measured, sample);
    }

    /// <summary>
    /// The fixture is only useful if the strategies actually disagree on it.
    /// </summary>
    /// <remarks>
    /// Four equivalence tests that all pass because every strategy returns the same order would
    /// prove nothing. This asserts the fixture discriminates, so the tests above are load-bearing.
    /// </remarks>
    [Fact]
    public async Task TheFixtureActuallySeparatesTheStrategies()
    {
        List<string> naive = await RankAsync(new NaiveScoreFusion());
        List<string> corrected = await RankAsync(
            new IdfCorrectionFusion(new SidecarDocumentFrequencyProvider(Statistics())));

        Assert.NotEqual(naive, corrected);

        // The phenomenon in miniature. Stripe B holds "dragon" in 5 documents against stripe A's
        // 400, so it scores its one dragon document highest of anything returned — and naive
        // merging puts it first. The correction knows the term is ordinary corpus-wide, scales
        // stripe B down, and hands the top position back to stripe A.
        Assert.Equal("b1", naive[0]);
        Assert.Equal("a1", corrected[0]);
    }
}
