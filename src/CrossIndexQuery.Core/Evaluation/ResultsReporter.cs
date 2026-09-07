using System.Globalization;
using System.Text;

namespace CrossIndexQuery.Core.Evaluation;

/// <summary>
/// Writes evaluation results as a machine-readable CSV and a human-readable Markdown report.
/// </summary>
/// <remarks>
/// Both, deliberately. The CSV is the evidence — every query, every strategy, every metric, so a
/// reader who disbelieves a conclusion can recompute it or slice it differently. The Markdown is the
/// argument, and its job is to make the trade-offs legible without anyone having to open a
/// spreadsheet.
/// </remarks>
public static class ResultsReporter
{
    public static async Task WriteCsvAsync(
        string path,
        IReadOnlyList<EvaluationRecord> records,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(records);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        var sb = new StringBuilder();
        sb.AppendLine(
            "queryId,query,shape,span,intent,mode,strategy,ndcg,recall,jaccard,kendallTau,rbo,"
            + "judgedNdcg,judgedCoverage,queries,computeUnits,modelTokens,latencyMs,stripeMix");

        foreach (EvaluationRecord r in records)
        {
            string mix = string.Join(
                " | ", r.StripeContribution.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                    .Select(kv => $"{kv.Key}={kv.Value}"));

            sb.Append(Csv(r.QueryId)).Append(',')
              .Append(Csv(r.QueryText)).Append(',')
              .Append(r.Shape).Append(',')
              .Append(r.Span).Append(',')
              .Append(r.Intent).Append(',')
              .Append(r.Mode).Append(',')
              .Append(Csv(r.Strategy)).Append(',')
              .Append(Num(r.Ndcg)).Append(',')
              .Append(Num(r.Recall)).Append(',')
              .Append(Num(r.Jaccard)).Append(',')
              .Append(Num(r.KendallTau)).Append(',')
              .Append(Num(r.RankBiasedOverlap)).Append(',')
              .Append(r.JudgedNdcg is { } jn ? Num(jn) : string.Empty).Append(',')
              .Append(r.JudgedCoverage is { } jc ? Num(jc) : string.Empty).Append(',')
              .Append(r.QueryCount.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(Num(r.ComputeUnits)).Append(',')

              // Blank rather than 0 when the strategy consumes no model tokens, so a reader cannot
              // mistake "this meter did not run" for "this meter ran and charged nothing".
              .Append(r.ModelTokens is { } mt ? mt.ToString(CultureInfo.InvariantCulture) : string.Empty)
              .Append(',')
              .Append(Num(r.LatencyMs)).Append(',')
              .Append(Csv(mix))
              .AppendLine();
        }

        await File.WriteAllTextAsync(path, sb.ToString(), cancellationToken).ConfigureAwait(false);
    }

    public static async Task WriteMarkdownAsync(
        string path,
        IReadOnlyList<EvaluationRecord> records,
        string serviceDescription,
        bool semanticRanker = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(records);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        var sb = new StringBuilder();
        sb.AppendLine("# Cross-index fusion results").AppendLine();
        sb.Append("Measured against ").Append(serviceDescription)
          .Append(" on ").Append(DateTime.UtcNow.ToString("u", CultureInfo.InvariantCulture))
          .AppendLine(".").AppendLine();

        sb.AppendLine(
            "Every score compares a fused two-index result against the same query answered by a single")
          .AppendLine(
            "index holding the whole corpus. `1.000` means the split was invisible; lower means striping")
          .AppendLine(
            "cost relevance that the strategy did not recover. These are fidelity numbers, not absolute")
          .AppendLine("relevance judgements.").AppendLine();

        foreach (IGrouping<string, EvaluationRecord> byMode in records.GroupBy(r => r.Mode))
        {
            sb.Append("## ").Append(byMode.Key).AppendLine().AppendLine();

            List<StrategySummary> summaries =
            [
                .. byMode.GroupBy(r => r.Strategy)
                    .Select(g => StrategySummary.Aggregate(byMode.Key, g.Key, [.. g]))
                    .OrderByDescending(s => s.Ndcg)
            ];

            sb.AppendLine("| Strategy | nDCG@10 | Recall@10 | RBO | Kendall τ | Queries | Compute units | Model tokens | p50 ms | p95 ms |");
            sb.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");

            foreach (StrategySummary s in summaries)
            {
                sb.Append("| `").Append(s.Strategy).Append("` | ")
                  .Append(F3(s.Ndcg)).Append(" | ")
                  .Append(F3(s.Recall)).Append(" | ")
                  .Append(F3(s.RankBiasedOverlap)).Append(" | ")
                  .Append(F3(s.KendallTau)).Append(" | ")
                  .Append(F1(s.QueriesPerRequest)).Append(" | ")
                  .Append(F4(s.ComputeUnits)).Append(" | ")

                  // An em dash where no model meter ran at all, which is not the same claim as zero.
                  .Append(s.ModelTokens is { } tokens
                      ? tokens.ToString("N0", CultureInfo.InvariantCulture)
                      : "—")
                  .Append(" | ")
                  .Append(F0(s.LatencyP50Ms)).Append(" | ")
                  .Append(F0(s.LatencyP95Ms)).AppendLine(" |");
            }

            sb.AppendLine();
            AppendSpanBreakdown(sb, byMode);
            AppendTradeoff(sb, byMode, semanticRanker);
            AppendSignificance(sb, byMode);
        }

        await File.WriteAllTextAsync(path, sb.ToString(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reports quality, cost and latency against the single index on one line per strategy.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The table the whole study exists to produce. Quality alone cannot answer "should I split",
    /// because a technique that recovers the relevance loss by paying for a reranker on every query
    /// has not made splitting free — it has moved the cost to a different meter. Putting all three
    /// axes on one row is what stops that trade from being invisible.
    /// </para>
    /// <para>
    /// Everything is expressed as a multiple of the single index rather than in absolute units.
    /// Absolute figures are in the columns beside them for anyone who wants them, but the multiple
    /// is what survives a change of corpus, tier or region — and it is the form in which the
    /// answer is actually useful: "this costs 70x more and returns 4% better" is a decision, where
    /// "$7.40 per thousand queries" is a number needing a second number to interpret.
    /// </para>
    /// </remarks>
    private static void AppendTradeoff(
        StringBuilder sb,
        IEnumerable<EvaluationRecord> records,
        bool semanticRanker)
    {
        List<StrategySummary> summaries =
        [
            .. records.GroupBy(r => r.Strategy)
                .Select(g => StrategySummary.Aggregate(g.First().Mode, g.Key, [.. g]))
        ];

        StrategySummary? baseline = summaries
            .FirstOrDefault(s => s.Strategy == EvaluationHarness.SingleIndexBaseline);

        if (baseline is null || summaries.Count < 2)
        {
            return;
        }

        CostModel model = CostModel.Default;

        double Cost(StrategySummary s) => model.PerThousandQueries(
            s.ComputeUnits,

            // Every request in a semantic run invokes the ranker, so the number of ranker
            // invocations is the number of requests. A strategy that fans out to two indexes
            // therefore pays the ranker twice for one user query, which is a real cost of splitting
            // and one that no client-side merge can avoid.
            semanticRanker ? (int)Math.Round(s.QueriesPerRequest) : 0,
            (int)Math.Round(s.ModelTokens ?? 0));

        double baselineCost = Cost(baseline);
        double baselineQuality = baseline.JudgedNdcg ?? 0;
        double baselineLatency = baseline.LatencyP50Ms;

        sb.AppendLine("### Quality, cost and latency").AppendLine();
        sb.Append("Cost assumes $")
          .Append(F2(model.DollarsPerComputeUnitHour))
          .Append(" per compute-unit hour and $")
          .Append(F2(model.DollarsPerThousandSemanticQueries))
          .AppendLine(" per 1K semantic ranker queries, both read from the Azure retail")
          .Append("prices API, plus $")
          .Append(F2(model.DollarsPerMillionTokens))
          .AppendLine(" per million model tokens, which is an assumption rather than a")
          .AppendLine("measurement — substitute your own rate and the token-bearing rows move with it.")
          .AppendLine();

        sb.AppendLine(
            "| Strategy | Quality | vs single | $/1K queries | vs single | p50 ms | vs single | Tokens/query |");
        sb.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");

        foreach (StrategySummary s in summaries.OrderByDescending(s => s.JudgedNdcg ?? -1))
        {
            double cost = Cost(s);
            double quality = s.JudgedNdcg ?? 0;

            sb.Append("| `").Append(s.Strategy).Append("` | ")
              .Append(F3(quality)).Append(" | ")
              .Append(Multiple(quality, baselineQuality)).Append(" | ")
              .Append(CostModel.Format(cost)).Append(" | ")
              .Append(Multiple(cost, baselineCost)).Append(" | ")
              .Append(F0(s.LatencyP50Ms)).Append(" | ")
              .Append(Multiple(s.LatencyP50Ms, baselineLatency)).Append(" | ")
              .Append(s.ModelTokens is { } t ? t.ToString("N0", CultureInfo.InvariantCulture) : "—")
              .AppendLine(" |");
        }

        sb.AppendLine();
        sb.AppendLine(
            "A multiple below 1.00x in the quality column is relevance lost; above 1.00x in the cost")
          .AppendLine(
            "or latency columns is what recovering it charged you. Read the three together — a row")
          .AppendLine("that wins on quality alone has not necessarily won.")
          .AppendLine();
    }

    /// <summary>
    /// Formats a ratio against the baseline, guarding the degenerate denominator.
    /// </summary>
    private static string Multiple(double value, double baseline)
    {
        if (baseline <= 0)
        {
            return "—";
        }

        double ratio = value / baseline;

        // Large multiples are the interesting ones here and three decimals on them is noise.
        return ratio >= 100
            ? ratio.ToString("F0", CultureInfo.InvariantCulture) + "x"
            : ratio.ToString("F2", CultureInfo.InvariantCulture) + "x";
    }
    /// <remarks>
    /// <para>
    /// Emitted only when judgments exist, because significance against the oracle-fidelity metric
    /// would be a test of whether a strategy reproduces the baseline's ordering, not of whether it
    /// retrieves better documents. The fidelity columns already answer the first question and the
    /// baseline scores a definitional 1.000 there, so a test against it is vacuous.
    /// </para>
    /// <para>
    /// The comparison is paired by query id rather than by position, so a strategy that skipped a
    /// query cannot silently shift the alignment and compare unrelated pairs.
    /// </para>
    /// </remarks>
    private static void AppendSignificance(StringBuilder sb, IEnumerable<EvaluationRecord> records)
    {
        EvaluationRecord[] judged = [.. records.Where(r => r.JudgedNdcg is not null)];
        if (judged.Length == 0)
        {
            return;
        }

        Dictionary<string, double> baseline = judged
            .Where(r => r.Strategy == EvaluationHarness.SingleIndexBaseline)
            .ToDictionary(r => r.QueryId, r => r.JudgedNdcg!.Value, StringComparer.Ordinal);

        if (baseline.Count == 0)
        {
            return;
        }

        var comparisons = new List<(string Strategy, PairedComparison Result)>();

        foreach (IGrouping<string, EvaluationRecord> byStrategy in judged
            .Where(r => r.Strategy != EvaluationHarness.SingleIndexBaseline)
            .GroupBy(r => r.Strategy, StringComparer.Ordinal))
        {
            var baselineScores = new List<double>();
            var candidateScores = new List<double>();

            foreach (EvaluationRecord record in byStrategy.OrderBy(r => r.QueryId, StringComparer.Ordinal))
            {
                if (baseline.TryGetValue(record.QueryId, out double baselineScore))
                {
                    baselineScores.Add(baselineScore);
                    candidateScores.Add(record.JudgedNdcg!.Value);
                }
            }

            if (baselineScores.Count > 1)
            {
                comparisons.Add(
                    (byStrategy.Key, SignificanceTests.Compare(baselineScores, candidateScores)));
            }
        }

        if (comparisons.Count == 0)
        {
            return;
        }

        // Corrected across every strategy tested in this mode, which is the family a reader is
        // actually scanning when they look for the winners in the table above.
        double[] adjusted = SignificanceTests.HolmAdjust(
            [.. comparisons.Select(c => c.Result.TTestP)]);

        sb.AppendLine("### Significance against the single index").AppendLine();
        sb.AppendLine(
            "Paired over the same queries. `Δ judged` is mean judged nDCG@10 minus the single index;")
          .AppendLine(
            "the interval is a 95% paired bootstrap over 10,000 resamples. `p (Holm)` is the paired")
          .AppendLine(
            "t-test corrected across every strategy in this mode; `p (W)` is the uncorrected Wilcoxon")
          .AppendLine(
            "signed-rank, shown because it is the conservative check. W/L/T counts queries where the")
          .AppendLine("strategy beat, lost to, or tied the single index.").AppendLine();

        sb.AppendLine("| Strategy | Δ judged | 95% interval | d | p (Holm) | p (W) | W/L/T |");
        sb.AppendLine("| --- | ---: | :---: | ---: | ---: | ---: | :---: |");

        var ordered = comparisons
            .Select((c, i) => (c.Strategy, c.Result, Adjusted: adjusted[i]))
            .OrderByDescending(c => c.Result.MeanDifference);

        foreach ((string strategy, PairedComparison result, double adjustedP) in ordered)
        {
            sb.Append("| `").Append(strategy).Append("` | ")
              .Append(Signed(result.MeanDifference)).Append(" | ")
              .Append('[').Append(Signed(result.IntervalLow)).Append(", ")
              .Append(Signed(result.IntervalHigh)).Append("] | ")
              .Append(F2(result.EffectSize)).Append(" | ")
              .Append(P(adjustedP)).Append(" | ")
              .Append(P(result.WilcoxonP)).Append(" | ")
              .Append(result.Wins).Append('/').Append(result.Losses).Append('/').Append(result.Ties)
              .AppendLine(" |");
        }

        sb.AppendLine();
        sb.AppendLine(
            "An interval that spans zero means the data are consistent with no difference, whatever")
          .AppendLine(
            "the point estimate suggests. Treat those rows as parity, not as small effects.")
          .AppendLine();
    }

    /// <summary>
    /// Reports the same strategies split by whether a query's answers straddle the stripe boundary.
    /// </summary>
    /// <remarks>
    /// The most informative table in the report, and the one an average would destroy. A query whose
    /// good answers all live in one stripe barely exercises fusion at all — the other stripe returns
    /// nothing worth ranking, and every strategy looks competent. The damage is concentrated
    /// entirely in queries that straddle the split, and a single blended figure dilutes that effect
    /// in proportion to how many easy queries happen to be in the set.
    /// </remarks>
    private static void AppendSpanBreakdown(StringBuilder sb, IEnumerable<EvaluationRecord> records)
    {
        sb.AppendLine("### By query span").AppendLine();
        sb.AppendLine(
            "Stripe-local queries find their answers in one index; cross-stripe queries need both.")
          .AppendLine("Fusion quality is decided by the second column.").AppendLine();

        var byStrategy = records
            .GroupBy(r => r.Strategy)
            .Select(g => new
            {
                Strategy = g.Key,
                Local = Average(g, QuerySpan.StripeLocal),
                Cross = Average(g, QuerySpan.CrossStripe),
            })
            .OrderByDescending(x => x.Cross);

        sb.AppendLine("| Strategy | nDCG stripe-local | nDCG cross-stripe | Δ |");
        sb.AppendLine("| --- | ---: | ---: | ---: |");

        foreach (var row in byStrategy)
        {
            sb.Append("| `").Append(row.Strategy).Append("` | ")
              .Append(F3(row.Local)).Append(" | ")
              .Append(F3(row.Cross)).Append(" | ")
              .Append(F3(row.Cross - row.Local)).AppendLine(" |");
        }

        sb.AppendLine();
    }

    private static double Average(IEnumerable<EvaluationRecord> records, QuerySpan span)
    {
        double[] values = [.. records.Where(r => r.Span == span).Select(r => r.Ndcg)];
        return values.Length == 0 ? 0d : values.Average();
    }

    private static string Csv(string value) =>
        value.Contains(',', StringComparison.Ordinal)
        || value.Contains('"', StringComparison.Ordinal)
        || value.Contains('\n', StringComparison.Ordinal)
            ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : value;

    private static string Num(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static string F0(double value) => value.ToString("F0", CultureInfo.InvariantCulture);

    private static string F1(double value) => value.ToString("F1", CultureInfo.InvariantCulture);

    private static string F3(double value) => value.ToString("F3", CultureInfo.InvariantCulture);

    private static string F2(double value) => value.ToString("F2", CultureInfo.InvariantCulture);

    /// <summary>
    /// Formats a difference with an explicit sign, so a negative effect cannot be skimmed as positive.
    /// </summary>
    private static string Signed(double value) =>
        value.ToString("+0.000;-0.000;0.000", CultureInfo.InvariantCulture);

    /// <summary>
    /// Formats a p-value, collapsing anything below the resolution of the test rather than
    /// printing a precision the procedure does not have.
    /// </summary>
    private static string P(double value) => value < 0.0001
        ? "&lt;0.0001"
        : value.ToString("F4", CultureInfo.InvariantCulture);

    private static string F4(double value) => value.ToString("F4", CultureInfo.InvariantCulture);
}
