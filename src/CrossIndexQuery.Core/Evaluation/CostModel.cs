using System.Globalization;

namespace CrossIndexQuery.Core.Evaluation;

/// <summary>
/// Converts measured meters into money, so quality, cost and latency can be read side by side.
/// </summary>
/// <remarks>
/// <para>
/// The harness measures three different meters — search compute units, semantic ranker queries and
/// model tokens — that are billed separately and at wildly different rates. Left as raw counts they
/// cannot be compared: a reader has no way to tell whether 0.0006 compute units is more or less
/// expensive than 18,500 reasoning tokens. Converting to a common unit is what makes the tradeoff
/// legible.
/// </para>
/// <para>
/// Two of the three rates are published and were read from the Azure retail prices API rather than
/// quoted from memory. The third is not published in a form this project can fetch, so it is left
/// as a parameter with a conservative default and reported separately — a reader with a negotiated
/// rate substitutes their own and every figure that depends on it moves with it.
/// </para>
/// <para>
/// Prices are regional, change over time, and exclude reserved capacity and negotiated discounts.
/// The numbers here are for <em>order-of-magnitude comparison between techniques</em>, which is
/// robust to all of that, and not for forecasting a bill, which is not.
/// </para>
/// </remarks>
public sealed record CostModel
{
    /// <summary>
    /// Serverless compute, in dollars per compute-unit hour.
    /// </summary>
    /// <remarks>
    /// Read from the Azure retail prices API on 2026-09-06 for <c>centralus</c>: meter
    /// "Serverless Compute Unit", $0.27 per hour. The service bills compute as CU/h and reports
    /// consumption per request in the <c>x-ms-azs-compute-units-consumed</c> response header, so
    /// the header value multiplied by this rate is the compute cost of one query.
    /// </remarks>
    public double DollarsPerComputeUnitHour { get; init; } = 0.27;

    /// <summary>
    /// Semantic ranker, in dollars per thousand queries.
    /// </summary>
    /// <remarks>
    /// Read from the same source: meter "Semantic Ranker queries", $1.00 per 1K. Billed per query
    /// that invokes the ranker, independent of how many documents it scores — which is why the
    /// striped arm pays twice, once per index.
    /// </remarks>
    public double DollarsPerThousandSemanticQueries { get; init; } = 1.00;

    /// <summary>
    /// Model tokens, in dollars per million.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not verifiable through the retail prices API for the models used here, so unlike the other
    /// two rates this one is an assumption rather than a measurement, and it is labelled as such
    /// wherever a figure derived from it appears.
    /// </para>
    /// <para>
    /// The default is a small-model rate. It is deliberately conservative: the agentic strategies
    /// consume tens of thousands of tokens per query, so a low rate understates rather than
    /// inflates their cost, and the conclusion drawn from it — that model tokens dominate every
    /// other meter — only gets stronger at a realistic price.
    /// </para>
    /// </remarks>
    public double DollarsPerMillionTokens { get; init; } = 0.40;

    /// <summary>The rates used by the published report.</summary>
    public static CostModel Default { get; } = new();

    /// <summary>
    /// Cost of one query, in dollars.
    /// </summary>
    /// <param name="computeUnits">Compute units the service reported for this query.</param>
    /// <param name="semanticQueries">Requests that invoked the semantic ranker.</param>
    /// <param name="modelTokens">Model tokens consumed, if any.</param>
    public double PerQuery(double computeUnits, int semanticQueries, int modelTokens) =>
        (computeUnits * DollarsPerComputeUnitHour)
        + (semanticQueries * DollarsPerThousandSemanticQueries / 1_000d)
        + (modelTokens * DollarsPerMillionTokens / 1_000_000d);

    /// <summary>
    /// Cost of a thousand queries, which is the unit the report presents.
    /// </summary>
    /// <remarks>
    /// Per-query costs land in the fourth or fifth decimal place, where differences of two orders
    /// of magnitude stop being visible. Per thousand queries the same numbers span cents to
    /// dollars, which is both readable and closer to how anyone actually budgets.
    /// </remarks>
    public double PerThousandQueries(double computeUnits, int semanticQueries, int modelTokens) =>
        PerQuery(computeUnits, semanticQueries, modelTokens) * 1_000d;

    /// <summary>Formats a cost per thousand queries at a sensible precision.</summary>
    public static string Format(double dollarsPerThousand) => dollarsPerThousand switch
    {
        < 0.01 => "<$0.01",
        < 1 => "$" + dollarsPerThousand.ToString("F3", CultureInfo.InvariantCulture),
        _ => "$" + dollarsPerThousand.ToString("F2", CultureInfo.InvariantCulture),
    };
}
