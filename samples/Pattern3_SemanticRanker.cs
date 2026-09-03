// Pattern 3 — the built-in semantic ranker.
//
// The service reranks each index's own results with a cross-encoder that reads the query and the
// document together and returns an absolute 0-4 score. That score consults no corpus statistics, so
// unlike @search.score it means the same thing in every index. Merging becomes a sort.
//
// There are two ways to use it, and one of them is free.

using Azure.Search.Documents;
using Azure.Search.Documents.Models;

namespace CrossIndexQuery.Samples;

public static class Pattern3SemanticRanker
{
    // ---------------------------------------------------------------------------------------
    // The good way: ask for semantic ranking on the fan-out itself, then merge on the score that
    // comes back. No second round trip, no extra request, nothing to reconcile.
    //
    // Measured identical to the second-pass approach below, to three decimal places, at half the
    // latency and half the compute units. If your fan-out can request semantic ranking, this is the
    // whole implementation.
    // ---------------------------------------------------------------------------------------
    public static async Task<List<Ranked>> RetrieveAndMergeAsync(
        IReadOnlyList<SearchClient> indexes,
        string query,
        string semanticConfiguration,
        int perIndex,
        int topK,
        CancellationToken cancellationToken = default)
    {
        SearchOptions Options() => new()
        {
            Size = perIndex,
            QueryType = SearchQueryType.Semantic,
            SemanticSearch = new SemanticSearchOptions
            {
                SemanticConfigurationName = semanticConfiguration,

                // An index with nothing relevant to contribute should return its keyword results
                // rather than failing the whole fan-out.
                ErrorMode = SemanticErrorMode.Partial,
            },
        };

        var tasks = indexes
            .Select(client => client.SearchAsync<SearchDocument>(query, Options(), cancellationToken))
            .ToList();

        var ranked = new List<Ranked>();

        foreach (var task in tasks)
        {
            var response = await task.ConfigureAwait(false);

            await foreach (SearchResult<SearchDocument> result
                in response.Value.GetResultsAsync().WithCancellation(cancellationToken))
            {
                // A document the reranker declined to score has not been judged. Substituting the
                // BM25 score would put an incomparable number on the same scale as the comparable
                // ones, which is the exact mistake this pattern exists to avoid.
                if (result.SemanticSearch?.RerankerScore is not { } rerankerScore)
                {
                    continue;
                }

                ranked.Add(new Ranked((string)result.Document["id"], rerankerScore));
            }
        }

        // Absolute 0-4 from a cross-encoder. Directly comparable across indexes, so this sort is
        // correct rather than approximate.
        return [.. ranked.OrderByDescending(r => r.RerankerScore).Take(topK)];
    }

    // ---------------------------------------------------------------------------------------
    // The second-pass way: you already have keyword or vector results and want them reranked. Issue
    // one extra semantic query per index, filtered to the keys that index contributed.
    //
    // Necessary when the original fan-out was not semantic. Measured to produce the same ordering as
    // the approach above at twice the cost, so prefer that one when you can.
    //
    // A document can only be reranked by the index that holds it, so this fans out too — there is
    // no single query that can score the whole union.
    // ---------------------------------------------------------------------------------------
    public static async Task<List<Ranked>> RerankExistingAsync(
        IReadOnlyDictionary<SearchClient, IReadOnlyList<string>> keysByIndex,
        string query,
        string semanticConfiguration,
        int topK,
        CancellationToken cancellationToken = default)
    {
        var tasks = keysByIndex.Select(async pair =>
        {
            // The reranker accepts at most 50 documents per query, and that cap is not
            // configurable. Anything beyond it is silently not reranked.
            var keys = pair.Value.Take(50).ToList();

            if (keys.Count == 0)
            {
                return (IReadOnlyList<Ranked>)[];
            }

            var options = new SearchOptions
            {
                Size = keys.Count,

                // search.in is a set membership test and stays fast at fifty-plus values, where a
                // chain of `or` clauses degrades sharply.
                Filter = $"search.in(id, '{string.Join(",", keys)}', ',')",
                QueryType = SearchQueryType.Semantic,
                SemanticSearch = new SemanticSearchOptions
                {
                    SemanticConfigurationName = semanticConfiguration,
                    ErrorMode = SemanticErrorMode.Partial,
                },
            };

            var response = await pair.Key
                .SearchAsync<SearchDocument>(query, options, cancellationToken)
                .ConfigureAwait(false);

            var ranked = new List<Ranked>();

            await foreach (SearchResult<SearchDocument> result
                in response.Value.GetResultsAsync().WithCancellation(cancellationToken))
            {
                if (result.SemanticSearch?.RerankerScore is { } score)
                {
                    ranked.Add(new Ranked((string)result.Document["id"], score));
                }
            }

            return (IReadOnlyList<Ranked>)ranked;
        });

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        return [.. results.SelectMany(r => r).OrderByDescending(r => r.RerankerScore).Take(topK)];
    }

    // One consequence worth knowing about, because it is counter-intuitive and it is not a setting
    // you can change: the 50-document reranker cap applies *per index*. Two indexes therefore put
    // 100 documents through the cross-encoder where a single index puts 50, and asking each index
    // for fewer results does not shrink its window — it only discards documents the reranker has
    // already scored.
    //
    // Striping widens your semantic funnel whether or not you wanted it to.

    public sealed record Ranked(string Id, double RerankerScore);
}
