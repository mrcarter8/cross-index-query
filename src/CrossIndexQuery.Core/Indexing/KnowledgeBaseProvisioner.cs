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

        await client.CreateOrUpdateKnowledgeBaseAsync(
            knowledgeBase, onlyIfUnchanged: false, cancellationToken).ConfigureAwait(false);

        Console.WriteLine(
            $"  knowledge base ready: {options.Search.KnowledgeBaseName} "
            + $"({references.Count} sources)");
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
