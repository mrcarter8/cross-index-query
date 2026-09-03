using CrossIndexQuery.Core.Clients;
using CrossIndexQuery.Core.Configuration;
using CrossIndexQuery.Core.Fusion;
using CrossIndexQuery.Core.Retrieval;

namespace CrossIndexQuery.Cli.Commands;

/// <summary>
/// Runs one query across the stripes, fuses it, and optionally shows the arithmetic.
/// </summary>
/// <remarks>
/// The explain output is the teaching surface of the sample. The evaluation harness proves which
/// strategy wins on aggregate; this shows a reader a single concrete query where the raw scores
/// from two indexes disagree, and what a given strategy did about it. Seeing one document with a
/// higher BM25 score than another and being ranked below it anyway is what makes the abstract point
/// about corpus statistics land.
/// </remarks>
public sealed class QueryCommand(CrossIndexOptions options)
{
    public async Task<int> RunAsync(
        string query,
        RetrievalMode mode,
        string strategyName,
        bool semantic,
        bool explain,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var factory = new SearchClientFactory(options.Search);
        var retriever = new MultiStripeRetriever(new StripeRetriever(factory), options);
        FusionStrategyRegistry registry = FusionStrategyRegistry.CreateDefault(factory, options);

        if (!registry.TryGet(strategyName, out IFusionStrategy? strategy) || strategy is null)
        {
            Console.Error.WriteLine($"Unknown strategy '{strategyName}'. Available:");
            foreach (IFusionStrategy known in registry.All.OrderBy(s => s.Name, StringComparer.Ordinal))
            {
                Console.Error.WriteLine($"  {known.Name,-24} {known.Description}");
            }

            return 1;
        }

        if (!strategy.Supports(mode))
        {
            Console.Error.WriteLine($"Strategy '{strategy.Name}' does not apply to {mode} retrieval.");
            return 1;
        }

        ReadOnlyMemory<float>? vector = null;
        if (mode is RetrievalMode.Vector or RetrievalMode.Hybrid)
        {
            var embedder = new AzureOpenAIQueryEmbedder(options);
            vector = await embedder.EmbedAsync(query, cancellationToken).ConfigureAwait(false);
        }

        var request = new RetrievalRequest
        {
            Query = query,
            Mode = mode,
            QueryVector = vector,
            Size = options.Evaluation.PerStripeK,
            UseSemanticRanker = semantic,
        };

        FanOutResult fanOut = await retriever.SearchStripesAsync(request, cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<FusedDocument> fused = await strategy
            .FuseAsync(fanOut, new FusionContext(options.Evaluation.TopK, vector), cancellationToken)
            .ConfigureAwait(false);

        if (explain)
        {
            WritePerStripe(fanOut);
        }

        WriteFused(query, mode, strategy, fanOut, fused, explain);
        return 0;
    }

    /// <summary>
    /// Shows what each index returned before anything was merged.
    /// </summary>
    /// <remarks>
    /// Printing the two lists side by side is the fastest way to see the problem the sample exists
    /// to solve. Two stripes routinely return their top result with scores that differ by a factor
    /// of two or more, and neither index is wrong — they simply computed relevance against
    /// different corpora.
    /// </remarks>
    private static void WritePerStripe(FanOutResult fanOut)
    {
        foreach (StripeResultSet stripe in fanOut.Stripes)
        {
            Console.WriteLine();
            Console.WriteLine(
                $"--- {stripe.IndexName}: {stripe.Documents.Count} results in {stripe.Elapsed.TotalMilliseconds:F0} ms, "
                + $"{stripe.ComputeUnits:F4} CU ---");

            foreach (ScoredDocument doc in stripe.Documents.Take(10))
            {
                Console.WriteLine(
                    $"  {doc.Rank,2}. {Truncate(doc.Document.Title, 52),-52} {FormatScores(doc)}");
            }
        }

        foreach (StripeFailure failure in fanOut.Failures)
        {
            Console.WriteLine();
            Console.WriteLine($"--- {failure.IndexName}: FAILED ({failure.Status}) {failure.Message}");
        }
    }

    private static void WriteFused(
        string query,
        RetrievalMode mode,
        IFusionStrategy strategy,
        FanOutResult fanOut,
        IReadOnlyList<FusedDocument> fused,
        bool explain)
    {
        Console.WriteLine();
        Console.WriteLine($"=== {query} | {mode} | {strategy.Name} ===");
        Console.WriteLine(strategy.Description);
        Console.WriteLine();

        int rank = 0;
        foreach (FusedDocument doc in fused)
        {
            rank++;
            Console.WriteLine(
                $"{rank,2}. {Truncate(doc.Source.Document.Title, 52),-52} [{ShortIndex(doc.SourceIndex)}]");

            if (explain)
            {
                Console.WriteLine($"      {doc.Explanation}");
            }
        }

        Console.WriteLine();
        Console.WriteLine(
            $"{fanOut.QueryCount} queries, {fanOut.ComputeUnits:F4} compute units, "
            + $"{fanOut.Elapsed.TotalMilliseconds:F0} ms wall clock.");

        // Where the results came from is worth surfacing on every query. A fused list drawn
        // entirely from one stripe is either a correct answer to a stripe-local query or a fusion
        // strategy that has quietly collapsed, and the two look identical without this line.
        var mix = fused
            .GroupBy(d => d.SourceIndex, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{ShortIndex(g.Key)}={g.Count()}");

        Console.WriteLine($"Stripe mix: {string.Join("  ", mix)}");
    }

    private static string FormatScores(ScoredDocument doc)
    {
        List<string> parts = [$"score={doc.Score:F4}"];

        if (doc.TextScore is { } text)
        {
            parts.Add($"bm25={text:F4}");
        }

        if (doc.VectorSimilarity is { } similarity)
        {
            parts.Add($"cos={similarity:F4}");
        }

        if (doc.RerankerScore is { } reranker)
        {
            parts.Add($"rerank={reranker:F3}");
        }

        return string.Join("  ", parts);
    }

    private static string ShortIndex(string indexName)
    {
        int dash = indexName.LastIndexOf('-');
        return dash >= 0 && dash < indexName.Length - 1 ? indexName[(dash + 1)..] : indexName;
    }

    private static string Truncate(string? value, int length)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= length ? value : string.Concat(value.AsSpan(0, length - 1), "…");
    }
}
