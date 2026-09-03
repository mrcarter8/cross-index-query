using CrossIndexQuery.Core.Evaluation;
using Xunit;

namespace CrossIndexQuery.Tests;

/// <summary>
/// Reference-value tests for the ranking metrics.
/// </summary>
/// <remarks>
/// These metrics decide which fusion strategy the sample recommends, so they are worth pinning to
/// hand-computable cases. A subtly wrong nDCG would not throw or look obviously broken; it would
/// just quietly rank the strategies in the wrong order, which is the one failure mode that would
/// invalidate the whole exercise.
/// </remarks>
public sealed class RankingMetricsTests
{
    private static readonly string[] Ideal = ["a", "b", "c", "d", "e"];

    [Fact]
    public void Ndcg_IsOneWhenRankingMatchesTheOracle()
    {
        double score = RankingMetrics.NormalizedDiscountedCumulativeGain(Ideal, Ideal, 5);
        Assert.Equal(1.0, score, precision: 10);
    }

    [Fact]
    public void Ndcg_IsZeroWhenNothingRelevantIsRetrieved()
    {
        double score = RankingMetrics.NormalizedDiscountedCumulativeGain(["x", "y", "z"], Ideal, 5);
        Assert.Equal(0.0, score, precision: 10);
    }

    [Fact]
    public void Ndcg_PenalisesReversingTheOrder()
    {
        double reversed = RankingMetrics.NormalizedDiscountedCumulativeGain(["e", "d", "c", "b", "a"], Ideal, 5);

        Assert.True(reversed < 1.0);
        Assert.True(reversed > 0.0);
    }

    /// <summary>
    /// Getting the best document right matters more than getting the second one right.
    /// </summary>
    /// <remarks>
    /// This is the property that makes nDCG the right metric for the sample. Cross-index fusion
    /// errors are concentrated at the top of the list — that is where scores from two corpora
    /// compete most directly — so the metric has to weight the top heavily or it will report those
    /// errors as negligible.
    /// </remarks>
    [Fact]
    public void Ndcg_WeightsEarlyPositionsMoreHeavily()
    {
        double topSwapped = RankingMetrics.NormalizedDiscountedCumulativeGain(["b", "a", "c", "d", "e"], Ideal, 5);
        double tailSwapped = RankingMetrics.NormalizedDiscountedCumulativeGain(["a", "b", "c", "e", "d"], Ideal, 5);

        Assert.True(topSwapped < tailSwapped);
    }

    [Fact]
    public void RecallAtK_CountsOracleDocumentsPresent()
    {
        Assert.Equal(0.6, RankingMetrics.RecallAtK(["a", "b", "c", "x", "y"], Ideal, 5), precision: 10);
        Assert.Equal(1.0, RankingMetrics.RecallAtK(Ideal, Ideal, 5), precision: 10);
        Assert.Equal(0.0, RankingMetrics.RecallAtK(["x"], Ideal, 5), precision: 10);
    }

    [Fact]
    public void KendallTau_IsOneForIdenticalOrderAndMinusOneForReversed()
    {
        Assert.Equal(1.0, RankingMetrics.KendallTau(Ideal, Ideal), precision: 10);
        Assert.Equal(-1.0, RankingMetrics.KendallTau(["e", "d", "c", "b", "a"], Ideal), precision: 10);
    }

    /// <summary>
    /// A single adjacent transposition in a five-item list gives tau = 0.8.
    /// </summary>
    /// <remarks>
    /// There are 10 pairs; swapping one adjacent pair discordes exactly one of them, so
    /// (9 - 1) / 10 = 0.8. Pinning an exact hand-computed value catches sign and normalisation
    /// errors that a monotonicity check would let through.
    /// </remarks>
    [Fact]
    public void KendallTau_MatchesHandComputedValueForOneTransposition()
    {
        Assert.Equal(0.8, RankingMetrics.KendallTau(["b", "a", "c", "d", "e"], Ideal), precision: 10);
    }

    [Fact]
    public void RankBiasedOverlap_IsOneForIdenticalListsAndZeroForDisjoint()
    {
        Assert.Equal(1.0, RankingMetrics.RankBiasedOverlap(Ideal, Ideal), precision: 6);
        Assert.Equal(0.0, RankingMetrics.RankBiasedOverlap(["v", "w", "x", "y", "z"], Ideal), precision: 6);
    }

    /// <summary>
    /// RBO is top-weighted, which is why the sample reports it next to nDCG.
    /// </summary>
    /// <remarks>
    /// nDCG asks "did you find the good documents"; RBO asks "does this list look like that list".
    /// Two strategies can score identically on nDCG while producing visibly different result pages,
    /// and RBO is what distinguishes them.
    /// </remarks>
    [Fact]
    public void RankBiasedOverlap_PenalisesDisagreementAtTheTopMore()
    {
        double topDiffers = RankingMetrics.RankBiasedOverlap(["x", "b", "c", "d", "e"], Ideal);
        double tailDiffers = RankingMetrics.RankBiasedOverlap(["a", "b", "c", "d", "x"], Ideal);

        Assert.True(topDiffers < tailDiffers);
    }

    [Fact]
    public void JaccardAtK_IgnoresOrder()
    {
        Assert.Equal(
            1.0,
            RankingMetrics.JaccardAtK(["c", "b", "a"], ["a", "b", "c"], 3),
            precision: 10);
    }

    [Fact]
    public void Metrics_HandleEmptyInputWithoutThrowing()
    {
        Assert.Equal(0.0, RankingMetrics.NormalizedDiscountedCumulativeGain([], Ideal, 5));
        Assert.Equal(0.0, RankingMetrics.RecallAtK([], Ideal, 5));
        Assert.Equal(0.0, RankingMetrics.RankBiasedOverlap([], Ideal));

        // Two empty sets are trivially identical, so both similarity measures return 1 rather than
        // the 0/0 the definitions would otherwise produce.
        Assert.Equal(1.0, RankingMetrics.JaccardAtK([], [], 5));
        Assert.Equal(1.0, RankingMetrics.RankBiasedOverlap([], []));
    }
}
