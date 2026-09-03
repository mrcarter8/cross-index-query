using System.Text.Json;
using CrossIndexQuery.Core;
using CrossIndexQuery.Core.Clients;
using CrossIndexQuery.Core.Configuration;
using CrossIndexQuery.Core.Evaluation;
using CrossIndexQuery.Core.Fusion;
using CrossIndexQuery.Core.Retrieval;
using CrossIndexQuery.Core.Statistics;

namespace CrossIndexQuery.Cli.Commands;

/// <summary>
/// Runs the full strategy matrix over the committed query set and writes the results.
/// </summary>
public sealed class EvaluateCommand(CrossIndexOptions options)
{
    public async Task<int> RunAsync(
        IReadOnlyList<RetrievalMode> modes,
        bool semantic,
        int? queryLimit,
        CancellationToken cancellationToken = default)
    {
        string dataDirectory = RepositoryLocator.ResolveDataDirectory(options.Corpus.DataDirectory);
        string querySetPath = Path.Combine(dataDirectory, "queries.json");

        IReadOnlyList<EvaluationQuery> queries = await QuerySetLoader
            .LoadAsync(querySetPath, cancellationToken).ConfigureAwait(false);

        if (queryLimit is { } limit && limit > 0 && limit < queries.Count)
        {
            // Sampling with an even stride rather than taking the first N, so a smoke run still
            // covers both stripe-local and cross-stripe queries instead of whatever happens to sit
            // at the top of the file.
            double stride = queries.Count / (double)limit;
            queries = [.. Enumerable.Range(0, limit).Select(i => queries[(int)(i * stride)])];
        }

        int stripeCount = options.Search.StripeIndexes.Count;
        int perStripe = options.Evaluation.CandidatesPerStripe(stripeCount);

        Console.WriteLine($"{queries.Count} queries x {modes.Count} modes.");
        Console.WriteLine($"Semantic ranker: {(semantic ? "on" : "off")}.");
        Console.WriteLine(
            $"Candidate budget: {options.Evaluation.CandidateBudget} — "
            + $"oracle 1x{options.Evaluation.PerStripeK}, "
            + $"stripes {stripeCount}x{perStripe} = {stripeCount * perStripe}.");
        Console.WriteLine();

        var factory = new SearchClientFactory(options.Search);
        var retriever = new MultiStripeRetriever(new StripeRetriever(factory), options);
        var embedder = new AzureOpenAIQueryEmbedder(options);

        CorpusStatistics.TryLoad(dataDirectory, options.Corpus.SplitDescriptor, out CorpusStatistics? statistics);
        if (statistics is null)
        {
            Console.WriteLine(
                "No corpus-statistics.json found; the sidecar IDF strategy will be skipped. "
                + "The probe-based one still runs.");
            Console.WriteLine();
        }

        FusionStrategyRegistry registry = FusionStrategyRegistry.CreateDefault(factory, options, statistics);
        var harness = new EvaluationHarness(retriever, registry, embedder, options);

        var progress = new Progress<EvaluationProgress>(p =>
        {
            if (p.Completed % 10 == 0 || p.Completed == p.Total)
            {
                Console.WriteLine($"  {p.Mode}: {p.Completed}/{p.Total}");
            }
        });

        EvaluationHarness.EvaluationRun run = await harness
            .RunAsync(queries, modes, semantic, progress, cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<EvaluationRecord> records = run.Records;

        // Judgments are optional and arrive out of band, so they are applied after the run rather
        // than gathered during it. A run made before any judging still produces every fidelity
        // number; collecting judgments later adds the absolute column without re-querying.
        records = ApplyJudgments(records, dataDirectory, options.Evaluation);

        string outputDirectory = Path.IsPathRooted(options.Evaluation.OutputDirectory)
            ? options.Evaluation.OutputDirectory
            : Path.Combine(
                RepositoryLocator.ResolveRepositoryRoot(options.Corpus.DataDirectory),
                options.Evaluation.OutputDirectory);

        // The tier belongs in the filename as much as the split does. A semantic run and a lexical
        // run over the same split are different experiments with different ground truth, and
        // letting one overwrite the other silently produces a file whose contents contradict its
        // name.
        string tier = semantic ? "semantic" : "lexical";

        // A re-scoring against a different judge must not overwrite the primary results, or the
        // comparison between them becomes impossible to make.
        string judgeSuffix = string.Equals(
            options.Evaluation.JudgmentsFile, "judgments.json", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : ".alt-judge";

        string csvPath = Path.Combine(
            outputDirectory, $"results.{options.Corpus.SplitDescriptor}.{tier}{judgeSuffix}.csv");
        string markdownPath = Path.Combine(
            outputDirectory, $"results.{options.Corpus.SplitDescriptor}.{tier}{judgeSuffix}.md");

        // The service name is deliberately not written into published results. It identifies a
        // specific deployment rather than anything a reader needs, and results files are exactly the
        // artefact most likely to be shared outside the environment that produced them.
        string serviceDescription =
            $"a {(semantic ? "semantic" : "lexical")} run ({queries.Count} queries, "
            + $"top-{options.Evaluation.TopK}, {options.Evaluation.CandidateBudget} candidate budget: "
            + $"oracle 1x{options.Evaluation.PerStripeK} vs stripes {stripeCount}x{perStripe})";

        await ResultsReporter.WriteCsvAsync(csvPath, records, cancellationToken).ConfigureAwait(false);
        await ResultsReporter.WriteMarkdownAsync(
            markdownPath,
            records,
            serviceDescription,
            cancellationToken).ConfigureAwait(false);

        Console.WriteLine();
        WriteConsoleSummary(records);

        Console.WriteLine();
        Console.WriteLine($"Wrote {csvPath}");
        Console.WriteLine($"Wrote {markdownPath}");

        // Named per run configuration so a semantic run does not overwrite a lexical one, and one
        // split does not overwrite another. The judge unions every pool file it finds, which is also
        // how someone who adds a fusion strategy extends the judged set rather than replacing it.
        string poolPath = Path.Combine(
            outputDirectory,
            $"judgment-pool.{options.Corpus.SplitDescriptor}.{tier}.json");

        await WritePoolAsync(poolPath, run.Pool, cancellationToken).ConfigureAwait(false);

        int pairs = run.Pool.Sum(p => p.DocumentIds.Count);
        Console.WriteLine(
            $"Wrote {poolPath} — {run.Pool.Count} queries, {pairs} unique (query, document) pairs, "
            + $"{pairs / (double)Math.Max(run.Pool.Count, 1):F1} per query.");

        return 0;
    }

    /// <summary>
    /// Writes the pooled candidate set for independent relevance judging.
    /// </summary>
    /// <remarks>
    /// Deliberately carries no strategy attribution. A judge that can see which system produced a
    /// document can favour one, and the entire purpose of pooling is to obtain judgments that no
    /// approach in the comparison had a hand in.
    /// </remarks>
    private static async Task WritePoolAsync(
        string path,
        IReadOnlyList<EvaluationHarness.PooledCandidate> pool,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        await using FileStream stream = File.Create(path);
        await JsonSerializer.SerializeAsync(
            stream, pool, new JsonSerializerOptions { WriteIndented = true }, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Attaches independent relevance judgments to each record, when they have been collected.
    /// </summary>
    /// <remarks>
    /// Absent judgments leave the columns null rather than zero. A zero would read as "returned
    /// nothing relevant", which is a claim about the retrieval; null reads as "not judged", which is
    /// a claim about the evidence. The distinction matters because most of this sample's history is
    /// runs made before any judge existed.
    /// </remarks>
    private static IReadOnlyList<EvaluationRecord> ApplyJudgments(
        IReadOnlyList<EvaluationRecord> records,
        string dataDirectory,
        EvaluationOptions settings)
    {
        int topK = settings.TopK;
        string path = Path.Combine(dataDirectory, settings.JudgmentsFile);
        if (!File.Exists(path))
        {
            Console.WriteLine(
                $"No {settings.JudgmentsFile} found in data; reporting fidelity to the oracle only. "
                + "Run 'dataprep judge submit' then 'judge collect' to add absolute relevance.");
            Console.WriteLine();
            return records;
        }

        Dictionary<string, Dictionary<string, int>>? judgments;
        using (FileStream stream = File.OpenRead(path))
        {
            judgments = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, int>>>(stream);
        }

        if (judgments is null || judgments.Count == 0)
        {
            return records;
        }

        var empty = new Dictionary<string, int>(StringComparer.Ordinal);

        return
        [
            .. records.Select(r =>
            {
                Dictionary<string, int> grades =
                    judgments.TryGetValue(r.QueryId, out Dictionary<string, int>? g) ? g : empty;

                return r with
                {
                    JudgedNdcg = RankingMetrics.JudgedNdcg(r.ReturnedIds, grades, topK),
                    JudgedCoverage = RankingMetrics.JudgedCoverage(r.ReturnedIds, grades, topK),
                };
            })
        ];
    }

    private static void WriteConsoleSummary(IReadOnlyList<EvaluationRecord> records)
    {
        bool judged = records.Any(r => r.JudgedNdcg is not null);

        foreach (IGrouping<string, EvaluationRecord> byMode in records.GroupBy(r => r.Mode))
        {
            Console.WriteLine($"=== {byMode.Key} ===");

            if (judged)
            {
                // Sorted by judged relevance, because once an independent judge exists that is the
                // question people actually have. Fidelity stays visible next to it: a strategy can
                // reproduce the single index faithfully and still be returning mediocre documents,
                // and the two columns disagreeing is the interesting case.
                Console.WriteLine(
                    $"{"strategy",-26}{"judged",8}{"cover",8}{"fidelity",10}{"recall",8}{"CU",10}{"p50ms",8}");

                foreach (StrategySummary s in Summaries(byMode).OrderByDescending(s => s.JudgedNdcg ?? -1))
                {
                    Console.WriteLine(
                        $"{s.Strategy,-26}{s.JudgedNdcg ?? 0,8:F3}{s.JudgedCoverage ?? 0,8:P0}"
                        + $"{s.Ndcg,10:F3}{s.Recall,8:F3}{s.ComputeUnits,10:F4}{s.LatencyP50Ms,8:F0}");
                }
            }
            else
            {
                Console.WriteLine(
                    $"{"strategy",-26}{"nDCG",8}{"recall",8}{"RBO",8}{"cross",8}{"CU",10}{"p50ms",8}");

                foreach (StrategySummary s in Summaries(byMode).OrderByDescending(s => s.Ndcg))
                {
                    double cross = CrossStripeNdcg(byMode, s.Strategy);

                    Console.WriteLine(
                        $"{s.Strategy,-26}{s.Ndcg,8:F3}{s.Recall,8:F3}{s.RankBiasedOverlap,8:F3}"
                        + $"{cross,8:F3}{s.ComputeUnits,10:F4}{s.LatencyP50Ms,8:F0}");
                }
            }

            Console.WriteLine();
        }

        if (judged)
        {
            Console.WriteLine(
                $"'{EvaluationHarness.SingleIndexBaseline}' is the un-striped baseline. Its fidelity "
                + "column is a self-comparison and is always 1.000; only its judged, cost and latency "
                + "figures are meaningful.");
        }
    }

    private static IEnumerable<StrategySummary> Summaries(IEnumerable<EvaluationRecord> records) =>
        records
            .GroupBy(r => r.Strategy)
            .Select(g => StrategySummary.Aggregate(g.First().Mode, g.Key, [.. g]));

    private static void WriteLegacyConsoleSummary(IReadOnlyList<EvaluationRecord> records)
    {
        foreach (IGrouping<string, EvaluationRecord> byMode in records.GroupBy(r => r.Mode))
        {
            Console.WriteLine($"=== {byMode.Key} ===");
            Console.WriteLine(
                $"{"strategy",-26}{"nDCG",8}{"recall",8}{"RBO",8}{"cross",8}{"CU",10}{"p50ms",8}");

            IEnumerable<StrategySummary> summaries = byMode
                .GroupBy(r => r.Strategy)
                .Select(g => StrategySummary.Aggregate(byMode.Key, g.Key, [.. g]))
                .OrderByDescending(s => s.Ndcg);

            foreach (StrategySummary s in summaries)
            {
                double cross = CrossStripeNdcg(byMode, s.Strategy);

                Console.WriteLine(
                    $"{s.Strategy,-26}{s.Ndcg,8:F3}{s.Recall,8:F3}{s.RankBiasedOverlap,8:F3}"
                    + $"{cross,8:F3}{s.ComputeUnits,10:F4}{s.LatencyP50Ms,8:F0}");
            }

            Console.WriteLine();
        }
    }


    private static double CrossStripeNdcg(IEnumerable<EvaluationRecord> records, string strategy)
    {
        double[] values =
        [
            .. records
                .Where(r => r.Strategy == strategy && r.Span == QuerySpan.CrossStripe)
                .Select(r => r.Ndcg)
        ];

        return values.Length == 0 ? 0d : values.Average();
    }
}
