using System.Diagnostics;
using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using CrossIndexQuery.Core.Clients;
using CrossIndexQuery.Core.Indexing;
using CrossIndexQuery.Core.Models;
using CrossIndexQuery.Core.Telemetry;

namespace CrossIndexQuery.Core.Retrieval;

/// <summary>
/// Issues one query against one index and normalizes the response into <see cref="ScoredDocument"/>.
/// </summary>
/// <remarks>
/// <para>
/// Two details here matter more than they look.
/// </para>
/// <para>
/// <b>Global scoring statistics.</b> By default BM25 is computed per shard, so the same query
/// against the same index can return slightly different scores run to run. That is invisible noise
/// in normal use and fatal in a benchmark: it would be indistinguishable from the cross-index
/// effect being measured. Requesting global statistics costs latency and buys determinism.
/// </para>
/// <para>
/// <b>Debug subscores.</b> A hybrid query returns an RRF score built from ranks, which discards the
/// underlying BM25 and cosine values — the very numbers a good fusion strategy needs. Asking for
/// vector debug information returns both component scores on every hit, so the legs can be
/// separated without issuing a second and third query per stripe.
/// </para>
/// </remarks>
public sealed class StripeRetriever(SearchClientFactory factory)
{
    public async Task<StripeResultSet> SearchAsync(
        string indexName,
        RetrievalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        SearchClient client = factory.GetSearchClient(indexName);
        SearchOptions options = BuildOptions(request);

        string? searchText = request.Mode == RetrievalMode.Vector ? null : request.Query;

        using ComputeUnitScope scope = ComputeUnitScope.Begin($"{request.Mode.ToString().ToLowerInvariant()}:{indexName}");
        long start = Stopwatch.GetTimestamp();

        Response<SearchResults<BookDocument>> response = await client
            .SearchAsync<BookDocument>(searchText, options, cancellationToken)
            .ConfigureAwait(false);

        List<ScoredDocument> documents = [];
        int rank = 0;

        await foreach (SearchResult<BookDocument> hit in response.Value.GetResultsAsync()
            .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            rank++;
            (double? textScore, double? similarity) = ReadSubscores(hit);

            documents.Add(new ScoredDocument(
                Document: hit.Document,
                SourceIndex: indexName,
                Rank: rank,
                Score: hit.Score ?? 0d,
                TextScore: textScore,
                VectorSimilarity: similarity,
                RerankerScore: hit.SemanticSearch?.RerankerScore));
        }

        TimeSpan elapsed = Stopwatch.GetElapsedTime(start);

        return new StripeResultSet(
            IndexName: indexName,
            Mode: request.Mode,
            Documents: documents,
            TotalCount: response.Value.TotalCount,
            Elapsed: elapsed,
            ComputeUnits: scope.TotalComputeUnits ?? 0d);
    }

    private static SearchOptions BuildOptions(RetrievalRequest request)
    {
        var options = new SearchOptions
        {
            Size = request.Size,
            IncludeTotalCount = request.IncludeTotalCount,
            QueryType = request.UseSemanticRanker ? SearchQueryType.Semantic : SearchQueryType.Simple,

            // Determinism. See the class remarks: without this, shard-local BM25 adds run-to-run
            // noise that cannot be told apart from the cross-index effect being measured.
            ScoringStatistics = ScoringStatistics.Global,
        };

        foreach (string field in BookIndexSchema.DefaultSelect)
        {
            options.Select.Add(field);
        }

        if (request.Mode is RetrievalMode.Keyword or RetrievalMode.Hybrid)
        {
            foreach (string field in BookIndexSchema.TextSearchFields)
            {
                options.SearchFields.Add(field);
            }
        }

        if (request.Mode is RetrievalMode.Vector or RetrievalMode.Hybrid)
        {
            if (request.QueryVector is null)
            {
                throw new ArgumentException(
                    $"{request.Mode} retrieval requires a query vector.", nameof(request));
            }

            options.VectorSearch = new VectorSearchOptions
            {
                Queries =
                {
                    new VectorizedQuery(request.QueryVector.Value)
                    {
                        KNearestNeighborsCount = request.Size,
                        Fields = { BookIndexSchema.VectorFieldName },

                        // Off by default, because HNSW is what anyone actually runs. Available
                        // because it separates two effects that otherwise arrive as one number:
                        // vector scores are perfectly comparable across indexes, so a striped
                        // vector search should reproduce the single-index ranking exactly — but
                        // HNSW is approximate, and traversing two smaller graphs does not visit the
                        // same neighbours as traversing one large one. Any shortfall under
                        // exhaustive search is attributable to striping; the difference between
                        // exhaustive and HNSW is the approximation, and belongs to the algorithm
                        // rather than to the split.
                        Exhaustive = request.ExhaustiveVectorSearch,
                    },
                },
            };

            // Recovers the component scores that RRF would otherwise hide.
            options.Debug = QueryDebugMode.Vector;
        }

        if (request.UseSemanticRanker)
        {
            options.SemanticSearch = new SemanticSearchOptions
            {
                SemanticConfigurationName = BookIndexSchema.SemanticConfigurationName,

                // A stripe that genuinely has nothing to contribute should return its BM25 list
                // rather than fail the whole fan-out.
                ErrorMode = SemanticErrorMode.Partial,
            };
        }

        if (!string.IsNullOrWhiteSpace(request.Filter))
        {
            options.Filter = request.Filter;
        }

        return options;
    }

    private static (double? TextScore, double? VectorSimilarity) ReadSubscores(SearchResult<BookDocument> hit)
    {
        QueryResultDocumentSubscores? subscores = hit.DocumentDebugInfo?.Vectors?.Subscores;
        if (subscores is null)
        {
            return (null, null);
        }

        double? textScore = subscores.Text?.SearchScore;

        // Vectors is a list of maps keyed by field name, one entry per vector query. The sample
        // issues a single vector query over a single field, so take the best value present rather
        // than assuming a fixed shape.
        //
        // Entries can be null: a document retrieved by the text leg alone has no vector result to
        // report, and the service returns a hole in the list rather than omitting it. That is the
        // same leg-attribution behaviour probe #3 observed, seen from the other side, so it is
        // normal data rather than a fault — skip it and let the missing similarity mean what it
        // says.
        double? similarity = null;
        if (subscores.Vectors is { Count: > 0 })
        {
            foreach (IDictionary<string, SingleVectorFieldResult>? map in subscores.Vectors)
            {
                if (map is null)
                {
                    continue;
                }

                if (map.TryGetValue(BookIndexSchema.VectorFieldName, out SingleVectorFieldResult? field)
                    && field?.VectorSimilarity is { } value)
                {
                    similarity = similarity is null ? value : Math.Max(similarity.Value, value);
                }
            }
        }

        return (textScore, similarity);
    }
}

/// <summary>One query, expressed independently of which index will answer it.</summary>
public sealed record RetrievalRequest
{
    public required string Query { get; init; }

    public required RetrievalMode Mode { get; init; }

    /// <summary>
    /// Embedding of <see cref="Query"/>, produced by the same model that built the corpus.
    /// Required for vector and hybrid retrieval.
    /// </summary>
    public ReadOnlyMemory<float>? QueryVector { get; init; }

    /// <summary>
    /// Forces exact nearest-neighbour search instead of the index's ANN algorithm.
    /// </summary>
    /// <remarks>
    /// Slower, and not how anyone runs in production. It exists so the study can tell two things
    /// apart. Vector similarity consults no corpus statistics, so a striped vector search ought to
    /// reproduce the single-index ranking exactly — but HNSW is approximate, and two smaller graphs
    /// are not traversed the same way as one large one. Without this flag, that approximation error
    /// is indistinguishable from a cost of striping, and would be reported as one.
    /// </remarks>
    public bool ExhaustiveVectorSearch { get; init; }

    /// <summary>
    /// Documents to request from each index. Deliberately larger than the final result size:
    /// fusion can only reorder what it was given, so a shallow per-stripe fetch caps the best
    /// achievable quality no matter how good the strategy is.
    /// </summary>
    public int Size { get; init; } = 50;

    public bool UseSemanticRanker { get; init; }

    /// <summary>Requests the document count, which the probe-IDF strategy uses to recover statistics.</summary>
    public bool IncludeTotalCount { get; init; }

    public string? Filter { get; init; }
}

