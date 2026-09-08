using CrossIndexQuery.Core.Configuration;
using CrossIndexQuery.Core.Retrieval;
using CrossIndexQuery.Core.Statistics;

namespace CrossIndexQuery.Core.Fusion;

/// <summary>
/// Decides which indexes are worth querying, then queries only those.
/// </summary>
/// <remarks>
/// <para>
/// Every other strategy in this catalog accepts that splitting a corpus means querying every part
/// of it, and competes on what to do with the results. This one attacks the premise. Its
/// observation is that a query about romance novels has nothing to find in an index of horror, and
/// the statistics needed to know that are already sitting in a file the sample builds offline.
/// </para>
/// <para>
/// This matters because of where the cost of splitting actually comes from. Measured on this
/// corpus, roughly <b>53% of a query's compute is fixed overhead</b> that does not depend on how
/// many documents the index holds — so two queries against half-size indexes cost 1.53x one query
/// against the whole corpus, not 1.0x. The corollary is the opportunity: <b>one query against a
/// half-size index costs about 0.77x</b> a query against the whole thing. Skipping an index is the
/// only move available that lands <em>below</em> the single-index cost, and no amount of
/// cleverness in the merge step can get there, because by then both queries have been paid for.
/// </para>
/// <para>
/// This is resource selection from the federated search literature, in its simplest defensible
/// form. It is a genuine gamble rather than a free lunch: skip an index that held the answer and
/// the answer is gone, with nothing downstream able to recover it. <see cref="Threshold"/> is the
/// dial governing how much of that risk you take.
/// </para>
/// </remarks>
public sealed class SelectiveSearchFusion : IFusionStrategy
{
    private readonly MultiStripeRetriever _retriever;
    private readonly CorpusStatistics _statistics;
    private readonly CrossIndexOptions _options;
    private readonly double _threshold;
    private readonly string _name;

    /// <param name="threshold">
    /// How close a stripe's selection score must be to the best stripe's before it is also queried,
    /// as a fraction. <c>1.0</c> queries only the single best stripe; <c>0.0</c> queries everything
    /// and degenerates to an ordinary fan-out.
    /// </param>
    public SelectiveSearchFusion(
        MultiStripeRetriever retriever,
        CorpusStatistics statistics,
        CrossIndexOptions options,
        double threshold,
        string name)
    {
        ArgumentNullException.ThrowIfNull(retriever);
        ArgumentNullException.ThrowIfNull(statistics);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        _retriever = retriever;
        _statistics = statistics;
        _options = options;
        _threshold = Math.Clamp(threshold, 0.0, 1.0);
        _name = name;
    }

    /// <summary>The selection aggressiveness this instance was configured with.</summary>
    public double Threshold => _threshold;

    public string Name => _name;

    public string Description => _threshold >= 1.0
        ? "Queries only the single most promising index, chosen offline from corpus statistics."
        : $"Queries every index whose expected yield is within {1 - _threshold:P0} of the best.";

    /// <summary>
    /// Keyword and hybrid only.
    /// </summary>
    /// <remarks>
    /// Selection here is driven by term statistics, and a pure vector query has no terms to consult.
    /// Selecting on vectors is a real technique — cluster the corpus and compare the query vector to
    /// centroids — but it needs an artifact this sample does not build, and guessing at it would put
    /// an unmeasured mechanism in a measured table.
    /// </remarks>
    public bool Supports(RetrievalMode mode) => mode is RetrievalMode.Keyword or RetrievalMode.Hybrid;

    /// <summary>
    /// Issues its own retrieval, so the harness charges it for what it actually asked for.
    /// </summary>
    /// <remarks>
    /// The entire claim of this strategy is that it issues fewer queries than the fan-out it was
    /// handed. Scoring it against the shared fan-out's cost would erase the only thing it does.
    /// </remarks>
    public bool PerformsOwnRetrieval => true;

    /// <summary>Indexes the most recent call decided to query.</summary>
    public IReadOnlyList<string> LastSelection { get; private set; } = [];

    public async ValueTask<IReadOnlyList<FusedDocument>> FuseAsync(
        FanOutResult fanOut,
        FusionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fanOut);
        ArgumentNullException.ThrowIfNull(context);

        IReadOnlyList<string> selected = Select(fanOut.Query);
        LastSelection = selected;

        // Budget equalized against the single index the same way every other arm is. Selecting one
        // stripe means that stripe supplies all 50 candidates; selecting two means 25 each. Without
        // this, a selective arm would be compared on a different candidate depth and the table would
        // be measuring two things at once.
        int perIndex = (int)Math.Ceiling(
            _options.Evaluation.PerStripeK / (double)Math.Max(selected.Count, 1));

        var request = new RetrievalRequest
        {
            Query = fanOut.Query,
            Mode = fanOut.Mode,
            QueryVector = context.QueryVector,
            Size = perIndex,
            ExhaustiveVectorSearch = _options.Evaluation.ExhaustiveVectorSearch,
        };

        FanOutResult narrowed = await _retriever
            .SearchAsync(selected, request, cancellationToken)
            .ConfigureAwait(false);

        // One index means one scoring scale, so there is nothing to reconcile and sorting by the
        // service's own score is exactly right. More than one and the cross-index problem is back,
        // so it is handed to the best merge this study measured rather than being solved twice.
        if (narrowed.Stripes.Count <= 1)
        {
            List<FusedDocument> single =
            [
                .. narrowed.AllDocuments
                    .OrderByDescending(d => d.Score)
                    .Take(context.TopK)
                    .Select(d => new FusedDocument(
                        d,
                        d.Score,
                        $"only index queried: {d.SourceIndex}"))
            ];

            return single;
        }

        return await new GlobalBm25Fusion(_statistics)
            .FuseAsync(narrowed, context, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Ranks the indexes by how much of the answer each is likely to hold.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The estimator is deliberately the simplest thing that uses only committed statistics and no
    /// extra request: for each query term, how many documents in that index contain it, weighted by
    /// how informative the term is corpus-wide.
    /// </para>
    /// <code>
    /// yield(index) = Σ_terms  globalIdf(term) × localDocumentFrequency(index, term)
    /// </code>
    /// <para>
    /// The two factors do different jobs. The document frequency measures how much material an
    /// index has on the topic. The global IDF weight stops a common word from dominating the
    /// decision — without it, a query for "the history of Rome" would be routed by "the".
    /// </para>
    /// <para>
    /// A term absent from an index contributes exactly zero, which is the case this technique
    /// exists for and the case it gets right most confidently.
    /// </para>
    /// <para>
    /// Better estimators exist — Taily models each index's score distribution parametrically and
    /// estimates how many documents would clear a global score threshold. This one is used because
    /// it needs nothing beyond the sidecar the sample already ships, and because a technique whose
    /// selection quality is the point should not be introduced alongside an unmeasured refinement.
    /// </para>
    /// </remarks>
    private IReadOnlyList<string> Select(string query)
    {
        IReadOnlyList<string> indexes = _options.Search.StripeIndexes;
        List<string> terms = TextTokenizer.TokenizeQuery(query);

        if (terms.Count == 0 || indexes.Count <= 1)
        {
            // No signal to select on. Querying everything is the safe failure, because it is merely
            // the cost this strategy was trying to avoid rather than a wrong answer.
            return indexes;
        }

        double[] yields = new double[indexes.Count];

        for (int i = 0; i < indexes.Count; i++)
        {
            foreach (string term in terms)
            {
                double weight = CorpusStatistics.Idf(
                    _statistics.GlobalDocumentFrequency(term), _statistics.DocumentCount);

                yields[i] += weight * _statistics.LocalDocumentFrequency(indexes[i], term);
            }
        }

        double best = yields.Max();

        if (best <= 0)
        {
            // Every term is unknown to every index. Nothing to go on, so decline to gamble.
            return indexes;
        }

        List<string> selected =
        [
            .. indexes.Where((_, i) => yields[i] >= best * _threshold)
        ];

        return selected.Count > 0 ? selected : indexes;
    }
}
