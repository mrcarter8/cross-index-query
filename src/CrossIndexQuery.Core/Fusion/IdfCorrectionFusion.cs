using CrossIndexQuery.Core.Retrieval;
using CrossIndexQuery.Core.Statistics;

namespace CrossIndexQuery.Core.Fusion;

/// <summary>
/// Rescales each index's BM25 scores by the ratio between the corpus-wide inverse document
/// frequency of the query terms and that index's local view of them.
/// </summary>
/// <remarks>
/// <para>
/// This attacks the cause of the problem rather than its symptoms. When a corpus is split, each
/// index computes inverse document frequency over only the documents it holds. A term that is rare
/// in one stripe and common in the other is scored as highly informative by the first and
/// unremarkable by the second, so identical documents receive different scores depending only on
/// where they happen to live. Every rank- and normalization-based strategy works around that
/// distortion; this one removes it.
/// </para>
/// <para>
/// The correction is a ratio. For each query term, the inverse document frequency the whole corpus
/// would have produced is divided by the one the index actually used, and the index's scores are
/// multiplied by the average of those ratios, weighted by each term's global informativeness. A
/// stripe that over-valued the query's terms is scaled down; one that under-valued them is scaled
/// up.
/// </para>
/// <para>
/// It is an approximation, and the shape of the approximation is worth being precise about. BM25
/// sums a separate inverse-document-frequency-weighted contribution per term, and the per-term
/// term frequencies are not disclosed in a search response, so the sum cannot be decomposed and
/// each term corrected individually. Applying one weighted average factor to the whole score is
/// exact for single-term queries and increasingly approximate as terms multiply and disagree about
/// which stripe favoured them. It corrects the systematic component of the bias, which is the part
/// that reorders results.
/// </para>
/// <para>
/// Where the frequencies come from is a separate decision from what is done with them, so the
/// provider is injected: a sidecar built offline costs nothing at query time, while probing costs
/// a few counting requests and needs no preparation.
/// </para>
/// </remarks>
public sealed class IdfCorrectionFusion(IDocumentFrequencyProvider provider) : IFusionStrategy
{
    public string Name => $"idf-correct-{provider.Source}";

    public string Description =>
        $"Rescale each index's BM25 by global/local IDF ratio (frequencies from {provider.Source}).";

    // Pure vector scores carry no corpus statistics, so there is nothing here to correct.
    public bool Supports(RetrievalMode mode) => mode is RetrievalMode.Keyword or RetrievalMode.Hybrid;

    public async ValueTask<IReadOnlyList<FusedDocument>> FuseAsync(
        FanOutResult fanOut,
        FusionContext context,
        CancellationToken cancellationToken = default)
    {
        List<string> terms = TextTokenizer.TokenizeQuery(fanOut.Query);
        string[] indexNames = [.. fanOut.Stripes.Select(s => s.IndexName)];

        if (terms.Count == 0 || indexNames.Length == 0)
        {
            return await new NaiveScoreFusion().FuseAsync(fanOut, context, cancellationToken)
                .ConfigureAwait(false);
        }

        TermFrequencies frequencies = await provider
            .GetAsync(terms, indexNames, cancellationToken)
            .ConfigureAwait(false);

        List<FusedDocument> scored = [];

        foreach (StripeResultSet stripe in fanOut.Stripes)
        {
            double factor = CorrectionFactor(stripe.IndexName, terms, frequencies);

            foreach (ScoredDocument doc in stripe.Documents)
            {
                // The text leg's BM25 when available, so this also works on hybrid results where
                // the headline score is an RRF value with no BM25 left in it.
                double baseScore = doc.TextScore ?? doc.Score;
                double corrected = baseScore * factor;

                scored.Add(new FusedDocument(
                    doc,
                    corrected,
                    $"{baseScore:F4} x {factor:F3} = {corrected:F4} ({stripe.IndexName} IDF correction)"));
            }
        }

        return FusionHelpers.RankAndTruncate(scored, context.TopK);
    }

    /// <summary>
    /// Weighted mean of <c>IDF_global(t) / IDF_local(t)</c> over the query terms.
    /// </summary>
    /// <remarks>
    /// Weighting by the term's global inverse document frequency makes informative terms dominate
    /// the correction. That is the desired behaviour: the whole distortion is concentrated in terms
    /// whose rarity the two indexes disagree about, and common terms have little to disagree over.
    /// </remarks>
    private static double CorrectionFactor(
        string indexName,
        IReadOnlyList<string> terms,
        TermFrequencies frequencies)
    {
        if (!frequencies.LocalDocumentFrequency.TryGetValue(indexName, out IReadOnlyDictionary<string, int>? local)
            || !frequencies.LocalDocumentCounts.TryGetValue(indexName, out int localCount)
            || localCount == 0)
        {
            return 1d;
        }

        double weightedSum = 0;
        double weightTotal = 0;

        foreach (string term in terms)
        {
            int globalDf = frequencies.GlobalDocumentFrequency.TryGetValue(term, out int g) ? g : 0;
            int localDf = local.TryGetValue(term, out int l) ? l : 0;

            // A term absent from this stripe tells us nothing about how this stripe scored it.
            if (localDf == 0)
            {
                continue;
            }

            double globalIdf = CorpusStatistics.Idf(globalDf, frequencies.GlobalDocumentCount);
            double localIdf = CorpusStatistics.Idf(localDf, localCount);

            if (localIdf <= double.Epsilon)
            {
                continue;
            }

            weightedSum += globalIdf / localIdf * globalIdf;
            weightTotal += globalIdf;
        }

        return weightTotal > double.Epsilon ? weightedSum / weightTotal : 1d;
    }
}
