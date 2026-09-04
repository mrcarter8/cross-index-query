using System.Globalization;
using CrossIndexQuery.Core.Evaluation;

namespace CrossIndexQuery.Cli.Commands;

/// <summary>
/// Runs a paired significance test between any two strategies in a committed results file.
/// </summary>
/// <remarks>
/// <para>
/// The point of this command is that it needs no Azure resources at all. Every per-query score this
/// study reports is committed as CSV, so a reader who distrusts a conclusion can re-derive the
/// statistics themselves in seconds, on their own machine, without a subscription, without paying
/// for a single query, and without trusting that the numbers in the report were produced by the
/// code in the repository.
/// </para>
/// <para>
/// It also allows comparisons the results table does not print. The table compares everything to
/// the single index, which is the question most readers arrive with. It is not the only question:
/// separating a strategy from its control means comparing two striped strategies to each other,
/// and that comparison is what decides whether an effect is attributable to the mechanism being
/// advertised or to something incidental that came along with it.
/// </para>
/// </remarks>
public sealed class CompareCommand
{
    /// <summary>
    /// Compares two strategies over the queries they both answered.
    /// </summary>
    /// <param name="resultsPath">A results CSV written by <c>evaluate</c>.</param>
    /// <param name="baselineName">Strategy treated as the reference.</param>
    /// <param name="candidateName">Strategy measured against it.</param>
    /// <param name="metric">Column to compare; judged relevance by default.</param>
    /// <param name="mode">Restrict to one retrieval mode, or compare within each mode present.</param>
    public async Task<int> RunAsync(
        string resultsPath,
        string baselineName,
        string candidateName,
        string metric,
        string? mode,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(resultsPath))
        {
            Console.Error.WriteLine($"{resultsPath} not found. Run 'evaluate' first, or point at one "
                + "of the committed files in results/.");
            return 1;
        }

        List<Row> rows = await ReadAsync(resultsPath, metric, cancellationToken).ConfigureAwait(false);

        if (rows.Count == 0)
        {
            Console.Error.WriteLine(
                $"No usable rows in {resultsPath}. Either the file is empty or column '{metric}' "
                + "holds no values — judged metrics are blank until 'judge collect' has run.");
            return 1;
        }

        string[] modes = mode is null
            ? [.. rows.Select(r => r.Mode).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)]
            : [mode];

        int compared = 0;

        foreach (string currentMode in modes)
        {
            Row[] inMode = [.. rows.Where(r => r.Mode.Equals(currentMode, StringComparison.OrdinalIgnoreCase))];

            Dictionary<string, double> baseline = Scores(inMode, baselineName);
            Dictionary<string, double> candidate = Scores(inMode, candidateName);

            if (baseline.Count == 0 || candidate.Count == 0)
            {
                continue;
            }

            // Paired on query id, and restricted to queries both strategies actually answered.
            // Comparing over a union would silently score a missing run as zero and manufacture an
            // effect out of an absence.
            string[] shared = [.. baseline.Keys.Intersect(candidate.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal)];

            if (shared.Length < 2)
            {
                Console.Error.WriteLine(
                    $"{currentMode}: only {shared.Length} shared quer{(shared.Length == 1 ? "y" : "ies")} "
                    + "between the two strategies; nothing to test.");
                continue;
            }

            PairedComparison result = SignificanceTests.Compare(
                [.. shared.Select(q => baseline[q])],
                [.. shared.Select(q => candidate[q])]);

            Report(currentMode, baselineName, candidateName, metric, result, shared.Length);
            compared++;
        }

        if (compared == 0)
        {
            Console.Error.WriteLine(
                $"Found no mode containing both '{baselineName}' and '{candidateName}'. "
                + $"Strategies present: {string.Join(", ", rows.Select(r => r.Strategy).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))}.");
            return 1;
        }

        return 0;
    }

    private static void Report(
        string mode,
        string baselineName,
        string candidateName,
        string metric,
        PairedComparison result,
        int queries)
    {
        Console.WriteLine();
        Console.WriteLine($"=== {mode}: {candidateName} vs {baselineName} ({metric}) ===");
        Console.WriteLine($"  queries paired       {queries}");
        Console.WriteLine($"  mean difference      {result.MeanDifference:+0.0000;-0.0000;0.0000}");
        Console.WriteLine(
            $"  95% interval         [{result.IntervalLow:+0.0000;-0.0000;0.0000}, "
            + $"{result.IntervalHigh:+0.0000;-0.0000;0.0000}]");
        Console.WriteLine($"  Cohen's d            {result.EffectSize:F3}");
        Console.WriteLine($"  paired t             t={result.TStatistic:F3}  p={Format(result.TTestP)}");
        Console.WriteLine($"  Wilcoxon signed-rank p={Format(result.WilcoxonP)}");
        Console.WriteLine($"  win / loss / tie     {result.Wins} / {result.Losses} / {result.Ties}");
        Console.WriteLine();

        // Stated in words as well as numbers, because the direction of a signed difference between
        // two named strategies is the single easiest thing in this report to read backwards.
        if (!result.IntervalExcludesZero)
        {
            Console.WriteLine(
                $"  The interval spans zero: this data cannot distinguish {candidateName} from "
                + $"{baselineName}.");
        }
        else if (result.MeanDifference > 0)
        {
            Console.WriteLine($"  {candidateName} is ahead of {baselineName} by "
                + $"{result.MeanDifference:F4} {metric}.");
        }
        else
        {
            Console.WriteLine($"  {candidateName} is behind {baselineName} by "
                + $"{Math.Abs(result.MeanDifference):F4} {metric}.");
        }

        Console.WriteLine(
            "  This p-value is uncorrected. It is a single planned comparison, not one row of a "
            + "family;");
        Console.WriteLine(
            "  if you scan many pairs looking for a significant one, correct it yourself.");
    }

    private static string Format(double p) =>
        p < 0.0001 ? "<0.0001" : p.ToString("F4", CultureInfo.InvariantCulture);

    private static Dictionary<string, double> Scores(IEnumerable<Row> rows, string strategy) =>
        rows.Where(r => r.Strategy.Equals(strategy, StringComparison.OrdinalIgnoreCase))
            .GroupBy(r => r.QueryId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal);

    /// <summary>
    /// Reads the three columns this command needs from an evaluate CSV.
    /// </summary>
    /// <remarks>
    /// Deliberately a small hand-rolled reader rather than a CSV dependency. The files are written
    /// by <see cref="ResultsReporter"/> in this repository, so the dialect is known: comma
    /// separated, quotes only around the query text, and no embedded newlines. Rows whose metric
    /// column is blank are dropped, which is how unjudged runs are excluded without special-casing.
    /// </remarks>
    private static async Task<List<Row>> ReadAsync(
        string path,
        string metric,
        CancellationToken cancellationToken)
    {
        string[] lines = await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false);
        var rows = new List<Row>();

        if (lines.Length < 2)
        {
            return rows;
        }

        string[] header = SplitCsv(lines[0]);
        int queryIdColumn = Array.FindIndex(header, h => h.Equals("queryId", StringComparison.OrdinalIgnoreCase));
        int modeColumn = Array.FindIndex(header, h => h.Equals("mode", StringComparison.OrdinalIgnoreCase));
        int strategyColumn = Array.FindIndex(header, h => h.Equals("strategy", StringComparison.OrdinalIgnoreCase));
        int metricColumn = Array.FindIndex(header, h => h.Equals(metric, StringComparison.OrdinalIgnoreCase));

        if (queryIdColumn < 0 || modeColumn < 0 || strategyColumn < 0)
        {
            throw new InvalidOperationException(
                $"{path} is missing one of queryId, mode or strategy. It does not look like an "
                + "evaluate results file.");
        }

        if (metricColumn < 0)
        {
            throw new InvalidOperationException(
                $"'{metric}' is not a column in {path}. Available: {string.Join(", ", header)}.");
        }

        foreach (string line in lines.Skip(1))
        {
            string[] fields = SplitCsv(line);
            if (fields.Length <= metricColumn)
            {
                continue;
            }

            if (!double.TryParse(
                fields[metricColumn], NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            {
                continue;
            }

            rows.Add(new Row(
                fields[queryIdColumn], fields[modeColumn], fields[strategyColumn], value));
        }

        return rows;
    }

    private static string[] SplitCsv(string line)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        bool quoted = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];

            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        fields.Add(current.ToString());

        return [.. fields];
    }

    private readonly record struct Row(string QueryId, string Mode, string Strategy, double Value);
}
