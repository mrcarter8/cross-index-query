using CrossIndexQuery.Core.Clients;
using CrossIndexQuery.Core.Configuration;
using CrossIndexQuery.Core.Indexing;
using CrossIndexQuery.Core.Retrieval;
using CrossIndexQuery.Core.Statistics;

namespace CrossIndexQuery.Core.Fusion;

/// <summary>
/// The catalog of fusion strategies, in increasing order of what they assume and what they cost.
/// </summary>
/// <remarks>
/// <para>
/// The ordering is the argument. Reading the registry top to bottom walks through the reasoning:
/// start by trusting the scores, discover they are not comparable, retreat to trusting only ranks,
/// then try to make the scores comparable by rescaling, then by recovering the statistics that were
/// lost, and finally by rescoring with something that never depended on corpus statistics at all.
/// </para>
/// <para>
/// Every one of them is registered, including the ones that are expected to do badly. A catalog
/// that only contains good ideas cannot show why they are good, and the naive strategy in
/// particular has to be present and measured because it is what the previous generation of this
/// sample did and what most people write first.
/// </para>
/// </remarks>
public sealed class FusionStrategyRegistry
{
    private readonly Dictionary<string, IFusionStrategy> _strategies = new(StringComparer.OrdinalIgnoreCase);

    public FusionStrategyRegistry(IEnumerable<IFusionStrategy> strategies)
    {
        ArgumentNullException.ThrowIfNull(strategies);

        foreach (IFusionStrategy strategy in strategies)
        {
            _strategies[strategy.Name] = strategy;
        }
    }

    public IReadOnlyCollection<IFusionStrategy> All => [.. _strategies.Values];

    public IFusionStrategy Get(string name) =>
        _strategies.TryGetValue(name, out IFusionStrategy? strategy)
            ? strategy
            : throw new KeyNotFoundException(
                $"Unknown fusion strategy '{name}'. Known strategies: {string.Join(", ", _strategies.Keys)}.");

    public bool TryGet(string name, out IFusionStrategy? strategy) =>
        _strategies.TryGetValue(name, out strategy);

    /// <summary>Strategies applicable to a given retrieval mode.</summary>
    public IReadOnlyList<IFusionStrategy> For(RetrievalMode mode) =>
        [.. _strategies.Values.Where(s => s.Supports(mode))];

    /// <summary>
    /// Builds the full catalog.
    /// </summary>
    /// <param name="statistics">
    /// The offline sidecar, when one has been built. Its absence removes only the strategies that
    /// depend on it — the probe-based correction still works, which is the point of having both.
    /// </param>
    public static FusionStrategyRegistry CreateDefault(
        SearchClientFactory factory,
        CrossIndexOptions options,
        CorpusStatistics? statistics = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(options);

        // Quota allocation needs relative index sizes. The sidecar knows them exactly; without it,
        // an equal split is the only unbiased assumption available.
        Dictionary<string, int> documentCounts = new(StringComparer.Ordinal);
        foreach (string index in options.Search.StripeIndexes)
        {
            documentCounts[index] = statistics?.LocalDocumentCount(index) ?? 1;
        }

        List<IFusionStrategy> strategies =
        [
            // Assumes scores mean the same thing everywhere. They do not; this is the control.
            new NaiveScoreFusion(),

            // Assumes nothing about scores, and nothing about relative quality either.
            new InterleaveFusion(),
            new QuotaMergeFusion(documentCounts),

            // Assumes ranks are trustworthy even when scores are not.
            new GlobalRrfFusion(),

            // Assumes scores become comparable after rescaling.
            new MinMaxNormalizationFusion(),
            new ZScoreNormalizationFusion(),

            // Assumes the corpus-independent component of the score can be isolated.
            new VectorSimilarityFusion(),
            new HybridLegFusion(),

            // Assumes the lost corpus statistics can be recovered at query time.
            new IdfCorrectionFusion(new ProbeDocumentFrequencyProvider(
                factory, BookIndexSchema.TextSearchFields)),

            // Assumes a scorer that never used corpus statistics can rank the union.
            new SemanticScoreFusion(),

            // Reranking is where candidate depth is genuinely controllable: this strategy names the
            // exact documents it wants scored, so the budget decides how many reach the
            // cross-encoder rather than merely how many come back.
            new SemanticRerankFusion(
                factory,
                options.Evaluation.CandidatesPerStripe(options.Search.StripeIndexes.Count)),
        ];

        if (statistics is not null)
        {
            strategies.Add(new IdfCorrectionFusion(new SidecarDocumentFrequencyProvider(statistics)));

            // Needs the same sidecar, but uses it to rebuild the score rather than to rescale it.
            strategies.Add(new GlobalBm25Fusion(statistics));
        }

        // Pattern 2: a model outside the search service scores every candidate. Registered last
        // because it is the most expensive thing in the catalog, and the ordering of this list is
        // meant to read as increasing cost.
        strategies.Add(new ExternalRerankFusion(
            options,
            options.Evaluation.CandidatesPerStripe(options.Search.StripeIndexes.Count)
                * Math.Max(options.Search.StripeIndexes.Count, 1)));

        // Pattern 4: the service retrieves across every stripe itself. Not a merge at all, which is
        // why it sits at the end of a list of merges.
        strategies.Add(new AgenticRetrievalFusion(options));

        return new FusionStrategyRegistry(strategies);
    }
}
