// Pattern 4 — agentic retrieval.
//
// There is no merge step here at all. A knowledge base references both stripe indexes as separate
// sources, and the service plans the subqueries, retrieves from each source, and returns one ranked
// list. The collation problem the other three patterns solve does not arise, because collation
// happens service-side.
//
// What you give up is control. The service chooses its own retrieval depth and query plan; neither
// is a parameter. That makes this the least tunable option and the least directly comparable to the
// others, which is worth knowing before putting its number in the same table.

using Azure.Identity;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.KnowledgeBases;
using Azure.Search.Documents.KnowledgeBases.Models;

namespace CrossIndexQuery.Samples;

public static class Pattern4AgenticRetrieval
{
    // ---------------------------------------------------------------------------------------
    // Setup, once. One knowledge source per stripe index, then one knowledge base referencing them
    // all. A knowledge base can reference several sources, which is exactly the striped case.
    // ---------------------------------------------------------------------------------------
    public static async Task CreateKnowledgeBaseAsync(
        SearchIndexClient client,
        string knowledgeBaseName,
        IReadOnlyList<string> indexNames,
        string semanticConfiguration,
        IReadOnlyList<string> searchFields,
        IReadOnlyList<string> returnedFields,
        CancellationToken cancellationToken = default)
    {
        var references = new List<KnowledgeSourceReference>();

        foreach (var indexName in indexNames)
        {
            var sourceName = $"{indexName}-source";

            var parameters = new SearchIndexKnowledgeSourceParameters(indexName)
            {
                SemanticConfigurationName = semanticConfiguration,
            };

            foreach (var field in searchFields)
            {
                parameters.SearchFields.Add(new SearchIndexFieldReference(field));
            }

            // Fields returned on each reference. Include your document key — it is how you line
            // these results up with anything else.
            foreach (var field in returnedFields)
            {
                parameters.SourceDataFields.Add(new SearchIndexFieldReference(field));
            }

            await client.CreateOrUpdateKnowledgeSourceAsync(
                new SearchIndexKnowledgeSource(sourceName, parameters),
                onlyIfUnchanged: false,
                cancellationToken).ConfigureAwait(false);

            references.Add(new KnowledgeSourceReference(sourceName));
        }

        await client.CreateOrUpdateKnowledgeBaseAsync(
            new KnowledgeBase(knowledgeBaseName, references),
            onlyIfUnchanged: false,
            cancellationToken).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------------------------------
    // Query. One call, one ranked list, spanning every stripe.
    // ---------------------------------------------------------------------------------------
    public static async Task<List<Ranked>> RetrieveAsync(
        Uri searchEndpoint,
        string knowledgeBaseName,
        IReadOnlyList<string> knowledgeSourceNames,
        string query,
        int topK,
        int maxRuntimeSeconds = 30,
        CancellationToken cancellationToken = default)
    {
        var client = new KnowledgeBaseRetrievalClient(
            searchEndpoint, knowledgeBaseName, new DefaultAzureCredential());

        var request = new KnowledgeBaseRetrievalRequest
        {
            MaxRuntimeInSeconds = maxRuntimeSeconds,

            // Turn on to see the subquery plan the service chose. Useful while developing, and the
            // only visibility you get into how it decided to search.
            IncludeActivity = false,
        };

        request.Intents.Add(new KnowledgeRetrievalSemanticIntent(query));

        foreach (var sourceName in knowledgeSourceNames)
        {
            request.KnowledgeSourceParams.Add(new SearchIndexKnowledgeSourceParams(sourceName)
            {
                // Without this you get generated text and no document references, which is a
                // different feature. References are what make the result a ranked list.
                IncludeReferences = true,
                IncludeReferenceSourceData = true,
            });
        }

        var response = await client.RetrieveAsync(request, cancellationToken).ConfigureAwait(false);

        var ranked = new List<Ranked>();

        foreach (var reference in response.Value.References)
        {
            // A knowledge base can span blob, OneLake and web sources too, so narrow to the ones
            // that came from a search index.
            if (reference is not KnowledgeBaseSearchIndexReference indexReference)
            {
                continue;
            }

            if (indexReference.RerankerScore is not { } score)
            {
                continue;
            }

            ranked.Add(new Ranked(indexReference.DocKey, score, indexReference.ActivitySource));
        }

        // Already ordered by the service, but sorting explicitly makes the ordering contract
        // visible rather than assumed — and the reranker score is the same absolute 0-4 scale
        // pattern 3 uses, so it is comparable across sources.
        return [.. ranked.OrderByDescending(r => r.RerankerScore).Take(topK)];
    }

    /// <param name="ActivitySource">Which knowledge source produced this document.</param>
    public sealed record Ranked(string Id, double RerankerScore, int ActivitySource);
}
