using System.Diagnostics;
using CrossIndexQuery.Core.Clients;
using CrossIndexQuery.Core.Configuration;
using CrossIndexQuery.Core.Fusion;
using CrossIndexQuery.Core.Retrieval;
using CrossIndexQuery.Core.Telemetry;

namespace CrossIndexQuery.Core.Evaluation;

/// <summary>
/// Runs every fusion strategy over every query in every retrieval mode and scores each result
/// against the same query answered by a single index holding the whole corpus.
/// </summary>
/// <remarks>
/// <para>
/// This is what makes the sample an argument rather than an assertion. The claim that a corpus
/// split across two indexes loses relevance, and that particular techniques recover particular
/// amounts of it, is only worth making if it is measured — and measured on the reader's own data,
/// since how much is lost depends entirely on how the split correlates with what people search for.
/// </para>
/// <para>
/// Two disciplines make the numbers trustworthy. Compute is measured from the service's own
/// response header rather than estimated, so cost comparisons between a cheap merge and an
/// expensive rerank are factual. And every mode is warmed up before measurement, because a
/// serverless service scales its compute to zero when idle and the first query after that pays a
/// cold start that has nothing to do with the strategy being measured — without a warmup, whichever
/// strategy happened to run first would always look worst.
/// </para>
/// </remarks>
public sealed class EvaluationHarness(
    MultiStripeRetriever retriever,
    FusionStrategyRegistry registry,
    IQueryEmbedder embedder,
    CrossIndexOptions options)
{
    /// <summary>
    /// Strategy name used for the single-index baseline row.
    /// </summary>
    /// <remarks>
    /// The oracle is not a fusion strategy — it is what you have before you are forced to stripe.
    /// Naming it explicitly keeps it distinguishable in every table, because its fidelity columns
    /// are self-comparisons and mean nothing, while its cost, latency and judged relevance are real
    /// and are exactly what the striped approaches have to be weighed against.
    /// </remarks>
    public const string SingleIndexBaseline = "single-index";

    /// <summary>
    /// One query and every document any approach returned for it.
    /// </summary>
    /// <remarks>
    /// The pool is the union of the top-k from every strategy in every mode, plus the oracle's own
    /// top-k. Judging the union once, blind to which approach produced what, is what lets each
    /// approach be scored against absolute relevance instead of against the oracle's opinion — and
    /// it is the only way to ask whether the oracle was the best answer rather than assuming it.
    /// </remarks>
    public sealed record PooledCandidate(
        string QueryId,
        string QueryText,
        IReadOnlyList<string> DocumentIds);

    /// <summary>Everything one evaluation run produced.</summary>
    public sealed record EvaluationRun(
        IReadOnlyList<EvaluationRecord> Records,
        IReadOnlyList<PooledCandidate> Pool);

    public async Task<EvaluationRun> RunAsync(
        IReadOnlyList<EvaluationQuery> queries,
        IReadOnlyList<RetrievalMode> modes,
        bool useSemanticRanker = false,
        IProgress<EvaluationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(queries);
        ArgumentNullException.ThrowIfNull(modes);

        EvaluationOptions settings = options.Evaluation;
        List<EvaluationRecord> records = [];

        // Accumulated across every mode, because the judge scores a (query, document) pair once
        // regardless of which mode surfaced it. Insertion-ordered so the pool file is stable.
        Dictionary<string, (EvaluationQuery Query, HashSet<string> Ids)> pool = new(StringComparer.Ordinal);

        foreach (RetrievalMode mode in modes)
        {
            await WarmUpAsync(queries, mode, useSemanticRanker, cancellationToken).ConfigureAwait(false);

            // A strategy that reranks is measured against a reranked oracle or not at all. Scoring
            // one against a BM25 baseline reports the gap between two scoring functions as though
            // it were the cost of striping, which is the single most misleading number this harness
            // could produce.
            IReadOnlyList<IFusionStrategy> strategies =
            [
                .. registry.For(mode)
                    .Where(s => useSemanticRanker || !s.RequiresSemanticRanker)
            ];

            int completed = 0;

            foreach (EvaluationQuery query in queries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                ReadOnlyMemory<float>? vector = mode is RetrievalMode.Vector or RetrievalMode.Hybrid
                    ? await embedder.EmbedAsync(query.Text, cancellationToken).ConfigureAwait(false)
                    : null;

                // The two arms are deliberately given different per-index sizes, because what has to
                // match is the total number of documents each arm puts in front of the ranker, not
                // the parameter passed to each index. Sending 50 to one oracle index and 50 to each
                // of two stripes gives the striped arm twice the candidate depth, and every
                // subsequent number would then be measuring the split and the depth together.
                int perStripe = settings.CandidatesPerStripe(options.Search.StripeIndexes.Count);

                var oracleRequest = new RetrievalRequest
                {
                    Query = query.Text,
                    Mode = mode,
                    QueryVector = vector,
                    Size = settings.PerStripeK,
                    UseSemanticRanker = useSemanticRanker,
                };

                var request = oracleRequest with { Size = perStripe };

                FanOutResult oracle = await retriever
                    .SearchOracleAsync(oracleRequest, cancellationToken).ConfigureAwait(false);

                FanOutResult stripes = await retriever
                    .SearchStripesAsync(request, cancellationToken).ConfigureAwait(false);

                IReadOnlyList<string> truth =
                [
                    .. oracle.AllDocuments
                        .OrderByDescending(d => d.RerankerScore ?? d.Score)
                        .Select(d => d.Id)
                        .Take(settings.PerStripeK)
                ];

                var context = new FusionContext(settings.TopK, vector);

                if (!pool.TryGetValue(query.Id, out (EvaluationQuery Query, HashSet<string> Ids) entry))
                {
                    entry = (query, new HashSet<string>(StringComparer.Ordinal));
                    pool[query.Id] = entry;
                }

                // The oracle is recorded as an approach in its own right. Against its own ordering
                // its fidelity is trivially perfect, which is why those columns are 1.0 and why they
                // say nothing; the row exists so the single index carries a real cost and latency
                // figure, and so that once independent judgments exist it can be scored — and
                // beaten — like everything else.
                IReadOnlyList<string> oracleTop = [.. truth.Take(settings.TopK)];

                records.Add(new EvaluationRecord
                {
                    QueryId = query.Id,
                    QueryText = query.Text,
                    Shape = query.Shape,
                    Span = query.Span,
                    Intent = query.Intent,
                    Mode = mode.ToString(),
                    Strategy = SingleIndexBaseline,
                    Ndcg = 1d,
                    Recall = 1d,
                    Jaccard = 1d,
                    KendallTau = 1d,
                    RankBiasedOverlap = 1d,
                    QueryCount = oracle.QueryCount,
                    ComputeUnits = oracle.ComputeUnits,
                    LatencyMs = oracle.Elapsed.TotalMilliseconds,
                    StripeContribution = new Dictionary<string, int>(StringComparer.Ordinal)
                    {
                        [options.Search.OracleIndex] = oracleTop.Count,
                    },
                    ReturnedIds = oracleTop,
                });

                foreach (string id in oracleTop)
                {
                    entry.Ids.Add(id);
                }

                foreach (IFusionStrategy strategy in strategies)
                {
                    EvaluationRecord? record = await EvaluateAsync(
                        query, mode, strategy, stripes, truth, context, cancellationToken)
                        .ConfigureAwait(false);

                    if (record is not null)
                    {
                        records.Add(record);

                        foreach (string id in record.ReturnedIds)
                        {
                            entry.Ids.Add(id);
                        }
                    }
                }

                progress?.Report(new EvaluationProgress(mode, ++completed, queries.Count));
            }
        }

        return new EvaluationRun(
            records,
            [
                .. pool.Values.Select(e => new PooledCandidate(
                    e.Query.Id,
                    e.Query.Text,
                    [.. e.Ids.Order(StringComparer.Ordinal)]))
            ]);
    }

    private async Task<EvaluationRecord?> EvaluateAsync(
        EvaluationQuery query,
        RetrievalMode mode,
        IFusionStrategy strategy,
        FanOutResult stripes,
        IReadOnlyList<string> truth,
        FusionContext context,
        CancellationToken cancellationToken)
    {
        using var scope = ComputeUnitScope.Begin($"fuse:{strategy.Name}");
        long start = Stopwatch.GetTimestamp();

        IReadOnlyList<FusedDocument> fused;
        try
        {
            fused = await strategy.FuseAsync(stripes, context, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // A strategy that declares its precondition unmet is reporting honestly, not failing.
            // Recording a zero would misrepresent it as having produced a bad result.
            return null;
        }

        TimeSpan fusionTime = Stopwatch.GetElapsedTime(start);
        IReadOnlyList<string> candidate = [.. fused.Select(d => d.Id)];

        Dictionary<string, int> contribution = new(StringComparer.Ordinal);
        foreach (FusedDocument doc in fused)
        {
            contribution[doc.SourceIndex] = contribution.GetValueOrDefault(doc.SourceIndex) + 1;
        }

        return new EvaluationRecord
        {
            QueryId = query.Id,
            QueryText = query.Text,
            Shape = query.Shape,
            Span = query.Span,
            Intent = query.Intent,
            Mode = mode.ToString(),
            Strategy = strategy.Name,
            Ndcg = RankingMetrics.NormalizedDiscountedCumulativeGain(candidate, truth, context.TopK),
            Recall = RankingMetrics.RecallAtK(candidate, truth, context.TopK),
            Jaccard = RankingMetrics.JaccardAtK(candidate, truth, context.TopK),
            KendallTau = RankingMetrics.KendallTau(candidate, truth),
            RankBiasedOverlap = RankingMetrics.RankBiasedOverlap(candidate, truth),

            // The retrieval is shared by every strategy, so the extra requests a strategy makes on
            // its own account are what actually distinguish them on cost — except for one that did
            // its own retrieval and never touched the shared fan-out, which is charged for itself
            // alone.
            QueryCount = strategy.PerformsOwnRetrieval
                ? scope.RequestCount
                : stripes.QueryCount + scope.RequestCount,
            ComputeUnits = strategy.PerformsOwnRetrieval
                ? scope.TotalComputeUnits ?? 0d
                : stripes.ComputeUnits + (scope.TotalComputeUnits ?? 0d),
            LatencyMs = strategy.PerformsOwnRetrieval
                ? fusionTime.TotalMilliseconds
                : stripes.Elapsed.TotalMilliseconds + fusionTime.TotalMilliseconds,
            StripeContribution = contribution,
            ReturnedIds = candidate,
        };
    }

    /// <summary>
    /// Issues and discards queries until the service is serving warm.
    /// </summary>
    /// <remarks>
    /// Mandatory on serverless, where compute scales to zero after roughly ten minutes idle. The
    /// discarded queries are drawn from the real query set so the warmup exercises the same code
    /// path, index and vector dimensionality the measured run will use.
    /// </remarks>
    private async Task WarmUpAsync(
        IReadOnlyList<EvaluationQuery> queries,
        RetrievalMode mode,
        bool useSemanticRanker,
        CancellationToken cancellationToken)
    {
        int count = Math.Min(options.Evaluation.WarmupQueries, queries.Count);

        for (int i = 0; i < count; i++)
        {
            EvaluationQuery query = queries[i % queries.Count];

            ReadOnlyMemory<float>? vector = mode is RetrievalMode.Vector or RetrievalMode.Hybrid
                ? await embedder.EmbedAsync(query.Text, cancellationToken).ConfigureAwait(false)
                : null;

            var request = new RetrievalRequest
            {
                Query = query.Text,
                Mode = mode,
                QueryVector = vector,
                Size = options.Evaluation.PerStripeK,
                UseSemanticRanker = useSemanticRanker,
            };

            await retriever.SearchOracleAsync(request, cancellationToken).ConfigureAwait(false);
            await retriever.SearchStripesAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>Progress report for a long evaluation run.</summary>
public sealed record EvaluationProgress(RetrievalMode Mode, int Completed, int Total);
