using Azure;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using CrossIndexQuery.Core.Clients;
using CrossIndexQuery.Core.Configuration;

namespace CrossIndexQuery.Core.Indexing;

/// <summary>
/// Creates the knowledge base that agentic retrieval queries, with one knowledge source per stripe.
/// </summary>
/// <remarks>
/// <para>
/// A knowledge base can reference several sources, which is exactly the striped case: the two
/// indexes are registered as separate sources of one logical corpus, and the service is left to
/// plan retrieval across both. That is the whole appeal of the pattern here — the collation problem
/// this sample spends ten strategies solving is handed to the service instead.
/// </para>
/// <para>
/// What the caller gives up is visibility. The service decides how many subqueries to issue, how
/// deep to retrieve in each source, and how to merge; none of those are parameters. The results are
/// therefore not a controlled comparison against the client-side strategies in the way those are
/// against each other, and the report has to say so rather than quietly listing the row alongside
/// them.
/// </para>
/// </remarks>
public sealed class KnowledgeBaseProvisioner(SearchClientFactory factory, CrossIndexOptions options)
{
    /// <summary>Knowledge source name for one stripe index.</summary>
    public static string SourceNameFor(string indexName) => $"{indexName}-source";

    public async Task CreateAsync(CancellationToken cancellationToken = default)
    {
        SearchIndexClient client = factory.CreateIndexClient();
        List<KnowledgeSourceReference> references = [];

        foreach (string indexName in options.Search.StripeIndexes)
        {
            string sourceName = SourceNameFor(indexName);

            var parameters = new SearchIndexKnowledgeSourceParameters(indexName)
            {
                SemanticConfigurationName = BookIndexSchema.SemanticConfigurationName,
            };

            foreach (string field in BookIndexSchema.TextSearchFields)
            {
                parameters.SearchFields.Add(new SearchIndexFieldReference(field));
            }

            // The fields that come back on a reference. The document key is what the harness needs
            // to line agentic results up against every other strategy's output; the rest is what
            // makes the response readable when demonstrating the feature.
            foreach (string field in new[] { "id", "title", "authors", "blurb" })
            {
                parameters.SourceDataFields.Add(new SearchIndexFieldReference(field));
            }

            var source = new SearchIndexKnowledgeSource(sourceName, parameters)
            {
                Description = $"Stripe index {indexName}, one partition of the book corpus.",
            };

            await client.CreateOrUpdateKnowledgeSourceAsync(
                source, onlyIfUnchanged: false, cancellationToken).ConfigureAwait(false);

            Console.WriteLine($"  knowledge source ready: {sourceName}");
            references.Add(new KnowledgeSourceReference(sourceName));
        }

        var knowledgeBase = new KnowledgeBase(options.Search.KnowledgeBaseName, references)
        {
            Description = "The whole book corpus, spanning every stripe index.",
        };

        // Attaching a model is what unlocks `low` and `medium` reasoning effort, and with them the
        // query planning that decomposes one query into subqueries. Without it the service is
        // pinned to `minimal`, where no LLM participates at all and ordering comes from the
        // semantic ranker. The sample is fully functional either way, so this stays optional.
        SearchServiceOptions search = options.Search;
        if (search.HasKnowledgeBaseModel)
        {
            string endpoint = string.IsNullOrWhiteSpace(search.KnowledgeBaseModelEndpoint)
                ? options.Embedding.Endpoint
                : search.KnowledgeBaseModelEndpoint;

            if (string.IsNullOrWhiteSpace(endpoint))
            {
                throw new InvalidOperationException(
                    "A knowledge base model deployment was configured but no endpoint could be "
                    + "resolved. Set Search:KnowledgeBaseModelEndpoint or Embedding:Endpoint.");
            }

            var parameters = new AzureOpenAIVectorizerParameters
            {
                ResourceUri = new Uri(endpoint),
                DeploymentName = search.KnowledgeBaseModelDeployment,

                // The service validates the model against its supported list, not the deployment
                // name. They are usually the same string, which is why one can stand in for the
                // other, but a deployment named for its purpose rather than its model would fail
                // validation with a confusing message if this were left to default.
                ModelName = string.IsNullOrWhiteSpace(search.KnowledgeBaseModelName)
                    ? search.KnowledgeBaseModelDeployment
                    : search.KnowledgeBaseModelName,
            };

            if (!string.IsNullOrWhiteSpace(search.KnowledgeBaseModelApiKey))
            {
                parameters.ApiKey = search.KnowledgeBaseModelApiKey;
            }

            knowledgeBase.Models.Add(new KnowledgeBaseAzureOpenAIModel(parameters));

            Console.WriteLine(
                $"  query planning model: {search.KnowledgeBaseModelDeployment} "
                + $"({(string.IsNullOrWhiteSpace(search.KnowledgeBaseModelApiKey)
                    ? "managed identity"
                    : "api key")})");
        }

        await client.CreateOrUpdateKnowledgeBaseAsync(
            knowledgeBase, onlyIfUnchanged: false, cancellationToken).ConfigureAwait(false);

        Console.WriteLine(
            $"  knowledge base ready: {options.Search.KnowledgeBaseName} "
            + $"({references.Count} sources)");

        if (!search.HasKnowledgeBaseModel)
        {
            // Stated rather than left silent, because the difference is invisible until a request
            // asking for anything above minimal effort is rejected.
            Console.WriteLine(
                "  no query planning model configured — agentic retrieval will run at minimal "
                + "reasoning effort, with no LLM in the loop.");
        }
    }

    /// <summary>Reports whether the knowledge base exists, without enumerating.</summary>
    public async Task<bool> ExistsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            SearchIndexClient client = factory.CreateIndexClient();
            await client.GetKnowledgeBaseAsync(options.Search.KnowledgeBaseName, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return false;
        }
    }
}
