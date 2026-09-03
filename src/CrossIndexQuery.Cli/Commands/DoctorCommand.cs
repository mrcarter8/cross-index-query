using System.Diagnostics;
using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using CrossIndexQuery.Core;
using CrossIndexQuery.Core.Clients;
using CrossIndexQuery.Core.Configuration;
using CrossIndexQuery.Core.Retrieval;
using CrossIndexQuery.Core.Statistics;

namespace CrossIndexQuery.Cli.Commands;

/// <summary>
/// Checks that the deployed service can actually support the experiment before anyone runs it.
/// </summary>
/// <remarks>
/// <para>
/// Most of these checks exist because the corresponding failure is silent. A dimension mismatch
/// between two indexes does not throw; it returns confidently wrong similarities. A rejected debug
/// flag does not throw either; it just omits the subscores, and the strategies that depend on them
/// quietly degrade to something else. Both would show up in the results table as a fusion strategy
/// that "did not work well", which is the worst possible way to learn about a configuration error.
/// </para>
/// </remarks>
public sealed class DoctorCommand(CrossIndexOptions options)
{
    /// <summary>
    /// Text used for every live probe, and for the vector the probes are issued with.
    /// </summary>
    /// <remarks>
    /// One string for all of them so the hybrid probe's two legs actually agree. Searching for one
    /// thing while supplying the embedding of another produces a query whose text leg contributes
    /// nothing, and the resulting "no text subscore" looks identical to the service having withheld
    /// it. It is also a content word rather than a stop word, so the text leg has real matches to
    /// return.
    /// </remarks>
    private const string ProbeQuery = "dragon";

    private int _failures;
    private int _warnings;

    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        var factory = new SearchClientFactory(options.Search);

        Console.WriteLine($"Endpoint     {options.Search.Endpoint}");
        Console.WriteLine($"Credential   {(factory.UsesEntraId ? "Entra ID (DefaultAzureCredential)" : "API key")}");
        Console.WriteLine($"Indexes      {string.Join(", ", factory.AllIndexNames)}");
        Console.WriteLine();

        IReadOnlyList<SearchIndex>? indexes = await CheckIndexesAsync(factory, cancellationToken)
            .ConfigureAwait(false);

        if (indexes is not null)
        {
            CheckVectorConsistency(indexes);
            CheckSemanticConfiguration(indexes);
        }

        ReadOnlyMemory<float>? queryVector = await CheckEmbeddingAsync(cancellationToken)
            .ConfigureAwait(false);

        await CheckQueryFeaturesAsync(factory, queryVector, cancellationToken).ConfigureAwait(false);
        CheckCorpusStatistics();

        Console.WriteLine();
        if (_failures > 0)
        {
            Console.WriteLine($"{_failures} failure(s), {_warnings} warning(s).");
            return 1;
        }

        Console.WriteLine($"All checks passed ({_warnings} warning(s)).");
        return 0;
    }

    /// <summary>
    /// Fetches each index by name.
    /// </summary>
    /// <remarks>
    /// Deliberately not <c>GetIndexes()</c>: serverless services reject unpaged enumeration of
    /// resources, so every index in this sample is addressed by name.
    /// </remarks>
    private async Task<IReadOnlyList<SearchIndex>?> CheckIndexesAsync(
        SearchClientFactory factory,
        CancellationToken cancellationToken)
    {
        SearchIndexClient client = factory.CreateIndexClient();
        List<SearchIndex> found = [];

        foreach (string name in factory.AllIndexNames)
        {
            try
            {
                Response<SearchIndex> response = await client
                    .GetIndexAsync(name, cancellationToken).ConfigureAwait(false);

                SearchClient searchClient = factory.GetSearchClient(name);
                long count = await searchClient.GetDocumentCountAsync(cancellationToken)
                    .ConfigureAwait(false);

                found.Add(response.Value);
                Pass($"index {name} exists ({count:N0} documents)");

                if (count == 0)
                {
                    Warn($"index {name} is empty; run 'init' before querying");
                }
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                Fail($"index {name} does not exist; run 'init'");
            }
            catch (RequestFailedException ex)
            {
                Fail($"index {name} unreachable ({ex.Status}): {ex.Message}");
            }
        }

        return found.Count == factory.AllIndexNames.Count ? found : null;
    }

    /// <summary>
    /// Confirms every index embeds into the same space.
    /// </summary>
    /// <remarks>
    /// Cosine similarity is comparable across indexes only because the vectors are coordinates in
    /// one shared space. Two indexes built with different models, or the same model at different
    /// dimensionalities, produce similarities that are individually meaningful and jointly
    /// meaningless. Every vector-side conclusion in this sample rests on this check.
    /// </remarks>
    private void CheckVectorConsistency(IReadOnlyList<SearchIndex> indexes)
    {
        Dictionary<string, int> dimensions = new(StringComparer.Ordinal);

        foreach (SearchIndex index in indexes)
        {
            SearchField? vectorField = index.Fields
                .FirstOrDefault(f => f.VectorSearchDimensions is not null);

            if (vectorField?.VectorSearchDimensions is not { } dims)
            {
                Fail($"index {index.Name} has no vector field");
                continue;
            }

            dimensions[index.Name] = dims;
        }

        if (dimensions.Count == 0)
        {
            return;
        }

        int[] distinct = [.. dimensions.Values.Distinct()];

        if (distinct.Length > 1)
        {
            Fail(
                "indexes disagree on vector dimensions ("
                + string.Join(", ", dimensions.Select(kv => $"{kv.Key}={kv.Value}"))
                + "); cross-index vector comparison is invalid");
        }
        else if (distinct[0] != options.Embedding.Dimensions)
        {
            Fail(
                $"indexes are {distinct[0]}-dimensional but configuration expects "
                + $"{options.Embedding.Dimensions}; query vectors will not match the corpus");
        }
        else
        {
            Pass($"all indexes share one {distinct[0]}-dimensional vector space");
        }
    }

    private void CheckSemanticConfiguration(IReadOnlyList<SearchIndex> indexes)
    {
        foreach (SearchIndex index in indexes)
        {
            int configurations = index.SemanticSearch?.Configurations.Count ?? 0;

            if (configurations == 0)
            {
                Warn($"index {index.Name} has no semantic configuration; semantic strategies will fail");
            }
        }

        if (indexes.All(i => i.SemanticSearch?.Configurations.Count > 0))
        {
            Pass("every index has a semantic configuration");
        }
    }

    /// <summary>
    /// Verifies the embedding deployment, and hands back the vector it produced.
    /// </summary>
    /// <remarks>
    /// The vector is returned rather than discarded so the query-feature probe can issue a genuine
    /// hybrid query. Debug subscores only exist on a query that has a vector leg, so checking for
    /// them without one reports absence that the query shape guaranteed.
    /// </remarks>
    private async Task<ReadOnlyMemory<float>?> CheckEmbeddingAsync(CancellationToken cancellationToken)
    {
        try
        {
            var embedder = new AzureOpenAIQueryEmbedder(options);
            ReadOnlyMemory<float> vector = await embedder
                .EmbedAsync(ProbeQuery, cancellationToken).ConfigureAwait(false);

            if (vector.Length != options.Embedding.Dimensions)
            {
                Fail(
                    $"embedding deployment returned {vector.Length} dimensions, configuration says "
                    + $"{options.Embedding.Dimensions}");
                return null;
            }

            Pass($"embedding deployment '{options.Embedding.Deployment}' returns {vector.Length} dimensions");
            return vector;
        }
        catch (Exception ex) when (ex is RequestFailedException or InvalidOperationException)
        {
            Fail($"embedding call failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Probes the two query features whose availability the sample cannot assume.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>ScoringStatistics.Global</c> matters for reproducibility rather than correctness: without
    /// it, BM25 is computed per shard, and repeated runs of the same query differ by an amount that
    /// is easy to mistake for the cross-index effect being measured.
    /// </para>
    /// <para>
    /// The debug subscores matter for capability. They are what let a hybrid query report its BM25
    /// and cosine legs separately without paying for two more round trips, and several fusion
    /// strategies read them. If they are unavailable the sample still runs, but those strategies
    /// have to fall back to issuing the legs as separate queries.
    /// </para>
    /// <para>
    /// The subscore probe therefore has to be a <em>hybrid</em> query. <c>StripeRetriever</c> only
    /// sets <c>QueryDebugMode.Vector</c> when the request has a vector leg, so probing with a
    /// keyword query reports the subscores missing every time regardless of what the service
    /// supports — a warning that is always wrong is worse than no warning, because it trains you to
    /// ignore the one case where it is right.
    /// </para>
    /// </remarks>
    private async Task CheckQueryFeaturesAsync(
        SearchClientFactory factory,
        ReadOnlyMemory<float>? queryVector,
        CancellationToken cancellationToken)
    {
        string probeIndex = options.Search.StripeIndexes[0];
        var retriever = new StripeRetriever(factory);

        var request = new RetrievalRequest
        {
            Query = ProbeQuery,
            Mode = RetrievalMode.Keyword,
            Size = 3,
        };

        try
        {
            var stopwatch = Stopwatch.StartNew();
            StripeResultSet result = await retriever
                .SearchAsync(probeIndex, request, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            Pass(
                $"keyword query against {probeIndex} succeeded "
                + $"({stopwatch.ElapsedMilliseconds} ms, {result.ComputeUnits:F4} CU)");

            if (result.ComputeUnits > 0)
            {
                Pass("service reports x-ms-azs-compute-units-consumed; cost will be measured, not estimated");
            }
            else
            {
                Warn("no compute-unit header seen; cost columns will be blank (expected on non-serverless tiers)");
            }
        }
        catch (RequestFailedException ex)
        {
            Fail($"probe query failed ({ex.Status}): {ex.Message}");
            return;
        }

        await CheckDebugSubscoresAsync(retriever, probeIndex, queryVector, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Issues a hybrid query and confirms the service returns the per-leg debug subscores.
    /// </summary>
    /// <remarks>
    /// A hybrid query fuses its text and vector legs into a single RRF score and discards the
    /// components, so <c>HybridLegFusion</c> depends on the debug subscores to recover them. If the
    /// service stops returning them the strategy does not throw — it silently sees no text leg and
    /// treats every document as vector-only, which shows up much later as an unexplained relevance
    /// result rather than as an error.
    /// </remarks>
    private async Task CheckDebugSubscoresAsync(
        StripeRetriever retriever,
        string probeIndex,
        ReadOnlyMemory<float>? queryVector,
        CancellationToken cancellationToken)
    {
        if (queryVector is null)
        {
            Warn("skipped the debug-subscore check because no query vector was available");
            return;
        }

        var request = new RetrievalRequest
        {
            Query = ProbeQuery,
            Mode = RetrievalMode.Hybrid,

            // Deep enough that both legs are represented. RRF can fill a small top-k entirely from
            // whichever leg ranks more confidently, so a size-3 probe can miss the text leg even
            // when the service is reporting it correctly.
            Size = 10,
            QueryVector = queryVector,
        };

        try
        {
            StripeResultSet result = await retriever
                .SearchAsync(probeIndex, request, cancellationToken).ConfigureAwait(false);

            bool sawText = result.Documents.Any(d => d.TextScore is not null);
            bool sawVector = result.Documents.Any(d => d.VectorSimilarity is not null);

            if (sawText && sawVector)
            {
                Pass("debug subscores available; hybrid legs can be separated without extra queries");
            }
            else
            {
                Warn(
                    $"hybrid debug subscores incomplete (text: {sawText}, vector: {sawVector}); "
                    + "leg-decomposition strategies will need separate queries per leg");
            }
        }
        catch (RequestFailedException ex)
        {
            Warn($"hybrid subscore probe failed ({ex.Status}): {ex.Message}");
        }
    }

    private void CheckCorpusStatistics()
    {
        string dataDirectory = RepositoryLocator.ResolveDataDirectory(options.Corpus.DataDirectory);

        if (CorpusStatistics.TryLoad(dataDirectory, options.Corpus.SplitDescriptor, out CorpusStatistics? stats)
            && stats is not null)
        {
            Pass(
                $"corpus statistics loaded ({stats.DocumentCount:N0} documents, "
                + $"{stats.DocumentFrequencies.Count:N0} terms)");
        }
        else
        {
            Warn(
                "no corpus-statistics.json; the sidecar IDF strategy will be skipped "
                + "(run 'dataprep stats')");
        }
    }

    private static void Pass(string message) => Console.WriteLine($"  [ ok ] {message}");

    private void Warn(string message)
    {
        _warnings++;
        Console.WriteLine($"  [warn] {message}");
    }

    private void Fail(string message)
    {
        _failures++;
        Console.Error.WriteLine($"  [FAIL] {message}");
    }
}
