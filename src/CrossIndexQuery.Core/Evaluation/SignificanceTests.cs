namespace CrossIndexQuery.Core.Evaluation;

/// <summary>
/// Paired significance testing for per-query effectiveness scores.
/// </summary>
/// <remarks>
/// <para>
/// Every comparison in this study is paired: the same 100 queries are answered by every strategy,
/// so the two samples being compared are two measurements of the same units, not two independent
/// groups. Pairing is what makes an effect of 0.04 nDCG detectable at all — per-query nDCG varies
/// enormously across queries (some queries are simply easier), and that variance cancels when the
/// test looks at within-query differences instead of at the two means.
/// </para>
/// <para>
/// Three tests are reported for each comparison rather than one, because each answers a question
/// the others cannot:
/// </para>
/// <list type="bullet">
///   <item><description>
///     The <b>paired bootstrap confidence interval</b> is the headline. It makes no assumption
///     about the shape of the per-query difference distribution — which matters here, because
///     per-query nDCG differences are neither normal nor continuous; they are a lumpy mixture with
///     a large spike at exactly zero for queries where both strategies returned the same documents.
///     An interval also communicates the magnitude and its uncertainty, where a p-value alone
///     communicates neither.
///   </description></item>
///   <item><description>
///     The <b>paired t-test</b> is reported because it is what most readers will expect, and
///     because agreement between it and the bootstrap is evidence that no distributional pathology
///     is driving the result. Disagreement between them is a signal worth investigating.
///   </description></item>
///   <item><description>
///     The <b>Wilcoxon signed-rank test</b> drops the magnitude of each difference and keeps only
///     its sign and rank, so a single query with a huge swing cannot manufacture significance.
///     It is the conservative check.
///   </description></item>
/// </list>
/// <para>
/// Reporting all three costs nothing and removes the accusation that a particular test was chosen
/// after seeing which one gave the desired answer.
/// </para>
/// </remarks>
public static class SignificanceTests
{
    /// <summary>
    /// Iterations for the paired bootstrap.
    /// </summary>
    /// <remarks>
    /// 10,000 is the conventional floor for a percentile interval reported to three decimals. The
    /// Monte Carlo error on a 95% interval bound at this count is small relative to the effects
    /// being measured, and the whole resampling pass over 100 queries costs milliseconds.
    /// </remarks>
    public const int DefaultBootstrapIterations = 10_000;

    /// <summary>
    /// Seed for the bootstrap resampler.
    /// </summary>
    /// <remarks>
    /// Fixed, and deliberately so. A confidence interval that moves when the study is re-run is not
    /// reproducible, and reproducibility is the entire claim this repository makes about its
    /// numbers. Anyone who wants to confirm the interval is not an artifact of this particular seed
    /// can pass a different one.
    /// </remarks>
    public const int DefaultSeed = 20260904;

    /// <summary>
    /// Compares a candidate against a baseline over the same queries.
    /// </summary>
    /// <param name="baseline">Per-query scores for the baseline, ordered by query.</param>
    /// <param name="candidate">Per-query scores for the candidate, in the same query order.</param>
    /// <param name="iterations">Bootstrap resamples to draw.</param>
    /// <param name="seed">Resampler seed.</param>
    /// <exception cref="ArgumentException">The two samples are not the same length, or are empty.</exception>
    public static PairedComparison Compare(
        IReadOnlyList<double> baseline,
        IReadOnlyList<double> candidate,
        int iterations = DefaultBootstrapIterations,
        int seed = DefaultSeed)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);

        if (baseline.Count != candidate.Count)
        {
            throw new ArgumentException(
                $"Paired tests need one score per query on both sides; got {baseline.Count} "
                + $"baseline and {candidate.Count} candidate scores. A length mismatch usually "
                + "means the two runs did not answer the same query set.",
                nameof(candidate));
        }

        if (baseline.Count == 0)
        {
            throw new ArgumentException("No queries to compare.", nameof(baseline));
        }

        int n = baseline.Count;
        double[] differences = new double[n];
        for (int i = 0; i < n; i++)
        {
            differences[i] = candidate[i] - baseline[i];
        }

        double mean = differences.Average();

        int wins = differences.Count(d => d > 0);
        int losses = differences.Count(d => d < 0);
        int ties = n - wins - losses;

        (double low, double high) = BootstrapInterval(differences, iterations, seed);
        (double t, double tP) = PairedT(differences);
        double wilcoxonP = WilcoxonSignedRank(differences);

        return new PairedComparison(
            n,
            mean,
            low,
            high,
            t,
            tP,
            wilcoxonP,
            wins,
            losses,
            ties,
            EffectSize(differences, mean));
    }

    /// <summary>
    /// Applies the Holm step-down correction to a family of p-values.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This study compares roughly ten strategies against one baseline within each retrieval mode.
    /// At an uncorrected threshold of 0.05, a family that size is expected to produce a false
    /// positive about 40% of the time, so any single "p &lt; 0.05" in the raw table is weak evidence
    /// on its own. Correction is not optional here; it is the difference between a result and a
    /// coincidence.
    /// </para>
    /// <para>
    /// Holm is used rather than plain Bonferroni because it controls the same family-wise error
    /// rate while rejecting at least as many hypotheses — it is uniformly more powerful, with no
    /// additional assumptions. Reporting the weaker correction would understate real effects for
    /// no methodological gain.
    /// </para>
    /// <para>
    /// Returned values are adjusted p-values, monotone in the input ordering, each capped at 1.0.
    /// Compare them directly against the unadjusted significance level.
    /// </para>
    /// </remarks>
    public static double[] HolmAdjust(IReadOnlyList<double> pValues)
    {
        ArgumentNullException.ThrowIfNull(pValues);

        int m = pValues.Count;
        double[] adjusted = new double[m];
        if (m == 0)
        {
            return adjusted;
        }

        int[] order = Enumerable.Range(0, m).OrderBy(i => pValues[i]).ToArray();

        double running = 0.0;
        for (int rank = 0; rank < m; rank++)
        {
            int index = order[rank];
            double scaled = (m - rank) * pValues[index];

            // Enforced monotonicity. Without it a later, larger raw p-value could receive a smaller
            // adjusted value than an earlier one, which would let a weaker result appear stronger.
            running = Math.Max(running, scaled);
            adjusted[index] = Math.Min(1.0, running);
        }

        return adjusted;
    }

    /// <summary>
    /// Percentile bootstrap interval over the mean paired difference.
    /// </summary>
    /// <remarks>
    /// Resampling is over queries, with replacement, preserving the pairing — each draw takes both
    /// strategies' score for the same query. Resampling the two sides independently would destroy
    /// the pairing and inflate the interval to roughly the unpaired width.
    /// </remarks>
    private static (double Low, double High) BootstrapInterval(
        double[] differences,
        int iterations,
        int seed)
    {
        int n = differences.Length;
        var random = new Random(seed);
        double[] means = new double[iterations];

        for (int iteration = 0; iteration < iterations; iteration++)
        {
            double sum = 0.0;
            for (int draw = 0; draw < n; draw++)
            {
                sum += differences[random.Next(n)];
            }

            means[iteration] = sum / n;
        }

        Array.Sort(means);

        return (Percentile(means, 0.025), Percentile(means, 0.975));
    }

    /// <summary>
    /// Linear-interpolated percentile of a sorted sample.
    /// </summary>
    private static double Percentile(double[] sorted, double fraction)
    {
        if (sorted.Length == 1)
        {
            return sorted[0];
        }

        double position = fraction * (sorted.Length - 1);
        int lower = (int)Math.Floor(position);
        int upper = Math.Min(lower + 1, sorted.Length - 1);
        double weight = position - lower;

        return (sorted[lower] * (1 - weight)) + (sorted[upper] * weight);
    }

    /// <summary>
    /// Two-tailed paired t-test on the difference vector.
    /// </summary>
    private static (double T, double P) PairedT(double[] differences)
    {
        int n = differences.Length;
        if (n < 2)
        {
            return (0.0, 1.0);
        }

        double mean = differences.Average();
        double sumSquares = differences.Sum(d => (d - mean) * (d - mean));
        double standardDeviation = Math.Sqrt(sumSquares / (n - 1));

        if (standardDeviation == 0.0)
        {
            // Every query moved by exactly the same amount, including the case where nothing moved
            // at all. A zero-variance difference vector has no t-statistic; report the honest
            // "no evidence of difference" rather than an infinity.
            return mean == 0.0 ? (0.0, 1.0) : (double.PositiveInfinity, 0.0);
        }

        double t = mean / (standardDeviation / Math.Sqrt(n));

        return (t, StudentTTwoTailed(Math.Abs(t), n - 1));
    }

    /// <summary>
    /// Wilcoxon signed-rank test, normal approximation with tie correction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The normal approximation is appropriate at this study's sample size; with 100 queries and
    /// rarely fewer than 20 non-zero differences it is accurate well past the third decimal.
    /// </para>
    /// <para>
    /// Zero differences are dropped before ranking, per Wilcoxon's original treatment. This matters
    /// here more than it usually does: in the vector-only mode most strategies return an identical
    /// list to the baseline on most queries, so the majority of differences are exactly zero and
    /// the effective sample size is far below 100.
    /// </para>
    /// </remarks>
    private static double WilcoxonSignedRank(double[] differences)
    {
        double[] nonZero = differences.Where(d => d != 0.0).ToArray();
        int n = nonZero.Length;
        if (n < 2)
        {
            return 1.0;
        }

        int[] order = Enumerable.Range(0, n).OrderBy(i => Math.Abs(nonZero[i])).ToArray();
        double[] ranks = new double[n];

        // Midranks for tied absolute differences, and the tie sizes needed for the variance
        // correction. Ties are common because per-query nDCG differences take a limited set of
        // values at a fixed cutoff.
        var tieSizes = new List<int>();
        int position = 0;
        while (position < n)
        {
            int runEnd = position;
            while (runEnd + 1 < n
                && Math.Abs(nonZero[order[runEnd + 1]]) == Math.Abs(nonZero[order[position]]))
            {
                runEnd++;
            }

            int runLength = runEnd - position + 1;
            double midRank = ((position + 1) + (runEnd + 1)) / 2.0;
            for (int i = position; i <= runEnd; i++)
            {
                ranks[order[i]] = midRank;
            }

            if (runLength > 1)
            {
                tieSizes.Add(runLength);
            }

            position = runEnd + 1;
        }

        double positiveRankSum = 0.0;
        for (int i = 0; i < n; i++)
        {
            if (nonZero[i] > 0)
            {
                positiveRankSum += ranks[i];
            }
        }

        double expected = n * (n + 1) / 4.0;
        double variance = n * (n + 1) * ((2.0 * n) + 1) / 24.0;

        foreach (int tie in tieSizes)
        {
            variance -= tie * ((double)tie * tie - 1) / 48.0;
        }

        if (variance <= 0.0)
        {
            return 1.0;
        }

        // Continuity correction, applied toward the mean so it can only make the test more
        // conservative.
        double deviation = Math.Abs(positiveRankSum - expected);
        double z = Math.Max(0.0, deviation - 0.5) / Math.Sqrt(variance);

        return 2.0 * (1.0 - NormalCdf(z));
    }

    /// <summary>
    /// Standardised mean difference for paired samples (Cohen's d on the differences).
    /// </summary>
    private static double EffectSize(double[] differences, double mean)
    {
        int n = differences.Length;
        if (n < 2)
        {
            return 0.0;
        }

        double sumSquares = differences.Sum(d => (d - mean) * (d - mean));
        double standardDeviation = Math.Sqrt(sumSquares / (n - 1));

        return standardDeviation == 0.0 ? 0.0 : mean / standardDeviation;
    }

    /// <summary>
    /// Two-tailed Student-t tail probability via the regularised incomplete beta function.
    /// </summary>
    private static double StudentTTwoTailed(double t, int degreesOfFreedom)
    {
        double x = degreesOfFreedom / (degreesOfFreedom + (t * t));

        return RegularisedIncompleteBeta(degreesOfFreedom / 2.0, 0.5, x);
    }

    /// <summary>
    /// Standard normal cumulative distribution, via the error function.
    /// </summary>
    private static double NormalCdf(double z) => 0.5 * (1.0 + Erf(z / Math.Sqrt(2.0)));

    /// <summary>
    /// Abramowitz and Stegun 7.1.26 rational approximation to the error function.
    /// </summary>
    /// <remarks>
    /// Maximum absolute error 1.5e-7, which is far below the precision at which any p-value in this
    /// study is reported or acted upon.
    /// </remarks>
    private static double Erf(double x)
    {
        double sign = x < 0 ? -1.0 : 1.0;
        x = Math.Abs(x);

        const double A1 = 0.254829592;
        const double A2 = -0.284496736;
        const double A3 = 1.421413741;
        const double A4 = -1.453152027;
        const double A5 = 1.061405429;
        const double P = 0.3275911;

        double t = 1.0 / (1.0 + (P * x));
        double y = 1.0 - ((((((((A5 * t) + A4) * t) + A3) * t) + A2) * t + A1) * t * Math.Exp(-x * x));

        return sign * y;
    }

    /// <summary>
    /// Regularised incomplete beta function by the continued fraction of Lentz.
    /// </summary>
    private static double RegularisedIncompleteBeta(double a, double b, double x)
    {
        if (x <= 0.0)
        {
            return 0.0;
        }

        if (x >= 1.0)
        {
            return 1.0;
        }

        double front = Math.Exp(
            LogGamma(a + b) - LogGamma(a) - LogGamma(b)
            + (a * Math.Log(x)) + (b * Math.Log(1.0 - x)));

        // The continued fraction converges quickly only on one side of the distribution; the
        // symmetry relation moves the evaluation to that side when necessary.
        if (x > (a + 1.0) / (a + b + 2.0))
        {
            return 1.0 - (RegularisedIncompleteBeta(b, a, 1.0 - x));
        }

        const double Tiny = 1e-30;
        const double Epsilon = 1e-12;

        double f = 1.0;
        double c = 1.0;
        double d = 0.0;

        for (int i = 0; i <= 300; i++)
        {
            int m = i / 2;
            double numerator;

            if (i == 0)
            {
                numerator = 1.0;
            }
            else if (i % 2 == 0)
            {
                numerator = m * (b - m) * x / ((a + (2.0 * m) - 1.0) * (a + (2.0 * m)));
            }
            else
            {
                numerator = -((a + m) * (a + b + m) * x) / ((a + (2.0 * m)) * (a + (2.0 * m) + 1.0));
            }

            d = 1.0 + (numerator * d);
            if (Math.Abs(d) < Tiny)
            {
                d = Tiny;
            }

            d = 1.0 / d;

            c = 1.0 + (numerator / c);
            if (Math.Abs(c) < Tiny)
            {
                c = Tiny;
            }

            double delta = c * d;
            f *= delta;

            if (Math.Abs(1.0 - delta) < Epsilon)
            {
                break;
            }
        }

        return front * (f - 1.0) / a;
    }

    /// <summary>
    /// Lanczos approximation to the log gamma function.
    /// </summary>
    private static double LogGamma(double x)
    {
        double[] coefficients =
        [
            76.18009172947146,
            -86.50532032941677,
            24.01409824083091,
            -1.231739572450155,
            0.1208650973866179e-2,
            -0.5395239384953e-5,
        ];

        double y = x;
        double tmp = x + 5.5;
        tmp -= (x + 0.5) * Math.Log(tmp);

        double series = 1.000000000190015;
        for (int j = 0; j < 6; j++)
        {
            series += coefficients[j] / ++y;
        }

        return -tmp + Math.Log(2.5066282746310005 * series / x);
    }
}

/// <summary>
/// The outcome of one paired comparison against a baseline.
/// </summary>
/// <param name="Queries">Number of paired observations.</param>
/// <param name="MeanDifference">Candidate minus baseline, averaged over queries.</param>
/// <param name="IntervalLow">Lower bound of the 95% paired bootstrap interval.</param>
/// <param name="IntervalHigh">Upper bound of the 95% paired bootstrap interval.</param>
/// <param name="TStatistic">Paired t-statistic.</param>
/// <param name="TTestP">Two-tailed p-value from the paired t-test.</param>
/// <param name="WilcoxonP">Two-tailed p-value from the Wilcoxon signed-rank test.</param>
/// <param name="Wins">Queries where the candidate scored higher.</param>
/// <param name="Losses">Queries where the baseline scored higher.</param>
/// <param name="Ties">Queries where the two scored identically.</param>
/// <param name="EffectSize">Cohen's d computed on the paired differences.</param>
public sealed record PairedComparison(
    int Queries,
    double MeanDifference,
    double IntervalLow,
    double IntervalHigh,
    double TStatistic,
    double TTestP,
    double WilcoxonP,
    int Wins,
    int Losses,
    int Ties,
    double EffectSize)
{
    /// <summary>
    /// Whether the 95% interval excludes zero.
    /// </summary>
    /// <remarks>
    /// Preferred over a p-value threshold when summarising, because it is a statement about the
    /// range of effects the data are consistent with rather than about a single decision boundary.
    /// </remarks>
    public bool IntervalExcludesZero => (IntervalLow > 0.0 && IntervalHigh > 0.0)
        || (IntervalLow < 0.0 && IntervalHigh < 0.0);
}
