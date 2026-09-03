using System.Text.Json;
using CrossIndexQuery.Core.Configuration;
using CrossIndexQuery.Core.Indexing;
using CrossIndexQuery.Core.Models;
using CrossIndexQuery.Core.Statistics;

namespace CrossIndexQuery.DataPrep.Stages;

/// <summary>
/// Computes the corpus statistics that no individual stripe can know.
/// </summary>
/// <remarks>
/// <para>
/// This is the offline half of the IDF-correction argument. BM25 weights a term by how rare it is,
/// and rarity is measured against whatever corpus the index happens to hold. Split one corpus into
/// two indexes and each one computes a different, locally correct, mutually incomparable rarity for
/// the same word. Fixing that after the fact requires knowing what the term's rarity would have
/// been in the undivided corpus, which is exactly what this stage writes down.
/// </para>
/// <para>
/// The output is committed alongside the corpus. Recomputing it at query time would mean scanning
/// every document on every query; computing it once offline costs a single pass and turns the
/// correction into arithmetic. The trade-off is that the sidecar goes stale as documents change,
/// which is why the sample also ships a probe-based provider that pays per-query latency in
/// exchange for always being current. Shipping both is the point: they bracket the practical range.
/// </para>
/// </remarks>
public sealed class CorpusStatisticsStage(string dataDirectory)
{
    public async Task<int> RunAsync(CrossIndexOptions options, CancellationToken cancellationToken = default)
    {
        string corpusPath = Path.Combine(dataDirectory, CorpusFile.FileName);

        if (!File.Exists(corpusPath))
        {
            Console.Error.WriteLine(
                $"{corpusPath} not found. Run 'blurbs collect' (and ideally 'embed') first.");
            return 1;
        }

        List<BookDocument> books = await CorpusFile.LoadAsync(corpusPath, cancellationToken)
            .ConfigureAwait(false);

        if (books.Count == 0)
        {
            Console.Error.WriteLine("Corpus was empty.");
            return 1;
        }

        GenreMap genreMap = GenreMap.Load(Path.Combine(dataDirectory, "genre-map.json"));

        var router = new StripeRouter(options.Search, options.Corpus, genreMap);

        var statistics = new CorpusStatistics();
        long totalLength = 0;
        Dictionary<string, long> lengthByStripe = new(StringComparer.Ordinal);

        foreach (BookDocument book in books)
        {
            string stripe = router.Route(book);

            statistics.PerIndexDocumentCounts[stripe] =
                statistics.PerIndexDocumentCounts.GetValueOrDefault(stripe) + 1;

            // Document frequency counts documents, not occurrences, so each term is credited once
            // per document however many times it appears. The distinct set here is what makes that
            // true; without it this would be collection frequency, which BM25 does not use.
            HashSet<string> terms = ExtractTerms(book, out int length);
            totalLength += length;
            lengthByStripe[stripe] = lengthByStripe.GetValueOrDefault(stripe) + length;

            foreach (string term in terms)
            {
                statistics.DocumentFrequencies[term] =
                    statistics.DocumentFrequencies.GetValueOrDefault(term) + 1;

                if (!statistics.PerIndexDocumentFrequencies.TryGetValue(
                        stripe, out Dictionary<string, int>? local))
                {
                    local = new Dictionary<string, int>(StringComparer.Ordinal);
                    statistics.PerIndexDocumentFrequencies[stripe] = local;
                }

                local[term] = local.GetValueOrDefault(term) + 1;
            }
        }

        statistics.DocumentCount = books.Count;
        statistics.AverageDocumentLength = totalLength / (double)books.Count;

        foreach ((string stripe, long length) in lengthByStripe)
        {
            int count = statistics.PerIndexDocumentCounts.GetValueOrDefault(stripe);
            if (count > 0)
            {
                statistics.PerIndexAverageDocumentLength[stripe] = length / (double)count;
            }
        }

        await statistics.SaveAsync(dataDirectory, options.Corpus.SplitDescriptor, cancellationToken)
            .ConfigureAwait(false);

        Report(statistics, options);
        return 0;
    }

    /// <summary>
    /// Tokenizes exactly the fields the query path searches.
    /// </summary>
    /// <remarks>
    /// The set of fields has to match <see cref="BookIndexSchema.TextSearchFields"/>. Counting a
    /// field the query never searches would inflate document frequencies for terms that cannot
    /// contribute to any score, and the correction would then be adjusting for a distortion that
    /// does not exist.
    /// </remarks>
    private static HashSet<string> ExtractTerms(BookDocument book, out int length)
    {
        HashSet<string> distinct = new(StringComparer.Ordinal);
        length = 0;

        foreach (string field in BookIndexSchema.TextSearchFields)
        {
            // Authors is multi-valued; joining rather than tokenizing each element separately keeps
            // the token stream identical to what a single searchable field would produce.
            string? value = field switch
            {
                "title" => book.Title,
                "authors" => string.Join(' ', book.Authors),
                "blurb" => book.Blurb,
                _ => null,
            };

            List<string> tokens = TextTokenizer.Tokenize(value);
            length += tokens.Count;

            foreach (string token in tokens)
            {
                distinct.Add(token);
            }
        }

        return distinct;
    }

    private static void Report(CorpusStatistics statistics, CrossIndexOptions options)
    {
        Console.WriteLine($"documents        {statistics.DocumentCount:N0}");
        Console.WriteLine($"distinct terms   {statistics.DocumentFrequencies.Count:N0}");
        Console.WriteLine($"avg length       {statistics.AverageDocumentLength:F1} tokens");

        // The mode is what determines whether this sidecar describes the experiment or the control,
        // and the two produce plausible-looking output that is impossible to tell apart downstream.
        // Naming it here is the difference between noticing that immediately and shipping it.
        Console.WriteLine($"stripe mode      {options.Corpus.StripeMode}");

        foreach ((string index, int count) in statistics.PerIndexDocumentCounts.OrderBy(
                     kv => kv.Key, StringComparer.Ordinal))
        {
            int terms = statistics.PerIndexDocumentFrequencies.TryGetValue(
                index, out Dictionary<string, int>? map) ? map.Count : 0;

            Console.WriteLine(
                $"  {index,-28} {count,6:N0} documents, {terms,7:N0} terms, "
                + $"avgdl {statistics.LocalAverageDocumentLength(index),6:F1}");
        }

        string[] stripes = [.. options.Search.StripeIndexes];
        if (stripes.Length != 2)
        {
            return;
        }

        // Size imbalance distorts BM25 on its own, with no vocabulary divergence required. For a
        // term appearing once, IDF is about ln(N), so two indexes of different sizes disagree about
        // that term by ln(N_large / N_small) — a systematic deflation of every rare term the smaller
        // index holds, and therefore of every document it would otherwise have ranked highly.
        int countA = statistics.LocalDocumentCount(stripes[0]);
        int countB = statistics.LocalDocumentCount(stripes[1]);

        if (countA > 0 && countB > 0)
        {
            int large = Math.Max(countA, countB);
            int small = Math.Min(countA, countB);

            Console.WriteLine();
            Console.WriteLine(
                $"Size imbalance     {large / (double)small,8:F1}:1  "
                + $"=> predicted singleton-term IDF gap {Math.Log(large / (double)small),5:F2} nats");

            double avgdlA = statistics.LocalAverageDocumentLength(stripes[0]);
            double avgdlB = statistics.LocalAverageDocumentLength(stripes[1]);
            Console.WriteLine(
                $"avgdl divergence   {Math.Abs(avgdlA - avgdlB),8:F1} tokens "
                + $"({Math.Abs(avgdlA - avgdlB) / statistics.AverageDocumentLength:P1} of global mean)");
        }

        var divergent = statistics.DocumentFrequencies
            .Where(kv => kv.Value >= 40)
            .Select(kv =>
            {
                double a = CorpusStatistics.Idf(
                    statistics.LocalDocumentFrequency(stripes[0], kv.Key),
                    statistics.LocalDocumentCount(stripes[0]));

                double b = CorpusStatistics.Idf(
                    statistics.LocalDocumentFrequency(stripes[1], kv.Key),
                    statistics.LocalDocumentCount(stripes[1]));

                return (Term: kv.Key, Delta: Math.Abs(a - b), A: a, B: b);
            })
            .OrderByDescending(t => t.Delta)
            .Take(10);

        Console.WriteLine();
        Console.WriteLine("Largest per-stripe IDF disagreements (terms in >=40 documents):");
        Console.WriteLine($"  {"term",-20}{"stripe A",10}{"stripe B",10}{"delta",10}");

        foreach ((string term, double delta, double a, double b) in divergent)
        {
            Console.WriteLine($"  {term,-20}{a,10:F3}{b,10:F3}{delta,10:F3}");
        }
    }
}
