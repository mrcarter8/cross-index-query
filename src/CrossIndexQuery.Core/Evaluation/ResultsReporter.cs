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
            + "judgedNdcg,judgedCoverage,queries,computeUnits,latencyMs,stripeMix");

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

            sb.AppendLine("| Strategy | nDCG@10 | Recall@10 | RBO | Kendall τ | Queries | Compute units | p50 ms | p95 ms |");
            sb.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");

            foreach (StrategySummary s in summaries)
            {
                sb.Append("| `").Append(s.Strategy).Append("` | ")
                  .Append(F3(s.Ndcg)).Append(" | ")
                  .Append(F3(s.Recall)).Append(" | ")
                  .Append(F3(s.RankBiasedOverlap)).Append(" | ")
                  .Append(F3(s.KendallTau)).Append(" | ")
                  .Append(F1(s.QueriesPerRequest)).Append(" | ")
                  .Append(F4(s.ComputeUnits)).Append(" | ")
                  .Append(F0(s.LatencyP50Ms)).Append(" | ")
                  .Append(F0(s.LatencyP95Ms)).AppendLine(" |");
            }

            sb.AppendLine();
            AppendSpanBreakdown(sb, byMode);
        }

        await File.WriteAllTextAsync(path, sb.ToString(), cancellationToken).ConfigureAwait(false);
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

    private static string F4(double value) => value.ToString("F4", CultureInfo.InvariantCulture);
}
