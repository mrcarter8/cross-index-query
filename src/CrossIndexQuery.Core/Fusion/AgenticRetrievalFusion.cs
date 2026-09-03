using System.Text.Json;
using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.Search.Documents.KnowledgeBases;
using Azure.Search.Documents.KnowledgeBases.Models;
using CrossIndexQuery.Core.Configuration;
using CrossIndexQuery.Core.Models;
using CrossIndexQuery.Core.Retrieval;
using CrossIndexQuery.Core.Telemetry;

namespace CrossIndexQuery.Core.Fusion;

/// <summary>
/// Delegates both retrieval and collation to the service, over a knowledge base spanning every
/// stripe.
/// </summary>
/// <remarks>
/// <para>
/// Every other strategy in this catalog is handed a fan-out and decides how to merge it. This one
/// is not merging anything: it issues its own retrieval against a knowledge base that references
/// both stripe indexes, and the service plans the subqueries, retrieves from each source and
/// returns one ranked list. The client-side collation problem does not arise, because there is no
/// client-side collation.
/// </para>
/// <para>
/// It is included as a peer to the fusion strategies rather than as one of them, and the harness
/// charges it only for the requests it actually issued. Charging it for the shared fan-out as well
/// would overstate its cost by exactly the work it declined to use, which for the most expensive
/// option in the comparison is the difference between an honest number and a misleading one.
/// </para>
/// <para>
/// Results are ordered by <c>RerankerScore</c> — the same absolute cross-encoder scale the semantic
/// strategies use, and for the same reason it is safe there: it consults no corpus statistics, so a
/// score from one source means what it means from the other. References that arrive without one are
/// dropped rather than defaulted, on the principle that an unjudged document should not be presented
/// as a judged one.
/// </para>
/// <para>
/// The comparison it supports is narrower than it first appears. The service chooses its own
/// retrieval depth and subquery plan, neither of which the caller controls, so this row answers
/// "what does the first-party answer produce" rather than "what does this fusion algorithm produce
/// under the same conditions as the others". That distinction belongs in any table it appears in.
/// </para>
/// </remarks>
public sealed class AgenticRetrievalFusion : IFusionStrategy
{
    private readonly KnowledgeBaseRetrievalClient _client;
    private readonly int _maxRuntimeSeconds;

    public AgenticRetrievalFusion(CrossIndexOptions options, int maxRuntimeSeconds = 30)
    {
        ArgumentNullException.ThrowIfNull(options);

        TokenCredential credential = new DefaultAzureCredential();

        _client = new KnowledgeBaseRetrievalClient(
            new Uri(options.Search.Endpoint),
            options.Search.KnowledgeBaseName,
            credential);

        _maxRuntimeSeconds = maxRuntimeSeconds;
        KnowledgeSourceNames = [.. options.Search.StripeIndexes.Select(Indexing.KnowledgeBaseProvisioner.SourceNameFor)];
    }

    private IReadOnlyList<string> KnowledgeSourceNames { get; }

    public string Name => "agentic-retrieval";

    public string Description =>
        "The service retrieves from every stripe and returns one ranked list. No client-side merge.";

    public bool Supports(RetrievalMode mode) => true;

    /// <summary>Ranks by reranker score, so it belongs only in a reranked comparison.</summary>
    public bool RequiresSemanticRanker => true;

    /// <summary>
    /// Declares that this strategy retrieves for itself, so the harness must not charge it for the
    /// fan-out it was handed and did not use.
    /// </summary>
    public bool PerformsOwnRetrieval => true;

    public async ValueTask<IReadOnlyList<FusedDocument>> FuseAsync(
        FanOutResult fanOut,
        FusionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fanOut);
        ArgumentNullException.ThrowIfNull(context);

        var request = new KnowledgeBaseRetrievalRequest
        {
            MaxRuntimeInSeconds = _maxRuntimeSeconds,
            IncludeActivity = false,
        };

        request.Intents.Add(new KnowledgeRetrievalSemanticIntent(fanOut.Query));

        foreach (string sourceName in KnowledgeSourceNames)
        {
            request.KnowledgeSourceParams.Add(new SearchIndexKnowledgeSourceParams(sourceName)
            {
                IncludeReferences = true,
                IncludeReferenceSourceData = true,
            });
        }

        using ComputeUnitScope scope = ComputeUnitScope.Begin("agentic-retrieval");

        Response<KnowledgeBaseRetrievalResponse> response = await _client
            .RetrieveAsync(request, cancellationToken)
            .ConfigureAwait(false);

        List<FusedDocument> scored = [];
        int rank = 0;

        foreach (KnowledgeBaseReference reference in response.Value.References)
        {
            if (reference is not KnowledgeBaseSearchIndexReference indexReference)
            {
                continue;
            }

            if (indexReference.RerankerScore is not { } rerankerScore)
            {
                continue;
            }

            rank++;

            scored.Add(new FusedDocument(
                ToScoredDocument(indexReference, rank, rerankerScore),
                rerankerScore,
                $"agentic reranker={rerankerScore:F3} from source {indexReference.ActivitySource}"));
        }

        return FusionHelpers.RankAndTruncate(scored, context.TopK);
    }

    /// <summary>
    /// Rebuilds enough of a document from the reference's source data to be comparable with the
    /// other strategies' output.
    /// </summary>
    /// <remarks>
    /// Only the key is strictly required — every metric is computed over document ids — but
    /// carrying the title and blurb through keeps the explain output and the judgment pool as
    /// legible for this strategy as for the rest.
    /// </remarks>
    private static ScoredDocument ToScoredDocument(
        KnowledgeBaseSearchIndexReference reference,
        int rank,
        double rerankerScore)
    {
        string Field(string name)
        {
            if (reference.SourceData is null
                || !reference.SourceData.TryGetValue(name, out BinaryData? value)
                || value is null)
            {
                return string.Empty;
            }

            // Source data arrives as raw JSON, so a string field is a quoted JSON string rather
            // than bare text. Parsing it is what keeps titles from carrying their own quotes.
            try
            {
                return value.ToObjectFromJson<string>() ?? string.Empty;
            }
            catch (JsonException)
            {
                return value.ToString();
            }
        }

        string id = reference.DocKey ?? Field("id");

        var document = new BookDocument
        {
            Id = id,
            Title = Field("title"),
            Blurb = Field("blurb"),
        };

        return new ScoredDocument(
            document,
            SourceIndex: $"knowledge-source-{reference.ActivitySource}",
            Rank: rank,
            Score: rerankerScore,
            RerankerScore: rerankerScore);
    }
}
