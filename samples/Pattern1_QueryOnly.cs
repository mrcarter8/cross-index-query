// Pattern 1 — query only.
//
// Two indexes, no reranker, no extra service calls, no AI. Everything here is arithmetic over the
// scores the two queries already returned, plus one precomputed file of corpus statistics.
//
// This is the pattern to reach for first, because it costs nothing at query time and recovers
// essentially all of the relevance that naive merging loses.

using Azure.Search.Documents;
using Azure.Search.Documents.Models;

namespace CrossIndexQuery.Samples;

public static class Pattern1QueryOnly
{
    // ---------------------------------------------------------------------------------------
    // Step 1 — fan out. Concurrently, so two indexes cost the latency of the slower one rather
    // than the sum. This part is identical for all four patterns.
    // ---------------------------------------------------------------------------------------
    public static async Task<List<Hit>> FanOutAsync(
        IReadOnlyList<SearchClient> indexes,
        string query,
        int perIndex,
        CancellationToken cancellationToken = default)
    {
        SearchOptions Options() => new()
        {
            Size = perIndex,

            // Ask each index to score against the whole index rather than per shard. Without this
            // the same query re-run against the same index can return slightly different scores,
            // which is easy to mistake for a cross-index effect.
            ScoringStatistics = ScoringStatistics.Global,
        };

        var tasks = indexes
            .Select(client => client.SearchAsync<SearchDocument>(query, Options(), cancellationToken))
            .ToList();

        var hits = new List<Hit>();

        foreach (var task in tasks)
        {
            var response = await task.ConfigureAwait(false);
            var indexName = response.Value.GetType().Name;
            var rank = 0;

            await foreach (SearchResult<SearchDocument> result
                in response.Value.GetResultsAsync().WithCancellation(cancellationToken))
            {
                hits.Add(new Hit(
                    Id: (string)result.Document["id"],
                    SourceIndex: indexName,
                    Rank: ++rank,
                    Score: result.Score ?? 0));
            }
        }

        return hits;
    }

    // ---------------------------------------------------------------------------------------
    // The wrong way. Included because it is what almost everyone writes first, and because the
    // report measures precisely how much it costs.
    //
    // BM25 weights a term by how rare it is *in the index that computed the score*. Split a corpus
    // in two and each index has a different idea of what is rare, so the same document scores
    // differently depending only on where it lives. These numbers are not on a common scale, and
    // sorting by them is comparing measurements taken in different units.
    // ---------------------------------------------------------------------------------------
    public static List<Hit> MergeNaively(List<Hit> hits, int topK) =>
        hits.OrderByDescending(h => h.Score).Take(topK).ToList();

    // ---------------------------------------------------------------------------------------
    // Also wrong, and worse under size imbalance. Reciprocal rank fusion discards the scores and
    // keeps only positions, which does dodge the incomparable-scale problem — but rank 1 of a
    // 19-document index then ties rank 1 of a 9,981-document index. When one index is much smaller
    // than the other, this promotes its best non-answer on every query.
    //
    // Measured: up to 0.166 nDCG worse than a single index at 525:1, and it degrades smoothly as
    // imbalance grows. Use it only when your indexes are comparable in size.
    // ---------------------------------------------------------------------------------------
    public static List<Hit> MergeByRank(List<Hit> hits, int topK, int k = 60) =>
        hits.GroupBy(h => h.Id)
            .Select(g => g.OrderBy(h => h.Rank).First() with
            {
                Score = g.Sum(h => 1.0 / (k + h.Rank)),
            })
            .OrderByDescending(h => h.Score)
            .Take(topK)
            .ToList();

    // ---------------------------------------------------------------------------------------
    // Right way #1 — correct the scores you were given.
    //
    // For each query term, work out the inverse document frequency the whole corpus would have
    // produced and the one this index actually used, and scale the index's scores by the ratio. An
    // index that over-valued the query's terms is scaled down; one that under-valued them is scaled
    // up.
    //
    // Exact for a single-term query. For multi-term queries it corrects the systematic component of
    // the bias, which is the part that reorders results, because a search response does not expose
    // per-term contributions to decompose.
    // ---------------------------------------------------------------------------------------
    public static List<Hit> MergeWithIdfCorrection(
        List<Hit> hits,
        IReadOnlyList<string> queryTerms,
        CorpusStats stats,
        int topK)
    {
        var factors = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var indexName in hits.Select(h => h.SourceIndex).Distinct(StringComparer.Ordinal))
        {
            double weightedSum = 0;
            double weightTotal = 0;

            foreach (var term in queryTerms)
            {
                var localDf = stats.LocalDocumentFrequency(indexName, term);

                // A term this index has never seen tells you nothing about how it scored the query.
                // Including it would derive an enormous correction from a term that contributed
                // nothing to the score in the first place.
                if (localDf == 0)
                {
                    continue;
                }

                var globalIdf = Idf(stats.GlobalDocumentFrequency(term), stats.DocumentCount);
                var localIdf = Idf(localDf, stats.LocalDocumentCount(indexName));

                if (localIdf <= double.Epsilon)
                {
                    continue;
                }

                // Weighted by global IDF so that informative terms dominate. That is where the
                // disagreement between indexes actually lives.
                weightedSum += globalIdf / localIdf * globalIdf;
                weightTotal += globalIdf;
            }

            factors[indexName] = weightTotal > double.Epsilon ? weightedSum / weightTotal : 1.0;
        }

        return hits
            .Select(h => h with { Score = h.Score * factors[h.SourceIndex] })
            .OrderByDescending(h => h.Score)
            .Take(topK)
            .ToList();
    }

    // ---------------------------------------------------------------------------------------
    // Right way #2 — ignore the scores and recompute BM25 yourself.
    //
    // Rather than reconciling two incompatible measurements, compute the measurement a single index
    // would have produced. Every quantity comes from the whole corpus, so the result does not depend
    // on which index returned the document. Exact for multi-term queries, at the cost of needing the
    // document text and doing more arithmetic.
    // ---------------------------------------------------------------------------------------
    public static List<Hit> MergeWithGlobalBm25(
        List<Hit> hits,
        IReadOnlyDictionary<string, string> documentText,
        IReadOnlyList<string> queryTerms,
        CorpusStats stats,
        int topK,
        double k1 = 1.2,
        double b = 0.75)
    {
        return hits
            .Select(h =>
            {
                var tokens = Tokenize(documentText.GetValueOrDefault(h.Id, string.Empty));
                var length = tokens.Count;
                var frequencies = tokens
                    .GroupBy(t => t, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

                double score = 0;

                foreach (var term in queryTerms)
                {
                    if (!frequencies.TryGetValue(term, out var tf))
                    {
                        continue;
                    }

                    var idf = Idf(stats.GlobalDocumentFrequency(term), stats.DocumentCount);
                    var norm = 1 - b + (b * length / stats.AverageDocumentLength);
                    score += idf * (tf * (k1 + 1)) / (tf + (k1 * norm));
                }

                return h with { Score = score };
            })
            .OrderByDescending(h => h.Score)
            .Take(topK)
            .ToList();
    }

    // ---------------------------------------------------------------------------------------
    // Vector results need none of this. Cosine similarity is a property of two vectors and consults
    // no corpus statistics, so it means the same thing in every index. Sorting a merged vector
    // result set by raw score is correct — measured at Kendall tau = 1.000 against a single index,
    // which is exact rank agreement.
    //
    // If your queries are vector-only, striping costs you nothing and you can stop reading here.
    // ---------------------------------------------------------------------------------------
    public static List<Hit> MergeVectorResults(List<Hit> hits, int topK) =>
        hits.OrderByDescending(h => h.Score).Take(topK).ToList();

    /// <summary>Okapi BM25 inverse document frequency, in the form Azure AI Search uses.</summary>
    private static double Idf(int documentFrequency, int documentCount) =>
        Math.Log(1 + ((documentCount - documentFrequency + 0.5) / (documentFrequency + 0.5)));

    private static List<string> Tokenize(string text) =>
        [.. text.Split(
                (char[])[' ', '\t', '\n', '\r', '.', ',', ';', ':', '!', '?', '"', '\'', '(', ')', '-'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.ToLowerInvariant())
            .Where(t => t.Length > 1)];

    public sealed record Hit(string Id, string SourceIndex, int Rank, double Score);

    /// <summary>
    /// Statistics no single stripe can know, computed once offline over the whole corpus and
    /// shipped alongside the indexes. This is the only input pattern 1 needs that the search
    /// response does not already contain.
    /// </summary>
    public sealed class CorpusStats
    {
        public int DocumentCount { get; init; }

        public double AverageDocumentLength { get; init; }

        public Dictionary<string, int> DocumentFrequencies { get; init; } = new(StringComparer.Ordinal);

        public Dictionary<string, int> PerIndexDocumentCounts { get; init; } = new(StringComparer.Ordinal);

        public Dictionary<string, Dictionary<string, int>> PerIndexDocumentFrequencies { get; init; } =
            new(StringComparer.Ordinal);

        public int GlobalDocumentFrequency(string term) =>
            DocumentFrequencies.GetValueOrDefault(term);

        public int LocalDocumentCount(string index) =>
            PerIndexDocumentCounts.GetValueOrDefault(index);

        public int LocalDocumentFrequency(string index, string term) =>
            PerIndexDocumentFrequencies.TryGetValue(index, out var map)
                ? map.GetValueOrDefault(term)
                : 0;
    }
}
