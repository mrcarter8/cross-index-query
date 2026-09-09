using System.Globalization;

namespace CrossIndexQuery.Core.Evaluation;

/// <summary>
/// Which Azure AI Search pricing model the numbers are being read under.
/// </summary>
/// <remarks>
/// The distinction matters more than it first appears, because the same measurement means two
/// different things depending on the answer, and reporting one number for both is how a benchmark
/// tells half its readers something false.
/// </remarks>
public enum PricingModel
{
    /// <summary>
    /// Serverless. Compute is metered per query and appears on the bill.
    /// </summary>
    Serverless,

    /// <summary>
    /// A provisioned tier — Basic, S1, S2, S3, L1, L2.
    /// </summary>
    /// <remarks>
    /// You buy search units by the hour and they cost the same whether idle or saturated. The
    /// marginal dollar cost of one more query is therefore <em>zero</em> until the service runs out
    /// of headroom, at which point it is the discrete price of another replica. Extra queries are
    /// consumed out of capacity, not out of budget.
    /// </remarks>
    Dedicated,
}

/// <summary>
/// Separates what a query <em>costs</em> from what it <em>consumes</em>.
/// </summary>
/// <remarks>
/// <para>
/// An earlier version of this model expressed everything as dollars by converting compute units at
/// the serverless rate. That is right for serverless and wrong for every provisioned tier, where
/// compute is a fixed hourly charge and an additional query adds nothing to the invoice. Presenting
/// one "cost" column meant telling a dedicated-tier reader that fanning out to two indexes costs
/// 1.4x more money, when for them it costs no money at all and instead consumes 1.4x of capacity
/// they have already bought.
/// </para>
/// <para>
/// So the model reports two quantities, measured separately and to be read separately:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <b>Metered cost</b> — consumption meters that bill on every tier: the semantic ranker, the
///     agentic retrieval reasoning meter, and any model tokens spent outside the search service.
///     This is money on any pricing model, and it is the honest answer to "what does this cost".
///   </description></item>
///   <item><description>
///     <b>Capacity</b> — compute units, relative to the single-index baseline. On serverless this
///     converts directly to money at the published CU rate. On a dedicated tier it converts to
///     throughput: consuming 1.4x the compute per query means roughly 29% less peak QPS from the
///     same hardware, which surfaces as a scaling decision rather than a line item.
///   </description></item>
/// </list>
/// <para>
/// Both come from the same measurements. Only the translation into consequences differs, which is
/// why the harness records compute units and meter counts rather than dollars.
/// </para>
/// </remarks>
public sealed record CostModel
{
    /// <summary>
    /// Serverless compute, in dollars per compute-unit hour.
    /// </summary>
    /// <remarks>
    /// Meter "Serverless Compute Unit", $0.27 per hour in <c>centralus</c>, read from the Azure
    /// retail prices API on 2026-09-09. Applies only under <see cref="PricingModel.Serverless"/>.
    /// </remarks>
    public double DollarsPerComputeUnitHour { get; init; } = 0.27;

    /// <summary>
    /// Semantic ranker, in dollars per thousand queries.
    /// </summary>
    /// <remarks>
    /// Meter "Semantic Ranker queries", $1.00 per 1K, billing type <c>Consumption</c> with no tier
    /// qualifier — so it bills identically on serverless and on every provisioned tier. Charged per
    /// request that invokes the ranker regardless of how many documents it scores, which is exactly
    /// why a two-index fan-out pays it twice for one user query. This is the clearest case in the
    /// study of a real dollar cost of splitting that no client-side cleverness avoids.
    /// </remarks>
    public double DollarsPerThousandSemanticQueries { get; init; } = 1.00;

    /// <summary>
    /// Agentic retrieval reasoning tokens, in dollars per million.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Meters "Agentic Retrieval Minimum/Low Reasoning Tokens", $0.000022 per 1K — that is
    /// <b>$0.022 per million</b>. Billed by Azure AI Search itself rather than by a model provider,
    /// billing type <c>Consumption</c>, so it applies on every tier.
    /// </para>
    /// <para>
    /// An earlier version of this study priced these at $0.40 per million by analogy with a small
    /// chat model, overstating them roughly eighteenfold and making agentic retrieval look far more
    /// expensive than it is. The lesson is one this report keeps relearning: an assumed rate is not
    /// a measurement, and the gap between them can invert a recommendation.
    /// </para>
    /// <para>
    /// The medium-effort meter is priced separately at $0.10 per million, roughly 4.5x the minimum
    /// and low rate. This study measures only minimum and low.
    /// </para>
    /// </remarks>
    public double DollarsPerMillionAgenticTokens { get; init; } = 0.022;

    /// <summary>
    /// Model tokens billed outside the search service, in dollars per million.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Applies to work sent to a model deployment the caller owns: the external reranking strategy,
    /// and the query-planning model attached to a knowledge base. Those are Foundry charges, on a
    /// separate bill from search.
    /// </para>
    /// <para>
    /// Unlike every other rate here, this one is <b>an assumption, not a measurement</b> — the
    /// retail prices API does not expose per-token rates for these deployments in a form this
    /// project can fetch. It is labelled as an assumption wherever a derived figure appears. The
    /// default is a small-model rate; substitute your own and every dependent figure moves with it.
    /// </para>
    /// </remarks>
    public double DollarsPerMillionModelTokens { get; init; } = 0.40;

    /// <summary>The rates used by the published report.</summary>
    public static CostModel Default { get; } = new();

    /// <summary>
    /// What a thousand queries cost in money, on any pricing model.
    /// </summary>
    /// <remarks>
    /// Deliberately excludes compute. On a dedicated tier compute is already paid for and adds
    /// nothing per query; on serverless it is added separately by
    /// <see cref="ComputeDollarsPerThousandQueries"/>. Keeping them apart is what lets one set of
    /// measurements serve both audiences honestly.
    /// </remarks>
    public double MeteredDollarsPerThousandQueries(
        int semanticQueries,
        int agenticTokens,
        int modelTokens)
    {
        double semantic = semanticQueries * DollarsPerThousandSemanticQueries;
        double agentic = agenticTokens * 1_000d / 1_000_000d * DollarsPerMillionAgenticTokens;
        double model = modelTokens * 1_000d / 1_000_000d * DollarsPerMillionModelTokens;

        return semantic + agentic + model;
    }

    /// <summary>
    /// What a thousand queries add in compute charges under serverless.
    /// </summary>
    /// <remarks>
    /// Zero under <see cref="PricingModel.Dedicated"/>, which is the entire point of the
    /// distinction: the compute was bought by the hour and the query does not add to the bill.
    /// </remarks>
    public double ComputeDollarsPerThousandQueries(double computeUnits, PricingModel pricing) =>
        pricing == PricingModel.Dedicated
            ? 0d
            : computeUnits * DollarsPerComputeUnitHour * 1_000d;

    /// <summary>
    /// Total dollars per thousand queries under a given pricing model.
    /// </summary>
    public double TotalDollarsPerThousandQueries(
        double computeUnits,
        int semanticQueries,
        int agenticTokens,
        int modelTokens,
        PricingModel pricing) =>
        MeteredDollarsPerThousandQueries(semanticQueries, agenticTokens, modelTokens)
        + ComputeDollarsPerThousandQueries(computeUnits, pricing);

    /// <summary>
    /// Peak throughput relative to the baseline, given a compute multiple.
    /// </summary>
    /// <remarks>
    /// The dedicated-tier reading of a compute measurement. A query consuming 1.4x the compute of
    /// the baseline leaves room for 1/1.4 as many queries per second on identical hardware, so this
    /// returns the fraction of peak QPS retained — 0.71 for a 1.4x consumer. It is an upper bound:
    /// it assumes compute is the binding constraint rather than connections, memory or replica
    /// count, which is true often enough to plan with and not always.
    /// </remarks>
    public static double RelativeThroughput(double computeMultiple) =>
        computeMultiple <= 0 ? 0 : 1d / computeMultiple;

    /// <summary>Formats a cost per thousand queries at a sensible precision.</summary>
    public static string Format(double dollarsPerThousand) => dollarsPerThousand switch
    {
        <= 0 => "—",
        < 0.01 => "<$0.01",
        < 1 => "$" + dollarsPerThousand.ToString("F3", CultureInfo.InvariantCulture),
        _ => "$" + dollarsPerThousand.ToString("F2", CultureInfo.InvariantCulture),
    };
}
