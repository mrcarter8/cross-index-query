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
/// One structural consequence deserves attention, and it depends on which candidate budget the
/// harness is running. The reranker accepts at most 50 documents per query. This strategy names its
/// candidates explicitly through a key filter, so the number it sends is whatever
/// <c>maxCandidatesPerStripe</c> says: under the default <c>Equalized</c> budget that is 25 per
/// stripe, which puts the same 50 documents through the cross-encoder as a single index would.
/// Under <c>PerIndex</c> it is 50 per stripe, and two stripes then rerank 100 against the oracle's
/// 50 — which is the condition under which striping could surface a document a single index would
/// never have reranked at all.
/// </para>
/// <para>
/// That contrast does <em>not</em> apply to in-place semantic retrieval, where the service selects
/// its own candidates. Measured directly against the live service, the reranker window is exactly
/// 50 per index and is <em>not</em> controlled by the requested result count: a query asking for 25
/// results and one asking for 50 return an identical first 25. Asking a stripe for fewer results
/// therefore discards documents the reranker already scored rather than narrowing what it saw, so
/// the in-place path is structurally 2x50 regardless of budget. See <c>docs/decisions.md</c>.
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
/// corpus statistics, no normalization and no assumption about score comparability. Its one
/// requirement is that the query was issued with the semantic ranker enabled, and it declines to
/// guess when it was not.
/// </para>
/// <para>
/// It also inherits a property of in-place semantic retrieval that is easy to miss: the reranker
/// window is 50 documents <em>per index</em> and is not controlled by the requested result count, so
/// two stripes put 100 documents through the cross-encoder where a single index puts 50. That is a
/// structural consequence of splitting, not a setting — you cannot opt out of it from the client,
/// and it is measured rather than assumed. See <c>docs/decisions.md</c>.
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
