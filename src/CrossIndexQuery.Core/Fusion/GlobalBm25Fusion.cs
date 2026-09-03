using CrossIndexQuery.Core.Indexing;
using CrossIndexQuery.Core.Models;
using CrossIndexQuery.Core.Retrieval;
using CrossIndexQuery.Core.Statistics;

namespace CrossIndexQuery.Core.Fusion;

/// <summary>
/// Recomputes BM25 from the returned document text using corpus-wide statistics, discarding the
/// scores the indexes reported.
/// </summary>
/// <remarks>
/// <para>
/// Every other keyword strategy in the catalog works with the numbers the service handed back.
/// This one throws them away. Each index scored its documents against the corpus it happens to
/// hold; rather than trying to reconcile two incompatible measurements, this recomputes the
/// measurement that a single index would have produced, over the merged candidate pool, using the
/// global document frequencies and average document length from the sidecar.
/// </para>
/// <para>
/// The distinction from <see cref="IdfCorrectionFusion"/> is worth being precise about, because
/// they use the same statistics to different ends. IDF correction applies one weighted factor to a
/// score it cannot decompose, which is exact for a single-term query and approximate beyond that.
/// This computes each term's contribution separately from the actual term frequencies in the text,
/// so multi-term queries are handled exactly rather than approximately — and the result is
/// identical no matter which index a document came from, because no per-index quantity enters the
/// calculation at all.
/// </para>
/// <para>
/// What it costs is the document text. The score is computed from the fields the query searched, so
/// those fields have to be returned, and the arithmetic runs over the whole merged pool rather than
/// over a handful of query terms. In exchange it needs no model, no second round trip, no service
/// feature and no tier: it is ordinary arithmetic over data already in hand, and it runs anywhere
/// the results can be assembled.
/// </para>
/// <para>
/// The one place it can differ from the service's own scoring is tokenization. The analyzer that
/// built the index applies stemming and language rules this does not reproduce, so a term the
/// analyzer would have matched by stem can be missed here. That affects both stripes identically
/// and so does not reintroduce cross-index bias, but it does mean this is a faithful reimplementation
/// of BM25 rather than a bit-exact replica of the service's scorer.
/// </para>
/// </remarks>
public sealed class GlobalBm25Fusion(CorpusStatistics statistics) : IFusionStrategy
{
    /// <summary>
    /// Term-frequency saturation. Azure AI Search uses the Lucene defaults, and matching them keeps
    /// the recomputed score on the same footing as the one it replaces.
    /// </summary>
    private const double K1 = 1.2;

    /// <summary>Length-normalization strength, again the Lucene default.</summary>
    private const double B = 0.75;

    public string Name => "global-bm25";

    public string Description =>
        "Recompute BM25 client-side over the merged pool using global corpus statistics.";

    // Vector scores contain no BM25 to replace. On hybrid the text leg is what this rebuilds.
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

        foreach (ScoredDocument doc in fanOut.AllDocuments)
        {
            double score = Score(doc.Document, terms, out int matched);

            scored.Add(new FusedDocument(
                doc,
                score,
                $"global BM25 {score:F4} over {matched}/{terms.Count} query terms "
                + $"(was {doc.TextScore ?? doc.Score:F4} in {doc.SourceIndex})"));
        }

        return ValueTask.FromResult(FusionHelpers.RankAndTruncate(scored, context.TopK));
    }

    /// <summary>
    /// Okapi BM25 over the searchable fields, with every corpus quantity taken from the whole
    /// corpus rather than from the index that returned the document.
    /// </summary>
    private double Score(BookDocument document, IReadOnlyList<string> terms, out int matched)
    {
        Dictionary<string, int> termFrequencies = CountTerms(document, out int documentLength);

        double averageLength = statistics.AverageDocumentLength > 0
            ? statistics.AverageDocumentLength
            : documentLength;

        double score = 0;
        matched = 0;

        foreach (string term in terms)
        {
            if (!termFrequencies.TryGetValue(term, out int tf) || tf == 0)
            {
                continue;
            }

            matched++;

            double idf = CorpusStatistics.Idf(
                statistics.GlobalDocumentFrequency(term), statistics.DocumentCount);

            double normalization = 1 - B + (B * documentLength / averageLength);
            score += idf * (tf * (K1 + 1)) / (tf + (K1 * normalization));
        }

        return score;
    }

    /// <summary>
    /// Counts term occurrences across exactly the fields the query searched.
    /// </summary>
    /// <remarks>
    /// Counting a field the query does not search would credit a document for a match that could
    /// not have contributed to its real score, which is the same error in the opposite direction
    /// from the one this strategy exists to fix.
    /// </remarks>
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
