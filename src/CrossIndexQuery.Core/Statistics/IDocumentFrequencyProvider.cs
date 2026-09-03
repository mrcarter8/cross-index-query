using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using CrossIndexQuery.Core.Clients;
using CrossIndexQuery.Core.Models;
using CrossIndexQuery.Core.Telemetry;

namespace CrossIndexQuery.Core.Statistics;

/// <summary>
/// Supplies the document frequency of a term, globally and per index.
/// </summary>
/// <remarks>
/// Two implementations exist because they occupy opposite ends of a real trade-off: one is free at
/// query time but requires an offline pass over the corpus, the other needs no preparation at all
/// but pays for its numbers in extra requests. Which is preferable depends on whether the corpus
/// is static, and the evaluation harness measures both.
/// </remarks>
public interface IDocumentFrequencyProvider
{
    /// <summary>Human-readable name of how these numbers were obtained.</summary>
    string Source { get; }

    ValueTask<TermFrequencies> GetAsync(
        IReadOnlyList<string> terms,
        IReadOnlyList<string> indexNames,
        CancellationToken cancellationToken = default);
}

/// <summary>Document frequencies for one query's terms, global and per index.</summary>
/// <param name="GlobalDocumentCount">Documents in the whole logical corpus.</param>
/// <param name="LocalDocumentCounts">Documents in each index.</param>
/// <param name="GlobalDocumentFrequency">Corpus-wide document frequency per term.</param>
/// <param name="LocalDocumentFrequency">Per-index document frequency, keyed by index then term.</param>
public sealed record TermFrequencies(
    int GlobalDocumentCount,
    IReadOnlyDictionary<string, int> LocalDocumentCounts,
    IReadOnlyDictionary<string, int> GlobalDocumentFrequency,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> LocalDocumentFrequency);

/// <summary>
/// Reads document frequencies from the statistics sidecar built during data preparation.
/// </summary>
/// <remarks>
/// Free at query time — no additional requests, no added latency — at the cost of a file that goes
/// stale the moment the corpus changes. Appropriate for a corpus that is rebuilt rather than
/// continuously updated, which describes most systems large enough to need striping in the first
/// place.
/// </remarks>
public sealed class SidecarDocumentFrequencyProvider(CorpusStatistics statistics) : IDocumentFrequencyProvider
{
    public string Source => "sidecar";

    public ValueTask<TermFrequencies> GetAsync(
        IReadOnlyList<string> terms,
        IReadOnlyList<string> indexNames,
        CancellationToken cancellationToken = default)
    {
        Dictionary<string, int> global = new(StringComparer.Ordinal);
        Dictionary<string, IReadOnlyDictionary<string, int>> local = new(StringComparer.Ordinal);
        Dictionary<string, int> counts = new(StringComparer.Ordinal);

        foreach (string term in terms)
        {
            global[term] = statistics.GlobalDocumentFrequency(term);
        }

        foreach (string index in indexNames)
        {
            counts[index] = statistics.LocalDocumentCount(index);

            Dictionary<string, int> perTerm = new(StringComparer.Ordinal);
            foreach (string term in terms)
            {
                perTerm[term] = statistics.LocalDocumentFrequency(index, term);
            }

            local[index] = perTerm;
        }

        return ValueTask.FromResult(new TermFrequencies(
            statistics.DocumentCount, counts, global, local));
    }
}

/// <summary>
/// Recovers document frequencies at query time by asking each index how many documents match each
/// term, using a counting query that returns no documents.
/// </summary>
/// <remarks>
/// <para>
/// This is the interesting one, because it needs nothing prepared in advance. A query for a single
/// term with <c>$top=0&amp;$count=true</c> returns precisely that term's document frequency in that
/// index, and summing across the stripes gives the corpus-wide figure that no individual index
/// knows. The statistics the striping destroyed can simply be asked for.
/// </para>
/// <para>
/// The cost is real but small and bounded: one lightweight request per query term per index,
/// retrieving zero documents. Probes are cached for the lifetime of the provider, so a repeated
/// term is paid for once. It works against a corpus that changes continuously, where a sidecar
/// would be wrong.
/// </para>
/// </remarks>
public sealed class ProbeDocumentFrequencyProvider(
    SearchClientFactory factory,
    IReadOnlyList<string> searchFields) : IDocumentFrequencyProvider
{
    private readonly Dictionary<(string Index, string Term), int> _cache = [];
    private readonly Dictionary<string, int> _documentCounts = new(StringComparer.Ordinal);

    public string Source => "probe";

    public async ValueTask<TermFrequencies> GetAsync(
        IReadOnlyList<string> terms,
        IReadOnlyList<string> indexNames,
        CancellationToken cancellationToken = default)
    {
        Dictionary<string, IReadOnlyDictionary<string, int>> local = new(StringComparer.Ordinal);
        Dictionary<string, int> global = new(StringComparer.Ordinal);
        Dictionary<string, int> counts = new(StringComparer.Ordinal);

        foreach (string term in terms)
        {
            global[term] = 0;
        }

        foreach (string index in indexNames)
        {
            counts[index] = await GetDocumentCountAsync(index, cancellationToken).ConfigureAwait(false);

            Dictionary<string, int> perTerm = new(StringComparer.Ordinal);

            foreach (string term in terms)
            {
                int df = await ProbeAsync(index, term, cancellationToken).ConfigureAwait(false);
                perTerm[term] = df;
                global[term] += df;
            }

            local[index] = perTerm;
        }

        return new TermFrequencies(counts.Values.Sum(), counts, global, local);
    }

    private async ValueTask<int> ProbeAsync(string index, string term, CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue((index, term), out int cached))
        {
            return cached;
        }

        var options = new SearchOptions
        {
            Size = 0,
            IncludeTotalCount = true,
            SearchMode = SearchMode.All,
            QueryType = SearchQueryType.Simple,
        };

        foreach (string field in searchFields)
        {
            options.SearchFields.Add(field);
        }

        using ComputeUnitScope scope = ComputeUnitScope.Begin($"probe-df:{index}:{term}");

        SearchClient client = factory.GetSearchClient(index);
        SearchResults<BookDocument> results = await client
            .SearchAsync<BookDocument>(term, options, cancellationToken)
            .ConfigureAwait(false);

        int df = (int)(results.TotalCount ?? 0);
        _cache[(index, term)] = df;
        return df;
    }

    private async ValueTask<int> GetDocumentCountAsync(string index, CancellationToken cancellationToken)
    {
        if (_documentCounts.TryGetValue(index, out int cached))
        {
            return cached;
        }

        using ComputeUnitScope scope = ComputeUnitScope.Begin($"count:{index}");

        SearchClient client = factory.GetSearchClient(index);
        long count = await client.GetDocumentCountAsync(cancellationToken).ConfigureAwait(false);

        int value = (int)count;
        _documentCounts[index] = value;
        return value;
    }
}
