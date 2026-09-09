namespace CrossIndexQuery.Core.Evaluation;

/// <summary>One strategy's result for one query in one retrieval mode.</summary>
public sealed record EvaluationRecord
{
    public required string QueryId { get; init; }

    public required string QueryText { get; init; }

    public required QueryShape Shape { get; init; }

    public required QuerySpan Span { get; init; }

    public required QueryIntent Intent { get; init; }

    public required string Mode { get; init; }

    public required string Strategy { get; init; }

    /// <summary>Fidelity to the oracle ordering, position-weighted.</summary>
    public required double Ndcg { get; init; }

    public required double Recall { get; init; }

    public required double Jaccard { get; init; }

    public required double KendallTau { get; init; }

    public required double RankBiasedOverlap { get; init; }

    /// <summary>Requests issued to the service, which is what a per-operation bill counts.</summary>
    public required int QueryCount { get; init; }

    /// <summary>
    /// Compute units the service reported consuming. Measured from the response header, not
    /// estimated — the reason the sample targets a serverless service.
    /// </summary>
    public required double ComputeUnits { get; init; }

    /// <summary>
    /// Reasoning tokens billed by Azure AI Search's own agentic retrieval meter.
    /// </summary>
    /// <remarks>
    /// Kept apart from <see cref="ModelTokens"/> because the two bill on different meters at rates
    /// roughly eighteen times apart — $0.022 per million here against a small-model rate of about
    /// $0.40 per million for a deployment you own. Summing them would produce a number that is not
    /// a price of anything.
    /// </remarks>
    public int? AgenticTokens { get; init; }

    /// <summary>
    /// Model tokens billed outside the search service, when the strategy reports them.
    /// </summary>
    /// <remarks>
    /// Work sent to a model deployment the caller owns — the external reranker, or the
    /// query-planning model attached to a knowledge base. Null for every strategy that sends none,
    /// and deliberately not folded into <see cref="ComputeUnits"/>: they are separate meters with
    /// separate prices, and adding them would produce a number that is not a cost of anything. Null
    /// rather than zero for the same reason judged relevance is null when unjudged — zero is a
    /// claim that the meter ran and charged nothing.
    /// </remarks>
    public int? ModelTokens { get; init; }

    public required double LatencyMs { get; init; }

    /// <summary>How many of the returned documents came from each stripe.</summary>
    public required IReadOnlyDictionary<string, int> StripeContribution { get; init; }

    /// <summary>
    /// The document ids this strategy actually returned, in rank order.
    /// </summary>
    /// <remarks>
    /// Kept so the returned sets can be pooled across every strategy and judged on their own merits.
    /// The fidelity metrics above all measure agreement with the oracle, which cannot answer whether
    /// the oracle was right; that question needs the documents themselves, scored by something with
    /// no stake in either retrieval path.
    /// </remarks>
    public required IReadOnlyList<string> ReturnedIds { get; init; }

    /// <summary>
    /// nDCG against independent relevance judgments, when they are available.
    /// </summary>
    /// <remarks>
    /// Null when no judgments have been collected. Unlike <see cref="Ndcg"/> this does not treat the
    /// oracle as correct, so it is the only column in which the single index can be beaten.
    /// </remarks>
    public double? JudgedNdcg { get; init; }

    /// <summary>Fraction of the returned documents that carried a judgment.</summary>
    public double? JudgedCoverage { get; init; }
}

/// <summary>Aggregated results for one strategy in one retrieval mode.</summary>
/// <remarks>
/// Latency is reported as a median and a 95th percentile rather than a mean, because retrieval
/// latency distributions have long right tails and an average of a bimodal distribution describes
/// no request that actually happened.
/// </remarks>
public sealed record StrategySummary(
    string Mode,
    string Strategy,
    int Queries,
    double Ndcg,
    double Recall,
    double Jaccard,
    double KendallTau,
    double RankBiasedOverlap,
    double QueriesPerRequest,
    double ComputeUnits,
    double LatencyP50Ms,
    double LatencyP95Ms,
    double? JudgedNdcg = null,
    double? JudgedCoverage = null,
    double? ModelTokens = null,
    double? AgenticTokens = null)
{
    public static StrategySummary Aggregate(string mode, string strategy, IReadOnlyList<EvaluationRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        if (records.Count == 0)
        {
            return new StrategySummary(mode, strategy, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        }

        double[] latencies = [.. records.Select(r => r.LatencyMs).Order()];

        // Averaged only over records that actually carry a judgment, so a partially judged run
        // reports the mean of what was judged rather than diluting it with zeros.
        EvaluationRecord[] judged = [.. records.Where(r => r.JudgedNdcg is not null)];

        return new StrategySummary(
            Mode: mode,
            Strategy: strategy,
            Queries: records.Count,
            Ndcg: records.Average(r => r.Ndcg),
            Recall: records.Average(r => r.Recall),
            Jaccard: records.Average(r => r.Jaccard),
            KendallTau: records.Average(r => r.KendallTau),
            RankBiasedOverlap: records.Average(r => r.RankBiasedOverlap),
            QueriesPerRequest: records.Average(r => r.QueryCount),
            ComputeUnits: records.Average(r => r.ComputeUnits),
            LatencyP50Ms: Percentile(latencies, 0.50),
            LatencyP95Ms: Percentile(latencies, 0.95),
            JudgedNdcg: judged.Length > 0 ? judged.Average(r => r.JudgedNdcg!.Value) : null,
            JudgedCoverage: judged.Length > 0 ? judged.Average(r => r.JudgedCoverage!.Value) : null,

            // Averaged only over records that reported tokens, so a strategy that consumes
            // none stays null rather than dragging a shared average toward zero.
            ModelTokens: records.Any(r => r.ModelTokens is not null)
                ? records.Where(r => r.ModelTokens is not null).Average(r => (double)r.ModelTokens!.Value)
                : null,

            AgenticTokens: records.Any(r => r.AgenticTokens is not null)
                ? records.Where(r => r.AgenticTokens is not null).Average(r => (double)r.AgenticTokens!.Value)
                : null);
    }

    /// <summary>Nearest-rank percentile over a pre-sorted array.</summary>
    private static double Percentile(double[] sorted, double q)
    {
        if (sorted.Length == 0)
        {
            return 0d;
        }

        int index = (int)Math.Ceiling(q * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }
}
