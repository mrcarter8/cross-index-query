using CrossIndexQuery.Core.Indexing;
using CrossIndexQuery.Core.Models;
using CrossIndexQuery.Core.Retrieval;
using CrossIndexQuery.Core.Statistics;

namespace CrossIndexQuery.Core.Fusion;

/// <summary>
/// Recomputes BM25 client-side using each index's <em>own</em> statistics rather than the corpus's.
/// </summary>
/// <remarks>
/// <para>
/// This strategy is a control, not a recommendation. It exists to answer the first objection a
/// careful reader should raise about <see cref="GlobalBm25Fusion"/>.
/// </para>
/// <para>
/// <see cref="GlobalBm25Fusion"/> differs from the scores the service returned in <em>two</em> ways
/// at once: it substitutes corpus-wide document frequencies for per-index ones, and it tokenizes
/// with this project's simple tokenizer rather than the analyzer that built the index. Its measured
/// advantage could therefore come from either — from repairing the cross-index statistics, which is
/// the claim, or merely from scoring both stripes with one consistent tokenizer, which would be a
/// far less interesting result and would not generalize.
/// </para>
/// <para>
/// This strategy holds the tokenizer fixed and varies only the statistics. It runs exactly the same
/// arithmetic over exactly the same text, but takes each term's document frequency and each index's
/// average document length from the index that returned the document. So:
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///     If <see cref="GlobalBm25Fusion"/> beats <em>this</em>, the gain is attributable to using
///     global statistics, which is the sample's claim.
///     </description>
///   </item>
///   <item>
///     <description>
///     If the two are indistinguishable, the gain was the tokenizer all along and the claim is
///     wrong.
///     </description>
///   </item>
/// </list>
/// <para>
/// Reporting a control that can falsify the headline result is the point. A comparison that cannot
/// come out against you is not evidence.
/// </para>
/// </remarks>
public sealed class LocalBm25Fusion(CorpusStatistics statistics) : IFusionStrategy
{
    private const double K1 = 1.2;
    private const double B = 0.75;

    public string Name => "local-bm25";

    public string Description =>
        "Control for global-bm25: same recomputation, but with each index's own statistics.";

    public bool Supports(RetrievalMode mode) => mode is RetrievalMode.Keyword or RetrievalMode.Hybrid;

    public ValueTask<IReadOnlyList<FusedDocument>> FuseAsync(
        FanOutResult fanOut,
        FusionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fanOut);
        ArgumentNullException.ThrowIfNull(context);

        List<string> terms = TextTokenizer.TokenizeQuery(fanOut.Query);

        if (terms.Count == 0)
        {
            return new NaiveScoreFusion().FuseAsync(fanOut, context, cancellationToken);
        }

        List<FusedDocument> scored = [];

        foreach (StripeResultSet stripe in fanOut.Stripes)
        {
            int localCount = statistics.LocalDocumentCount(stripe.IndexName);
            double localAverageLength = statistics.LocalAverageDocumentLength(stripe.IndexName);

            foreach (ScoredDocument doc in stripe.Documents)
            {
                double score = Score(doc.Document, terms, stripe.IndexName, localCount, localAverageLength);

                scored.Add(new FusedDocument(
                    doc,
                    score,
                    $"local BM25 {score:F4} using {stripe.IndexName}'s own statistics"));
            }
        }

        return ValueTask.FromResult(FusionHelpers.RankAndTruncate(scored, context.TopK));
    }

    private double Score(
        BookDocument document,
        IReadOnlyList<string> terms,
        string indexName,
        int localCount,
        double localAverageLength)
    {
        Dictionary<string, int> termFrequencies = CountTerms(document, out int documentLength);

        double averageLength = localAverageLength > 0 ? localAverageLength : documentLength;
        double score = 0;

        foreach (string term in terms)
        {
            if (!termFrequencies.TryGetValue(term, out int tf) || tf == 0)
            {
                continue;
            }

            // The only line that differs from GlobalBm25Fusion: the frequency and the document
            // count both come from the index that returned this document.
            double idf = CorpusStatistics.Idf(
                statistics.LocalDocumentFrequency(indexName, term), localCount);

            double normalization = 1 - B + (B * documentLength / averageLength);
            score += idf * (tf * (K1 + 1)) / (tf + (K1 * normalization));
        }

        return score;
    }

    private static Dictionary<string, int> CountTerms(BookDocument document, out int documentLength)
    {
        Dictionary<string, int> counts = new(StringComparer.Ordinal);
        documentLength = 0;

        foreach (string field in BookIndexSchema.TextSearchFields)
        {
            string? value = field switch
            {
                "title" => document.Title,
                "authors" => string.Join(' ', document.Authors),
                "blurb" => document.Blurb,
                _ => null,
            };

            foreach (string token in TextTokenizer.Tokenize(value))
            {
                counts[token] = counts.GetValueOrDefault(token) + 1;
                documentLength++;
            }
        }

        return counts;
    }
}
