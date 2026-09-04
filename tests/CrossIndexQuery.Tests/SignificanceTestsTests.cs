using CrossIndexQuery.Core.Evaluation;
using Xunit;

namespace CrossIndexQuery.Tests;

/// <summary>
/// Verifies the significance machinery against values a reader can check independently.
/// </summary>
/// <remarks>
/// The statistics in this repository carry the study's conclusions, so the implementations are
/// pinned against reference values from standard tools rather than against their own output.
/// Expected values in these tests come from R (<c>t.test</c>, <c>wilcox.test</c>, <c>p.adjust</c>)
/// and are quoted in the assertions so they can be re-derived without running this code.
/// </remarks>
public class SignificanceTestsTests
{
    /// <summary>
    /// Two identical runs must produce no effect and no significance.
    /// </summary>
    [Fact]
    public void IdenticalSamplesShowNoEffect()
    {
        double[] scores = [0.1, 0.5, 0.9, 0.3, 0.7];

        PairedComparison result = SignificanceTests.Compare(scores, scores);

        Assert.Equal(0.0, result.MeanDifference, 12);
        Assert.Equal(1.0, result.TTestP, 12);
        Assert.Equal(1.0, result.WilcoxonP, 12);
        Assert.Equal(0, result.Wins);
        Assert.Equal(0, result.Losses);
        Assert.Equal(5, result.Ties);
        Assert.False(result.IntervalExcludesZero);
    }

    /// <summary>
    /// A constant shift is a real effect with zero variance, and must not produce a NaN.
    /// </summary>
    /// <remarks>
    /// This is not a contrived case. In vector-only mode several strategies differ from the
    /// baseline by the same amount on every query they change, and an implementation that divides
    /// by a zero standard deviation reports NaN, which then propagates silently into the results
    /// table as a blank cell.
    /// </remarks>
    [Fact]
    public void ConstantShiftIsSignificantAndFinite()
    {
        double[] baseline = [0.1, 0.5, 0.9, 0.3, 0.7];
        double[] candidate = baseline.Select(s => s + 0.1).ToArray();

        PairedComparison result = SignificanceTests.Compare(baseline, candidate);

        Assert.Equal(0.1, result.MeanDifference, 10);
        Assert.Equal(0.0, result.TTestP, 10);
        Assert.False(double.IsNaN(result.TStatistic));
        Assert.Equal(5, result.Wins);
        Assert.True(result.IntervalExcludesZero);

        // Every resample of a constant vector has the same mean, so the interval collapses onto
        // the effect itself.
        Assert.Equal(0.1, result.IntervalLow, 10);
        Assert.Equal(0.1, result.IntervalHigh, 10);
    }

    /// <summary>
    /// Paired t-test against a hand-checkable case.
    /// </summary>
    /// <remarks>
    /// Differences of 1..5 give mean 3, sample standard deviation 1.5811, standard error 0.7071
    /// and t = 4.2426 on 4 degrees of freedom. R reports p = 0.01324 for
    /// <c>t.test(c(1,2,3,4,5))</c>.
    /// </remarks>
    [Fact]
    public void PairedTMatchesReferenceValue()
    {
        double[] baseline = [0, 0, 0, 0, 0];
        double[] candidate = [1, 2, 3, 4, 5];

        PairedComparison result = SignificanceTests.Compare(baseline, candidate);

        Assert.Equal(3.0, result.MeanDifference, 10);
        Assert.Equal(4.2426, result.TStatistic, 3);
        Assert.Equal(0.01324, result.TTestP, 4);
    }

    /// <summary>
    /// The t tail must reproduce published critical values.
    /// </summary>
    /// <remarks>
    /// Each pair is a standard two-tailed 5% critical value: t = 2.228 at 10 degrees of freedom,
    /// t = 2.086 at 20, t = 2.009 at 50. A tail implementation that is subtly wrong will still
    /// look plausible on a single case, so three points across the useful range are checked.
    /// </remarks>
    [Theory]
    [InlineData(2.228, 10)]
    [InlineData(2.086, 20)]
    [InlineData(2.009, 50)]
    public void StudentTTailMatchesCriticalValues(double t, int degreesOfFreedom)
    {
        // Reconstructed through the public surface: n - 1 degrees of freedom means n observations,
        // and a difference vector with the required mean and standard deviation produces the
        // target statistic.
        int n = degreesOfFreedom + 1;
        double[] differences = SyntheticDifferences(n, t);

        PairedComparison result = SignificanceTests.Compare(new double[n], differences);

        Assert.Equal(t, result.TStatistic, 2);
        Assert.Equal(0.05, result.TTestP, 3);
    }

    /// <summary>
    /// Wilcoxon drops zero differences before ranking.
    /// </summary>
    /// <remarks>
    /// Adding tied queries to a comparison must not change the signed-rank result, because the
    /// test is defined on the non-zero differences only. This is what keeps the vector-mode
    /// comparisons honest, where most queries are exact ties.
    /// </remarks>
    [Fact]
    public void WilcoxonIgnoresTiedQueries()
    {
        double[] baselineShort = [0, 0, 0, 0, 0, 0, 0, 0];
        double[] candidateShort = [1, 2, 3, 4, 5, 6, 7, 8];

        double[] baselineLong = [.. baselineShort, .. new double[20]];
        double[] candidateLong = [.. candidateShort, .. new double[20]];

        PairedComparison shortRun = SignificanceTests.Compare(baselineShort, candidateShort);
        PairedComparison longRun = SignificanceTests.Compare(baselineLong, candidateLong);

        Assert.Equal(shortRun.WilcoxonP, longRun.WilcoxonP, 12);
        Assert.Equal(20, longRun.Ties);
    }

    /// <summary>
    /// Reversing the comparison must flip the sign and leave the tests unchanged.
    /// </summary>
    [Fact]
    public void ComparisonIsAntisymmetric()
    {
        double[] a = [0.20, 0.55, 0.31, 0.78, 0.42, 0.11, 0.67, 0.39];
        double[] b = [0.31, 0.49, 0.44, 0.71, 0.52, 0.19, 0.61, 0.55];

        PairedComparison forward = SignificanceTests.Compare(a, b);
        PairedComparison reverse = SignificanceTests.Compare(b, a);

        Assert.Equal(forward.MeanDifference, -reverse.MeanDifference, 12);
        Assert.Equal(forward.TTestP, reverse.TTestP, 10);
        Assert.Equal(forward.WilcoxonP, reverse.WilcoxonP, 10);
        Assert.Equal(forward.Wins, reverse.Losses);
        Assert.Equal(forward.Losses, reverse.Wins);
        Assert.Equal(forward.IntervalLow, -reverse.IntervalHigh, 10);
    }

    /// <summary>
    /// The bootstrap must return the same interval on every run.
    /// </summary>
    [Fact]
    public void BootstrapIsReproducible()
    {
        double[] a = [0.20, 0.55, 0.31, 0.78, 0.42, 0.11, 0.67, 0.39, 0.28, 0.63];
        double[] b = [0.31, 0.49, 0.44, 0.71, 0.52, 0.19, 0.61, 0.55, 0.35, 0.70];

        PairedComparison first = SignificanceTests.Compare(a, b);
        PairedComparison second = SignificanceTests.Compare(a, b);

        Assert.Equal(first.IntervalLow, second.IntervalLow, 15);
        Assert.Equal(first.IntervalHigh, second.IntervalHigh, 15);

        // A different seed must move the interval, or the seed is not being used.
        PairedComparison reseeded = SignificanceTests.Compare(
            a, b, SignificanceTests.DefaultBootstrapIterations, seed: 7);

        Assert.NotEqual(first.IntervalLow, reseeded.IntervalLow);
    }

    /// <summary>
    /// The interval must bracket the observed effect.
    /// </summary>
    [Fact]
    public void IntervalContainsTheObservedEffect()
    {
        double[] a = [0.20, 0.55, 0.31, 0.78, 0.42, 0.11, 0.67, 0.39, 0.28, 0.63];
        double[] b = [0.31, 0.49, 0.44, 0.71, 0.52, 0.19, 0.61, 0.55, 0.35, 0.70];

        PairedComparison result = SignificanceTests.Compare(a, b);

        Assert.InRange(result.MeanDifference, result.IntervalLow, result.IntervalHigh);
    }

    /// <summary>
    /// Holm adjustment against R's <c>p.adjust</c>.
    /// </summary>
    /// <remarks>
    /// <c>p.adjust(c(0.01, 0.02, 0.03, 0.04, 0.05), method = "holm")</c> returns
    /// <c>0.05 0.08 0.09 0.09 0.09</c>. The repeated tail is the enforced monotonicity.
    /// </remarks>
    [Fact]
    public void HolmMatchesReferenceImplementation()
    {
        double[] adjusted = SignificanceTests.HolmAdjust([0.01, 0.02, 0.03, 0.04, 0.05]);

        Assert.Equal(0.05, adjusted[0], 10);
        Assert.Equal(0.08, adjusted[1], 10);
        Assert.Equal(0.09, adjusted[2], 10);
        Assert.Equal(0.09, adjusted[3], 10);
        Assert.Equal(0.09, adjusted[4], 10);
    }

    /// <summary>
    /// Holm must not depend on the order in which comparisons were run.
    /// </summary>
    [Fact]
    public void HolmIsIndependentOfInputOrder()
    {
        double[] forward = SignificanceTests.HolmAdjust([0.001, 0.04, 0.2, 0.6]);
        double[] shuffled = SignificanceTests.HolmAdjust([0.6, 0.2, 0.04, 0.001]);

        Assert.Equal(forward[0], shuffled[3], 12);
        Assert.Equal(forward[1], shuffled[2], 12);
        Assert.Equal(forward[2], shuffled[1], 12);
        Assert.Equal(forward[3], shuffled[0], 12);
    }

    /// <summary>
    /// Adjusted values never exceed 1 and never fall below the raw value.
    /// </summary>
    [Fact]
    public void HolmStaysInBoundsAndOnlyIncreases()
    {
        double[] raw = [0.2, 0.3, 0.44, 0.5, 0.9, 0.95];
        double[] adjusted = SignificanceTests.HolmAdjust(raw);

        for (int i = 0; i < raw.Length; i++)
        {
            Assert.InRange(adjusted[i], raw[i], 1.0);
        }
    }

    /// <summary>
    /// A length mismatch is a broken experiment, not a recoverable condition.
    /// </summary>
    [Fact]
    public void MismatchedSampleLengthsThrow()
    {
        Assert.Throws<ArgumentException>(
            () => SignificanceTests.Compare([0.1, 0.2], [0.1, 0.2, 0.3]));
    }

    /// <summary>
    /// Builds a difference vector of length <paramref name="n"/> whose paired t-statistic is
    /// <paramref name="target"/>.
    /// </summary>
    /// <remarks>
    /// Uses a symmetric two-level vector so the sample standard deviation is exactly the half
    /// spread, which makes the resulting statistic exact rather than approximate.
    /// </remarks>
    private static double[] SyntheticDifferences(int n, double target)
    {
        // Half the values sit at mean - spread, half at mean + spread, so sd = spread * sqrt(n/(n-1)).
        // Solving t = mean / (sd / sqrt(n)) for a chosen spread of 1 gives the required mean.
        double spread = 1.0;
        double standardDeviation = spread * Math.Sqrt(n / (double)(n - 1));
        double mean = target * standardDeviation / Math.Sqrt(n);

        double[] values = new double[n];
        for (int i = 0; i < n; i++)
        {
            values[i] = i < n / 2 ? mean - spread : mean + spread;
        }

        if (n % 2 == 1)
        {
            // An odd count cannot split evenly; place the odd one at the mean and rebalance so the
            // sample mean and standard deviation still hit their targets.
            values[n - 1] = mean;
            double actualMean = values.Average();
            double actualSd = Math.Sqrt(values.Sum(v => (v - actualMean) * (v - actualMean)) / (n - 1));
            double scale = standardDeviation / actualSd;
            for (int i = 0; i < n; i++)
            {
                values[i] = mean + ((values[i] - actualMean) * scale);
            }
        }

        return values;
    }
}
