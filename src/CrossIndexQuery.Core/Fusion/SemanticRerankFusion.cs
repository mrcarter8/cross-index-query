using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using CrossIndexQuery.Core.Clients;
using CrossIndexQuery.Core.Indexing;
using CrossIndexQuery.Core.Models;
using CrossIndexQuery.Core.Retrieval;
using CrossIndexQuery.Core.Telemetry;

namespace CrossIndexQuery.Core.Fusion;

/// <summary>
/// Re-scores the candidates with the semantic reranker and merges on the resulting score.
/// </summary>
/// <remarks>
/// <para>
/// The reranker is a cross-encoder: it reads the query and a document together and returns an
/// absolute relevance score between 0 and 4. It consults no corpus statistics whatsoever, which
/// makes its output the only score in Azure AI Search that is comparable across indexes by
/// construction rather than by argument. Sorting the union by reranker score does not work around
/// the comparability problem — it removes it.
/// </para>
/// <para>
/// Each stripe reranks its own candidates. That is a requirement, not a choice: a document can only
/// be reranked by the index that holds it, so no single query can score the whole union. The rerank
/// therefore fans out exactly as the retrieval did, one query per stripe, filtered to the keys that
/// stripe contributed.
/// </para>
/// <para>
/// This is the most expensive strategy in the catalog — a second round trip per stripe, plus
/// semantic ranker units, which bill on a separate meter from compute units. The harness measures
/// both so the trade is explicit rather than assumed.
/// </para>
/// <para>
/// One structural consequence deserves attention. The reranker accepts at most 50 documents per
/// query. A single index therefore reranks 50 candidates, while two stripes reranked in place put
/// 100 documents through the cross-encoder. Striping can consequently surface a document that a
/// single index would never have reranked at all, which means the single-index oracle is not
/// automatically an upper bound under semantic ranking. That is a measurable claim, and it is left
/// to the harness to confirm or refute on real data rather than asserted here.
/// </para>
/// </remarks>
public sealed class SemanticRerankFusion(
    SearchClientFactory factory,
    int maxCandidatesPerStripe = 50) : IFusionStrategy
{
    public string Name => "semantic-rerank";

    public string Description =>
        "Re-score each stripe's candidates with the semantic reranker, then merge on the 0-4 score.";

    public bool Supports(RetrievalMode mode) => true;

    public bool RequiresSemanticRanker => true;

    public async ValueTask<IReadOnlyList<FusedDocument>> FuseAsync(
        FanOutResult fanOut,
        FusionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fanOut);
        ArgumentNullException.ThrowIfNull(context);

        IReadOnlyList<FusedDocument>[] perStripe = await Task.WhenAll(
            fanOut.Stripes.Select(stripe => RerankStripeAsync(fanOut.Query, stripe, cancellationToken)))
            .ConfigureAwait(false);

        return FusionHelpers.RankAndTruncate(perStripe.SelectMany(x => x), context.TopK);
    }

    private async Task<IReadOnlyList<FusedDocument>> RerankStripeAsync(
        string query,
        StripeResultSet stripe,
        CancellationToken cancellationToken)
    {
        // The stripe's own ranking decides which candidates get the chance to be judged when it
        // returned more than the reranker will accept. It is the only ordering available at this
        // point that reflects how relevant this index believed each document to be.
        Dictionary<string, ScoredDocument> byId = new(StringComparer.Ordinal);
        foreach (ScoredDocument doc in stripe.Documents.Take(maxCandidatesPerStripe))
        {
            byId[doc.Id] = doc;
        }

        if (byId.Count == 0)
        {
            return [];
        }

        var options = new SearchOptions
        {
            Size = byId.Count,
            Filter = BuildKeyFilter(byId.Keys),
            QueryType = SearchQueryType.Semantic,
            SemanticSearch = new SemanticSearchOptions
            {
                SemanticConfigurationName = BookIndexSchema.SemanticConfigurationName,
                ErrorMode = SemanticErrorMode.Partial,
            },
        };

        foreach (string field in BookIndexSchema.DefaultSelect)
        {
            options.Select.Add(field);
        }

        using ComputeUnitScope scope = ComputeUnitScope.Begin($"semantic-rerank:{stripe.IndexName}");

        SearchClient client = factory.GetSearchClient(stripe.IndexName);
        SearchResults<BookDocument> results = await client
            .SearchAsync<BookDocument>(query, options, cancellationToken)
            .ConfigureAwait(false);

        List<FusedDocument> scored = [];

        await foreach (SearchResult<BookDocument> hit in results.GetResultsAsync()
            .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            // A stripe with nothing relevant to say can be refused a reranker score under partial
            // error mode. Dropping those documents is correct: they have not been judged, and
            // inventing a score would be worse than leaving them out.
            if (hit.SemanticSearch?.RerankerScore is not { } reranker)
            {
                continue;
            }

            ScoredDocument source = byId.TryGetValue(hit.Document.Id, out ScoredDocument? original)
                ? original with { RerankerScore = reranker }
                : new ScoredDocument(hit.Document, stripe.IndexName, 0, hit.Score ?? 0, RerankerScore: reranker);

            scored.Add(new FusedDocument(
                source,
                reranker,
                $"reranker={reranker:F3} (0-4) over {byId.Count} candidates from {stripe.IndexName}"));
        }

        return scored;
    }

    /// <summary>
    /// Builds a <c>search.in</c> filter over document keys.
    /// </summary>
    /// <remarks>
    /// <c>search.in</c> is used rather than a chain of <c>or</c> clauses because it is evaluated as
    /// a set membership test and stays fast at fifty-plus values, where the disjunction form
    /// degrades sharply.
    /// </remarks>
    private static string BuildKeyFilter(IEnumerable<string> keys) =>
        $"search.in(id, '{string.Join(",", keys)}', ',')";
}

/// <summary>
/// Merges on the reranker score already present on the results, without querying again.
/// </summary>
/// <remarks>
/// <para>
/// When the fan-out itself requested semantic ranking, every stripe has already reranked its own
/// results in place and each document carries an absolute 0-4 score. Merging is then a sort, and
/// costs nothing beyond the retrieval that had to happen anyway.
/// </para>
/// <para>
/// This is the strategy to reach for first. It is cheaper than reranking as a second pass, needs no
/// corpus statistics, no normalization and no assumption about score comparability, and it produces
/// the same 2x50 candidate depth described on <see cref="SemanticRerankFusion"/>. Its one
/// requirement is that the query was issued with the semantic ranker enabled, and it declines to
/// guess when it was not.
/// </para>
/// </remarks>
public sealed class SemanticScoreFusion : IFusionStrategy
{
    public string Name => "semantic-score";

    public string Description =>
        "Sort by the reranker score the stripes already returned. Cross-index safe, no extra query.";

    public bool Supports(RetrievalMode mode) => true;

    public bool RequiresSemanticRanker => true;

    public ValueTask<IReadOnlyList<FusedDocument>> FuseAsync(
        FanOutResult fanOut,
        FusionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fanOut);
        ArgumentNullException.ThrowIfNull(context);

        List<FusedDocument> scored = [];

        foreach (ScoredDocument doc in fanOut.AllDocuments)
        {
            if (doc.RerankerScore is not { } reranker)
            {
                continue;
            }

            scored.Add(new FusedDocument(
                doc, reranker, $"reranker={reranker:F3} (0-4), from {doc.SourceIndex}"));
        }

        if (scored.Count == 0)
        {
            throw new InvalidOperationException(
                $"'{Name}' requires the fan-out to have been issued with the semantic ranker enabled, "
                + "but no result carried a reranker score.");
        }

        return ValueTask.FromResult(FusionHelpers.RankAndTruncate(scored, context.TopK));
    }
}
