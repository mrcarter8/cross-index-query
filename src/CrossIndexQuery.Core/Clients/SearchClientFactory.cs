using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using CrossIndexQuery.Core.Configuration;
using CrossIndexQuery.Core.Telemetry;

namespace CrossIndexQuery.Core.Clients;

/// <summary>
/// Creates Azure AI Search clients that all share one credential and one measurement policy.
/// </summary>
/// <remarks>
/// <para>
/// Every index in this sample lives on the <em>same</em> search service. That is the whole premise:
/// the corpus was split not because it spans regions or teams, but because a single index ran out
/// of room. So there is exactly one endpoint and one credential, and the only thing that varies
/// per client is the index name.
/// </para>
/// <para>
/// Clients are cached per index name because <see cref="SearchClient"/> is thread-safe and holds a
/// connection pool; creating one per query would defeat connection reuse and distort the latency
/// numbers the evaluation harness reports.
/// </para>
/// </remarks>
public sealed class SearchClientFactory
{
    private readonly SearchServiceOptions _options;
    private readonly TokenCredential? _credential;
    private readonly AzureKeyCredential? _keyCredential;
    private readonly Dictionary<string, SearchClient> _clients = [];
    private readonly Lock _gate = new();

    public SearchClientFactory(SearchServiceOptions options, TokenCredential? credential = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Endpoint))
        {
            throw new InvalidOperationException(
                "Search:Endpoint is not configured. Run 'cross-index-query doctor' for setup guidance.");
        }

        _options = options;
        Endpoint = new Uri(options.Endpoint);

        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            _keyCredential = new AzureKeyCredential(options.ApiKey);
        }
        else
        {
            _credential = credential ?? new DefaultAzureCredential();
        }
    }

    public Uri Endpoint { get; }

    /// <summary>True when authenticating with Microsoft Entra ID rather than an admin key.</summary>
    public bool UsesEntraId => _keyCredential is null;

    /// <summary>Gets a cached query client for the given index.</summary>
    public SearchClient GetSearchClient(string indexName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);

        lock (_gate)
        {
            if (_clients.TryGetValue(indexName, out SearchClient? existing))
            {
                return existing;
            }

            SearchClient created = _keyCredential is not null
                ? new SearchClient(Endpoint, indexName, _keyCredential, CreateClientOptions())
                : new SearchClient(Endpoint, indexName, _credential!, CreateClientOptions());

            _clients[indexName] = created;
            return created;
        }
    }

    /// <summary>Creates a management client for index and knowledge-base definitions.</summary>
    public SearchIndexClient CreateIndexClient() =>
        _keyCredential is not null
            ? new SearchIndexClient(Endpoint, _keyCredential, CreateIndexClientOptions())
            : new SearchIndexClient(Endpoint, _credential!, CreateIndexClientOptions());

    /// <summary>The credential in use, for clients this factory does not construct directly.</summary>
    public TokenCredential? TokenCredential => _credential;

    /// <summary>The key credential in use, if key auth was configured.</summary>
    public AzureKeyCredential? KeyCredential => _keyCredential;

    private static SearchClientOptions CreateClientOptions()
    {
        var options = new SearchClientOptions();
        options.AddPolicy(new ComputeUnitPolicy(), HttpPipelinePosition.PerCall);
        return options;
    }

    private static SearchClientOptions CreateIndexClientOptions() => CreateClientOptions();

    /// <summary>Configured index names, in the order the sample refers to them.</summary>
    public IReadOnlyList<string> AllIndexNames =>
        [_options.StripeAIndex, _options.StripeBIndex, _options.OracleIndex];
}
