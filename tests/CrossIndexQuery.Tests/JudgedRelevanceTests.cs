using CrossIndexQuery.Core.Evaluation;
using Xunit;

namespace CrossIndexQuery.Tests;

/// <summary>
/// Reference-value tests for scoring against independent relevance judgments.
/// </summary>
/// <remarks>
/// These exist to pin the one property the oracle-fidelity metrics cannot have: that a result which
/// is genuinely better than the single index scores higher than it. Every other metric in the
/// harness treats the oracle as correct by construction, so if this file goes red the sample has
/// lost its only means of asking whether the oracle deserved that status.
/// </remarks>
public sealed class JudgedRelevanceTests
{
    private static readonly Dictionary<string, int> Grades = new(StringComparer.Ordinal)
    {
        ["excellent"] = 3,
        ["good"] = 2,
        ["marginal"] = 1,
        ["irrelevant"] = 0,
    };

    [Fact]
    public void JudgedNdcg_IsOneForTheBestPossibleOrdering()
    {
        double score = RankingMetrics.JudgedNdcg(
            ["excellent", "good", "marginal", "irrelevant"], Grades, 10);

        Assert.Equal(1.0, score, precision: 10);
    }

    [Fact]
    public void JudgedNdcg_IsZeroWhenNothingRelevantIsReturned()
    {
        double score = RankingMetrics.JudgedNdcg(["irrelevant", "unjudged"], Grades, 10);

        Assert.Equal(0.0, score, precision: 10);
    }

    [Fact]
    public void JudgedNdcg_PenalisesBuryingTheBestDocument()
    {
        double best = RankingMetrics.JudgedNdcg(["excellent", "good"], Grades, 10);
        double buried = RankingMetrics.JudgedNdcg(["good", "excellent"], Grades, 10);

        Assert.True(buried < best);
    }

    /// <summary>
    /// One highly relevant document beats two mediocre ones.
    /// </summary>
    /// <remarks>
    /// This is why the gain is exponential in the grade rather than linear. Under linear gain a
    /// pair of grade-2 documents would tie a single grade-3, and a strategy that reliably surfaces
    /// the single best answer would score no better than one that surfaces two adequate ones.
    /// </remarks>
    [Fact]
    public void JudgedNdcg_RewardsOneExcellentOverTwoMerelyGood()
    {
        var grades = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["excellent"] = 3,
            ["good1"] = 2,
            ["good2"] = 2,
        };

        double excellentFirst = RankingMetrics.JudgedNdcg(["excellent"], grades, 1);
        double twoGood = RankingMetrics.JudgedNdcg(["good1"], grades, 1);

        Assert.True(excellentFirst > twoGood);
    }

    /// <summary>
    /// The claim the whole judging apparatus exists to make testable.
    /// </summary>
    /// <remarks>
    /// A striped run that surfaces a document the single index never retrieved is scored as an error
    /// by oracle-fidelity nDCG, because the document cannot be in an answer key derived from the
    /// oracle's own output. Judged against absolute grades it scores higher, which is the correct
    /// answer. If these two assertions ever agree, the metric has stopped being independent.
    /// </remarks>
    [Fact]
    public void JudgedNdcg_LetsAStripedResultBeatTheOracle()
    {
        // The oracle never retrieved "excellent" — its candidate window cut off before reaching it.
        string[] oracleTop = ["good", "marginal"];
        string[] striped = ["excellent", "good"];

        double fidelity = RankingMetrics.NormalizedDiscountedCumulativeGain(striped, oracleTop, 10);
        double judgedOracle = RankingMetrics.JudgedNdcg(oracleTop, Grades, 10);
        double judgedStriped = RankingMetrics.JudgedNdcg(striped, Grades, 10);

        Assert.True(fidelity < 1.0, "fidelity penalises the striped run for departing from the oracle");
        Assert.True(judgedStriped > judgedOracle, "judged relevance recognises it as the better result");
    }

    [Fact]
    public void Coverage_ReportsTheFractionCarryingAJudgment()
    {
        double coverage = RankingMetrics.JudgedCoverage(
            ["excellent", "unjudged", "good", "alsoUnjudged"], Grades, 10);

        Assert.Equal(0.5, coverage, precision: 10);
    }

    [Fact]
    public void Coverage_IsBoundedByK()
    {
        double coverage = RankingMetrics.JudgedCoverage(["excellent", "unjudged"], Grades, 1);

        Assert.Equal(1.0, coverage, precision: 10);
    }

    /// <summary>
    /// An unjudged document is treated as irrelevant, never as a gap to be skipped over.
    /// </summary>
    /// <remarks>
    /// The standard pooling convention. It biases against a strategy that finds documents no other
    /// approach surfaced, which is precisely why coverage is reported next to the score rather than
    /// left implicit.
    /// </remarks>
    [Fact]
    public void JudgedNdcg_TreatsUnjudgedDocumentsAsIrrelevant()
    {
        double withGap = RankingMetrics.JudgedNdcg(["unjudged", "excellent"], Grades, 10);
        double withIrrelevant = RankingMetrics.JudgedNdcg(["irrelevant", "excellent"], Grades, 10);

        Assert.Equal(withIrrelevant, withGap, precision: 10);
    }
}
