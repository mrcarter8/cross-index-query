using System.Diagnostics;
using Azure;
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
    /// Strategy name for the single index rescored with the same client-side scorer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The control that makes the study's largest claim honest. <c>global-bm25</c> beats the single
    /// index by a wide margin, but it changes two things at once: it repairs the cross-index
    /// statistics, and it replaces the service's scoring with a client-side BM25 over the text the
    /// caller already has. The second change has nothing to do with striping and is available to
    /// anyone, split corpus or not.
    /// </para>
    /// <para>
    /// This row applies exactly that client-side scorer to the single index's own results. Because
    /// one index holding the whole corpus <em>is</em> the corpus, its statistics are the global
    /// statistics, so this is <c>global-bm25</c> with the split removed and nothing else changed.
    /// Comparing the striped rescore against this row — rather than against the raw single index —
    /// is the only comparison in the study where the split is genuinely the sole difference, and it
    /// is therefore the only one that can answer "what does striping cost" without confounding.
    /// </para>
    /// </remarks>
    public const string SingleIndexRescored = "single-index-rescored";

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

        // Strategies that turned out to depend on a resource this service does not have. Recorded
        // so the failure is reported once rather than once per query, and so the run continues
        // producing the numbers it can still produce.
        HashSet<string> unavailable = new(StringComparer.Ordinal);

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
                    ExhaustiveVectorSearch = settings.ExhaustiveVectorSearch,
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

                // The single index put through the same client-side scorer as the striped arm, so
                // the two differ only in whether the corpus was split. Skipped when no statistics
                // file is loaded, which is the same condition that removes global-bm25 itself.
                EvaluationRecord? rescored = await EvaluateRescoredBaselineAsync(
                    query, mode, oracle, truth, context, cancellationToken).ConfigureAwait(false);

                if (rescored is not null)
                {
                    records.Add(rescored);

                    foreach (string id in rescored.ReturnedIds)
                    {
                        entry.Ids.Add(id);
                    }
                }

                foreach (IFusionStrategy strategy in strategies)
                {
                    if (unavailable.Contains(strategy.Name))
                    {
                        continue;
                    }

                    EvaluationRecord? record;

                    try
                    {
                        record = await EvaluateAsync(
                            query, mode, strategy, stripes, truth, context, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (RequestFailedException ex) when (ex.Status is 404 or 403)
                    {
                        // A strategy that depends on a resource nobody provisioned — a knowledge
                        // base, a deployment — must not take a hundred-query run down with it. The
                        // rest of the catalog is unaffected and its numbers are still worth having,
                        // so the strategy is dropped for this run with one message rather than
                        // repeating the failure on every remaining query.
                        unavailable.Add(strategy.Name);

                        Console.Error.WriteLine(
                            $"Skipping '{strategy.Name}': {ex.Status} from the service. "
                            + "Every other strategy continues. "
                            + $"({ex.Message.Split('\n')[0].Trim()})");

                        continue;
                    }

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
            RankBiasedOverlap = RankingMetrics.RankBiasedOverlap(candidate, truth, context.TopK),

            // The retrieval is shared by every strategy, so the extra requests a strategy makes on
            // its own account are what actually distinguish them on cost — except for one that did
            // its own retrieval and never touched the shared fan-out, which is charged for itself
            // alone.
            //
            // A strategy whose work happens server-side reports its own counts, because the
            // client-side scope cannot observe requests it never issued. Falling back to the scope
            // there would print a zero, and a zero in a cost column reads as free rather than as
            // unmeasured.
            QueryCount = strategy switch
            {
                AgenticRetrievalFusion agentic => agentic.LastSearchCount,
                { PerformsOwnRetrieval: true } => scope.RequestCount,
                _ => stripes.QueryCount + scope.RequestCount,
            },
            ComputeUnits = strategy.PerformsOwnRetrieval
                ? scope.TotalComputeUnits ?? 0d
                : stripes.ComputeUnits + (scope.TotalComputeUnits ?? 0d),
            ModelTokens = strategy is AgenticRetrievalFusion tokenSource
                ? tokenSource.LastReasoningTokens
                : null,
            LatencyMs = strategy.PerformsOwnRetrieval
                ? fusionTime.TotalMilliseconds
                : stripes.Elapsed.TotalMilliseconds + fusionTime.TotalMilliseconds,
            StripeContribution = contribution,
            ReturnedIds = candidate,
        };
    }

    /// <summary>
    /// Applies the striped arm's client-side scorer to the single index's own results.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reuses the registered <c>global-bm25</c> instance rather than constructing a second one, so
    /// the two arms cannot drift apart through a constant being changed in one place. If that
    /// strategy is absent — which happens when no statistics file is loaded — the row is simply not
    /// produced, matching the striped side's behaviour.
    /// </para>
    /// <para>
    /// The fidelity columns are left at their definitional values. This row returns a different
    /// ordering from the raw single index, so its fidelity <em>against</em> the raw single index is
    /// genuinely below 1.0, but reporting that would invite it to be read as striping damage when no
    /// striping is involved. Its only meaningful column is judged relevance, which is the column
    /// the control exists to supply.
    /// </para>
    /// </remarks>
    private async Task<EvaluationRecord?> EvaluateRescoredBaselineAsync(
        EvaluationQuery query,
        RetrievalMode mode,
        FanOutResult oracle,
        IReadOnlyList<string> truth,
        FusionContext context,
        CancellationToken cancellationToken)
    {
        IFusionStrategy? rescorer = registry.For(mode)
            .FirstOrDefault(s => s.Name == GlobalBm25Fusion.StrategyName);

        if (rescorer is null)
        {
            return null;
        }

        IReadOnlyList<FusedDocument> fused;
        try
        {
            fused = await rescorer.FuseAsync(oracle, context, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        IReadOnlyList<string> candidate = [.. fused.Select(d => d.Id)];

        return new EvaluationRecord
        {
            QueryId = query.Id,
            QueryText = query.Text,
            Shape = query.Shape,
            Span = query.Span,
            Intent = query.Intent,
            Mode = mode.ToString(),
            Strategy = SingleIndexRescored,
            Ndcg = RankingMetrics.NormalizedDiscountedCumulativeGain(candidate, truth, context.TopK),
            Recall = RankingMetrics.RecallAtK(candidate, truth, context.TopK),
            Jaccard = RankingMetrics.JaccardAtK(candidate, truth, context.TopK),
            KendallTau = RankingMetrics.KendallTau(candidate, truth),
            RankBiasedOverlap = RankingMetrics.RankBiasedOverlap(candidate, truth, context.TopK),

            // One index, one query, and arithmetic on results already paid for.
            QueryCount = oracle.QueryCount,
            ComputeUnits = oracle.ComputeUnits,
            LatencyMs = oracle.Elapsed.TotalMilliseconds,
            StripeContribution = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                [options.Search.OracleIndex] = candidate.Count,
            },
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
                ExhaustiveVectorSearch = options.Evaluation.ExhaustiveVectorSearch,
            };

            await retriever.SearchOracleAsync(request, cancellationToken).ConfigureAwait(false);
            await retriever.SearchStripesAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>Progress report for a long evaluation run.</summary>
public sealed record EvaluationProgress(RetrievalMode Mode, int Completed, int Total);
