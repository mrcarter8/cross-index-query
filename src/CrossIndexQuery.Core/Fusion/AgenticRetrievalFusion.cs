using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using CrossIndexQuery.Core.Configuration;
using CrossIndexQuery.Core.Models;
using CrossIndexQuery.Core.Retrieval;

namespace CrossIndexQuery.Core.Fusion;

/// <summary>
/// How the service should order the results it gathers from every stripe.
/// </summary>
/// <remarks>
/// This is the cost/quality dial, and it is the only thing that differs between the two agentic
/// rows in the results table.
/// </remarks>
public enum AgenticResultsProcessing
{
    /// <summary>
    /// Run the semantic ranker over every candidate and order by its score.
    /// </summary>
    /// <remarks>
    /// The default, and what makes cross-index collation work here. The semantic ranker is a
    /// cross-encoder over (query, document): its score is a property of that pair and consults no
    /// corpus statistics, so a 2.4 from one stripe means what a 2.4 from the other means. That is
    /// the same reason merging on <c>@search.rerankerScore</c> works client-side.
    /// </remarks>
    Rerank,

    /// <summary>
    /// Skip reranking entirely.
    /// </summary>
    /// <remarks>
    /// Costs nothing in model tokens, and the references come back without a
    /// <c>rerankerScore</c> at all. With no comparable score to sort by, the service falls back to
    /// distributing results across sources in round-robin order — which is interleaving, measured
    /// in this study as the worst merge strategy available. The cheap mode buys the merge the rest
    /// of this report recommends against.
    /// </remarks>
    None,
}

/// <summary>
/// Delegates both retrieval and collation to the service, over a knowledge base spanning every
/// stripe.
/// </summary>
/// <remarks>
/// <para>
/// Every other strategy in this catalog is handed a fan-out and decides how to merge it. This one
/// is not merging anything: it issues its own retrieval against a knowledge base that references
/// both stripe indexes, and the service retrieves from each source and returns one ranked list. The
/// client-side collation problem does not arise, because there is no client-side collation.
/// </para>
/// <para>
/// <b>What this is not.</b> Despite the name, no large language model ranks anything here. With the
/// minimal reasoning effort this sample uses — which is forced, because the knowledge base has no
/// model attached — the documentation is explicit that "there's no LLM for intelligent query
/// planning or answer synthesis". The query goes straight to the retrieval engine, and ordering
/// comes from the semantic ranker named by the index's own semantic configuration. That is
/// observable in the response: the source activity reports <c>semanticConfigurationName</c>,
/// references carry <c>rerankerScore</c>, and no <c>modelQueryPlanning</c> or
/// <c>modelAnswerSynthesis</c> activity is ever emitted.
/// </para>
/// <para>
/// It therefore measures as a peer of the semantic strategies rather than as something categorically
/// smarter, and the numbers bear that out. The genuinely agentic capability — an LLM decomposing one
/// query into several subqueries — requires attaching a model to the knowledge base and is not
/// exercised here.
/// </para>
/// <para>
/// <b>Why this calls REST directly.</b> The GA SDK surface exposes neither
/// <c>resultsProcessing</c> nor <c>maxOutputDocuments</c>; both are preview-only. Since the whole
/// point of this strategy pair is to measure the difference those knobs make, the request is built
/// by hand against the preview API rather than pretending the dial does not exist.
/// </para>
/// </remarks>
public sealed class AgenticRetrievalFusion : IFusionStrategy, IDisposable
{
    /// <summary>
    /// API version carrying the knobs this strategy exists to measure.
    /// </summary>
    /// <remarks>
    /// The GA version, <c>2026-04-01</c>, rejects <c>maxOutputDocuments</c> and
    /// <c>resultsProcessing</c> outright and caps the response at 25 references. Anyone pinned to GA
    /// gets the reranked behaviour and that cap, with no way to ask for anything else.
    /// </remarks>
    public const string PreviewApiVersion = "2026-08-01-preview";

    /// <summary>
    /// Largest value the service accepts for <c>maxOutputDocuments</c>.
    /// </summary>
    public const int MaxSupportedOutputDocuments = 200;

    /// <summary>
    /// Smallest value the service accepts for <c>maxOutputDocuments</c>.
    /// </summary>
    /// <remarks>
    /// The reason this strategy cannot be held to the same candidate budget as the rest of the
    /// catalog. Every other striped arm is capped at 25 per stripe so its total matches the single
    /// index's 50; the service rejects anything below 50 here, so agentic retrieval necessarily
    /// sees twice the candidates. That is a property of the feature, not a choice, and any table
    /// containing these rows has to say so.
    /// </remarks>
    public const int MinimumOutputDocuments = 50;

    private static readonly string[] Scope = ["https://search.azure.com/.default"];

    private readonly HttpClient _http = new();
    private readonly TokenCredential _credential = new DefaultAzureCredential();
    private readonly Uri _endpoint;
    private readonly string _knowledgeBase;
    private readonly int _maxRuntimeSeconds;
    private readonly int _maxOutputDocuments;
    private readonly int _maxOutputSize;
    private readonly AgenticResultsProcessing _processing;

    public AgenticRetrievalFusion(
        CrossIndexOptions options,
        AgenticResultsProcessing processing = AgenticResultsProcessing.Rerank,
        int maxOutputDocuments = 50,
        int maxRuntimeSeconds = 30)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (maxOutputDocuments is < MinimumOutputDocuments or > MaxSupportedOutputDocuments)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxOutputDocuments),
                maxOutputDocuments,
                $"The service accepts {MinimumOutputDocuments} to {MaxSupportedOutputDocuments} "
                + "output documents.");
        }

        _endpoint = new Uri(options.Search.Endpoint);
        _knowledgeBase = options.Search.KnowledgeBaseName;
        _processing = processing;
        _maxRuntimeSeconds = maxRuntimeSeconds;
        _maxOutputDocuments = maxOutputDocuments;

        // Two independent caps govern the response, and only one of them is the one the caller set.
        // Asking for 200 documents without also raising the size budget silently returns about 49:
        // the request succeeds, no warning is emitted, and the shortfall is indistinguishable from
        // there being no more matching documents. Budgeting generously keeps maxOutputDocuments the
        // binding constraint.
        _maxOutputSize = Math.Max(200_000, maxOutputDocuments * 2_000);

        KnowledgeSourceNames =
            [.. options.Search.StripeIndexes.Select(Indexing.KnowledgeBaseProvisioner.SourceNameFor)];
    }

    private IReadOnlyList<string> KnowledgeSourceNames { get; }

    public string Name => _processing == AgenticResultsProcessing.Rerank
        ? "agentic-rerank"
        : "agentic-cheap";

    public string Description => _processing == AgenticResultsProcessing.Rerank
        ? "Service retrieves from every stripe and orders by semantic reranker score."
        : "Service retrieves from every stripe and interleaves round-robin. No reranking, no tokens.";

    public bool Supports(RetrievalMode mode) => true;

    /// <summary>
    /// Both variants belong in the reranked comparison.
    /// </summary>
    /// <remarks>
    /// True even for the cheap variant, which does no reranking itself. The point of that row is to
    /// show what declining to rerank costs, and that is only legible next to the arms that did;
    /// scoring it against an un-reranked baseline would compare it to the wrong thing.
    /// </remarks>
    public bool RequiresSemanticRanker => true;

    /// <summary>
    /// Declares that this strategy retrieves for itself, so the harness must not charge it for the
    /// fan-out it was handed and did not use.
    /// </summary>
    public bool PerformsOwnRetrieval => true;

    /// <summary>
    /// Searches the service issued internally on the most recent call, one per knowledge source.
    /// </summary>
    /// <remarks>
    /// Read from the activity array rather than assumed. Without it the harness reports zero queries
    /// for this strategy — not because it is free, but because the work happens server-side where
    /// the client's own request counter cannot see it, and a zero in a cost column reads as free.
    /// </remarks>
    public int LastSearchCount { get; private set; }

    /// <summary>
    /// Model tokens the service consumed on the most recent call.
    /// </summary>
    /// <remarks>
    /// The real price of the reranked variant, and it is not small: roughly 18,600 tokens for 49
    /// documents. Billed on a different meter from search compute units, so it belongs in its own
    /// column rather than folded into one.
    /// </remarks>
    public int LastReasoningTokens { get; private set; }

    public async ValueTask<IReadOnlyList<FusedDocument>> FuseAsync(
        FanOutResult fanOut,
        FusionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fanOut);
        ArgumentNullException.ThrowIfNull(context);

        using JsonDocument response = await RetrieveAsync(fanOut.Query, cancellationToken)
            .ConfigureAwait(false);

        JsonElement root = response.RootElement;
        RecordActivity(root);

        return Rank(root, context.TopK);
    }

    private async Task<JsonDocument> RetrieveAsync(string query, CancellationToken cancellationToken)
    {
        AccessToken token = await _credential
            .GetTokenAsync(new TokenRequestContext(Scope), cancellationToken)
            .ConfigureAwait(false);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{_endpoint.AbsoluteUri.TrimEnd('/')}/knowledgeBases/{_knowledgeBase}/retrieve"
                + $"?api-version={PreviewApiVersion}")
        {
            Content = new StringContent(BuildBody(query), Encoding.UTF8, "application/json"),
        };

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        using HttpResponseMessage message = await _http
            .SendAsync(request, cancellationToken).ConfigureAwait(false);

        string payload = await message.Content.ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!message.IsSuccessStatusCode)
        {
            // Deliberately not InvalidOperationException. The harness treats that type as a
            // strategy declaring its own precondition unmet and skips the row silently, so using it
            // here would turn every real API failure into a missing row with no error anywhere.
            throw new HttpRequestException(
                $"Agentic retrieval failed ({(int)message.StatusCode}). {Summarize(payload)}",
                inner: null,
                statusCode: message.StatusCode);
        }

        return JsonDocument.Parse(payload);
    }

    private string BuildBody(string query)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();

            // Minimal effort is not a tuning choice: any higher effort requires a model attached to
            // the knowledge base, and without one the service rejects the request outright.
            writer.WriteString("outputMode", "extractiveData");
            writer.WriteStartObject("retrievalReasoningEffort");
            writer.WriteString("kind", "minimal");
            writer.WriteEndObject();

            writer.WriteStartArray("intents");
            writer.WriteStartObject();
            writer.WriteString("type", "semantic");
            writer.WriteString("search", query);
            writer.WriteEndObject();
            writer.WriteEndArray();

            writer.WriteBoolean("includeActivity", true);
            writer.WriteNumber("maxRuntimeInSeconds", _maxRuntimeSeconds);
            writer.WriteNumber("maxOutputDocuments", _maxOutputDocuments);
            writer.WriteNumber("maxOutputSize", _maxOutputSize);

            writer.WriteStartArray("knowledgeSourceParams");
            foreach (string source in KnowledgeSourceNames)
            {
                writer.WriteStartObject();
                writer.WriteString("knowledgeSourceName", source);
                writer.WriteString("kind", "searchIndex");
                writer.WriteBoolean("includeReferences", true);
                writer.WriteBoolean("includeReferenceSourceData", true);

                // Forced on, so a stripe holding nothing obviously relevant is still queried.
                // Without it the service may skip a source, and a row that silently stopped
                // consulting one of the two indexes is not measuring cross-index retrieval at all.
                writer.WriteBoolean("alwaysQuerySource", true);

                if (_processing == AgenticResultsProcessing.None)
                {
                    writer.WriteString("resultsProcessing", "none");
                }

                // Each source has to retrieve at least as deep as the caller wants back, or the
                // request-level budget can never be met.
                writer.WriteNumber("maxOutputDocuments", _maxOutputDocuments);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// Reads the real cost of the call out of the activity array.
    /// </summary>
    private void RecordActivity(JsonElement root)
    {
        LastSearchCount = 0;
        LastReasoningTokens = 0;

        if (!root.TryGetProperty("activity", out JsonElement activity)
            || activity.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement record in activity.EnumerateArray())
        {
            if (!record.TryGetProperty("type", out JsonElement type))
            {
                continue;
            }

            switch (type.GetString())
            {
                case "searchIndex":
                    LastSearchCount++;
                    break;

                // Present in both modes, but carries a token count only when reranking ran.
                case "agenticReasoning":
                    if (record.TryGetProperty("reasoningTokens", out JsonElement tokens)
                        && tokens.ValueKind == JsonValueKind.Number)
                    {
                        LastReasoningTokens += tokens.GetInt32();
                    }

                    break;

                default:
                    break;
            }
        }
    }

    /// <summary>
    /// Turns the references array into ranked documents.
    /// </summary>
    /// <remarks>
    /// The two modes need different handling and the difference is the whole point. Reranked
    /// references carry a comparable score and are sorted by it. Unreranked references carry no
    /// score at all, so the only signal available is the order the service returned them in — its
    /// round-robin interleave. Inventing a score for them, or dropping them for lacking one, would
    /// each misrepresent what the cheap mode actually does.
    /// </remarks>
    private static List<FusedDocument> Rank(JsonElement root, int topK)
    {
        if (!root.TryGetProperty("references", out JsonElement references)
            || references.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        List<FusedDocument> ranked = [];
        int position = 0;

        foreach (JsonElement reference in references.EnumerateArray())
        {
            string? key = Text(reference, "docKey");
            if (string.IsNullOrEmpty(key))
            {
                continue;
            }

            position++;

            bool hasScore = reference.TryGetProperty("rerankerScore", out JsonElement scoreElement)
                && scoreElement.ValueKind == JsonValueKind.Number;

            // Descending with position, so the service's own ordering survives the sort below.
            double score = hasScore ? scoreElement.GetDouble() : -position;

            string source = reference.TryGetProperty("activitySource", out JsonElement activitySource)
                && activitySource.ValueKind == JsonValueKind.Number
                ? $"source-{activitySource.GetInt32()}"
                : "agentic";

            var document = new BookDocument
            {
                Id = key,
                Title = Text(reference, "title") ?? string.Empty,
                Blurb = SourceField(reference, "blurb"),
                Authors = [],
            };

            ranked.Add(new FusedDocument(
                new ScoredDocument(document, source, position, score),
                score,
                hasScore
                    ? $"agentic reranker={score:F3} from {source}"
                    : $"agentic round-robin position {position} from {source}"));
        }

        return [.. ranked.OrderByDescending(d => d.FusedScore).Take(topK)];
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string SourceField(JsonElement reference, string name)
    {
        if (!reference.TryGetProperty("sourceData", out JsonElement sourceData)
            || sourceData.ValueKind != JsonValueKind.Object
            || !sourceData.TryGetProperty(name, out JsonElement field))
        {
            return string.Empty;
        }

        return field.ValueKind == JsonValueKind.String ? field.GetString() ?? string.Empty : string.Empty;
    }

    private static string Summarize(string payload)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            if (document.RootElement.TryGetProperty("error", out JsonElement error)
                && error.TryGetProperty("message", out JsonElement message))
            {
                return message.GetString() ?? payload;
            }
        }
        catch (JsonException)
        {
            // Not JSON; the raw body is the most useful thing to report.
        }

        return payload.Length > 400 ? payload[..400] : payload;
    }

    public void Dispose() => _http.Dispose();
}
