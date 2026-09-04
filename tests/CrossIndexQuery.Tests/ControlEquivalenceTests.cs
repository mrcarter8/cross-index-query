using CrossIndexQuery.Core.Fusion;
using CrossIndexQuery.Core.Models;
using CrossIndexQuery.Core.Retrieval;
using CrossIndexQuery.Core.Statistics;
using Xunit;

namespace CrossIndexQuery.Tests;

/// <summary>
/// Guards the property that makes <c>local-bm25</c> a valid control for <c>global-bm25</c>.
/// </summary>
/// <remarks>
/// <para>
/// The study reports a decomposition: how much of <c>global-bm25</c>'s advantage comes from using
/// corpus-wide statistics rather than from rescoring with a consistent tokenizer. That
/// decomposition is only meaningful if the two strategies differ in exactly one thing. Reading the
/// two files side by side today establishes that; nothing stops a later edit to one of them from
/// quietly introducing a second difference and invalidating every conclusion drawn from the
/// comparison without anyone noticing.
/// </para>
/// <para>
/// These tests make the invariant executable. The mechanism is to feed both strategies a situation
/// in which the single intended difference vanishes — statistics where every index's local view
/// equals the global view — and require the outputs to become identical. Any second difference,
/// in the tokenizer, the field set, the constants or the length normalization, survives that
/// collapse and shows up as a failure.
/// </para>
/// </remarks>
public class ControlEquivalenceTests
{
    private const string StripeA = "books-a";
    private const string StripeB = "books-b";

    /// <summary>
    /// With per-index statistics equal to the global statistics, the two must agree exactly.
    /// </summary>
    /// <remarks>
    /// This is the load-bearing test. If it fails, the two strategies differ in something other
    /// than whose document frequencies they consult, and the reported split between "rescoring"
    /// and "global statistics" is measuring that something else as well.
    /// </remarks>
    [Fact]
    public async Task StrategiesAgreeWhenLocalStatisticsEqualGlobal()
    {
        CorpusStatistics identical = Statistics(divergent: false);

        IReadOnlyList<FusedDocument> global = await FuseAsync(new GlobalBm25Fusion(identical));
        IReadOnlyList<FusedDocument> local = await FuseAsync(new LocalBm25Fusion(identical));

        Assert.Equal(
            global.Select(d => d.Id).ToArray(),
            local.Select(d => d.Id).ToArray());

        for (int i = 0; i < global.Count; i++)
        {
            Assert.Equal(global[i].FusedScore, local[i].FusedScore, 10);
        }
    }

    /// <summary>
    /// With divergent per-index statistics the two must disagree.
    /// </summary>
    /// <remarks>
    /// The complement of the previous test, and just as necessary. A control that agrees with the
    /// strategy under every condition is not isolating a variable, it is a duplicate — and the
    /// measured difference between them would then be zero for an uninteresting reason.
    /// </remarks>
    [Fact]
    public async Task StrategiesDisagreeWhenLocalStatisticsDiverge()
    {
        CorpusStatistics divergent = Statistics(divergent: true);

        IReadOnlyList<FusedDocument> global = await FuseAsync(new GlobalBm25Fusion(divergent));
        IReadOnlyList<FusedDocument> local = await FuseAsync(new LocalBm25Fusion(divergent));

        Assert.NotEqual(
            global.Select(d => d.FusedScore).ToArray(),
            local.Select(d => d.FusedScore).ToArray());
    }

    /// <summary>
    /// Both strategies must reduce to the same thing on a single stripe with global statistics.
    /// </summary>
    /// <remarks>
    /// This is the arithmetic behind the <c>single-index-rescored</c> row. That row applies
    /// <c>global-bm25</c> to a fan-out containing one index holding the whole corpus, on the
    /// argument that such an index's statistics <em>are</em> the global statistics. The argument is
    /// sound only if the strategy treats a one-index fan-out no differently, which is what this
    /// asserts.
    /// </remarks>
    [Fact]
    public async Task SingleStripeRescoreMatchesLocalRescore()
    {
        CorpusStatistics identical = Statistics(divergent: false);
        FanOutResult single = new(
            "dragon ancient",
            RetrievalMode.Keyword,
            [Stripe(StripeA)],
            [],
            TimeSpan.FromMilliseconds(9));

        IReadOnlyList<FusedDocument> global =
            await new GlobalBm25Fusion(identical).FuseAsync(single, new FusionContext(10, null), TestContext.Current.CancellationToken);
        IReadOnlyList<FusedDocument> local =
            await new LocalBm25Fusion(identical).FuseAsync(single, new FusionContext(10, null), TestContext.Current.CancellationToken);

        Assert.Equal(
            global.Select(d => d.Id).ToArray(),
            local.Select(d => d.Id).ToArray());
    }

    /// <summary>
    /// Rescoring must not depend on the order the stripes were returned in.
    /// </summary>
    /// <remarks>
    /// Fan-out is concurrent, so stripe ordering is not guaranteed between runs. A strategy whose
    /// output depends on arrival order would make the whole study irreproducible in a way that
    /// re-running it would not reliably reveal.
    /// </remarks>
    [Fact]
    public async Task RescoringIsIndependentOfStripeOrder()
    {
        CorpusStatistics stats = Statistics(divergent: true);
        var context = new FusionContext(10, null);

        FanOutResult forward = new(
            "dragon ancient",
            RetrievalMode.Keyword,
            [Stripe(StripeA), Stripe(StripeB)],
            [],
            TimeSpan.FromMilliseconds(11));

        FanOutResult reversed = new(
            "dragon ancient",
            RetrievalMode.Keyword,
            [Stripe(StripeB), Stripe(StripeA)],
            [],
            TimeSpan.FromMilliseconds(11));

        IReadOnlyList<FusedDocument> a = await new GlobalBm25Fusion(stats).FuseAsync(forward, context, TestContext.Current.CancellationToken);
        IReadOnlyList<FusedDocument> b = await new GlobalBm25Fusion(stats).FuseAsync(reversed, context, TestContext.Current.CancellationToken);

        Assert.Equal(a.Select(d => d.Id).ToArray(), b.Select(d => d.Id).ToArray());
    }

    private static Task<IReadOnlyList<FusedDocument>> FuseAsync(IFusionStrategy strategy)
    {
        FanOutResult fanOut = new(
            "dragon ancient",
            RetrievalMode.Keyword,
            [Stripe(StripeA), Stripe(StripeB)],
            [],
            TimeSpan.FromMilliseconds(11));

        return strategy.FuseAsync(fanOut, new FusionContext(10, null)).AsTask();
    }

    private static StripeResultSet Stripe(string index)
    {
        ScoredDocument Doc(string id, int rank, double score, string title, string blurb) =>
            new(
                new BookDocument { Id = id, Title = title, Blurb = blurb, Authors = [] },
                index,
                rank,
                score);

        string prefix = index == StripeA ? "a" : "b";

        return new StripeResultSet(
            index,
            RetrievalMode.Keyword,
            [
                Doc($"{prefix}1", 1, 9.0, "Dragon Rider", "A dragon and its rider cross an ancient realm."),
                Doc($"{prefix}2", 2, 6.0, "Ancient Fire", "An ancient dragon sleeps beneath the mountain."),
            ],
            TotalCount: 2,
            Elapsed: TimeSpan.FromMilliseconds(10),
            ComputeUnits: 0.0001);
    }

    /// <summary>
    /// Builds statistics whose per-index views either match the global view or diverge sharply.
    /// </summary>
    private static CorpusStatistics Statistics(bool divergent)
    {
        // Chosen so the collapsed case is exact: with divergence off, each index reports the same
        // frequencies and the same average length as the corpus, so both strategies must compute
        // the identical IDF for every term.
        Dictionary<string, int> global = new(StringComparer.Ordinal)
        {
            ["dragon"] = 400,
            ["ancient"] = 300,
            ["rider"] = 120,
            ["realm"] = 90,
            ["fire"] = 150,
            ["sleeps"] = 60,
            ["beneath"] = 70,
            ["mountain"] = 110,
            ["cross"] = 80,
            ["its"] = 500,
        };

        Dictionary<string, Dictionary<string, int>> local = new(StringComparer.Ordinal)
        {
            [StripeA] = divergent
                ? global.ToDictionary(kv => kv.Key, kv => Math.Max(1, kv.Value / 8), StringComparer.Ordinal)
                : new Dictionary<string, int>(global, StringComparer.Ordinal),
            [StripeB] = divergent
                ? global.ToDictionary(kv => kv.Key, kv => Math.Min(999, kv.Value * 2), StringComparer.Ordinal)
                : new Dictionary<string, int>(global, StringComparer.Ordinal),
        };

        return new CorpusStatistics
        {
            DocumentCount = 1000,
            AverageDocumentLength = 24.0,
            DocumentFrequencies = global,
            PerIndexDocumentCounts = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                [StripeA] = 1000,
                [StripeB] = 1000,
            },
            PerIndexAverageDocumentLength = new Dictionary<string, double>(StringComparer.Ordinal)
            {
                [StripeA] = 24.0,
                [StripeB] = 24.0,
            },
            PerIndexDocumentFrequencies = local,
        };
    }
}
