using Azure.Search.Documents.Indexes.Models;

namespace CrossIndexQuery.Core.Indexing;

/// <summary>
/// Builds the one index schema used by every index in the sample.
/// </summary>
/// <remarks>
/// <para>
/// There is a single method here on purpose. The whole sample rests on the claim that differences
/// in result quality come from <em>where documents live</em>, not from how the indexes are
/// configured. If the stripes and the oracle could drift apart — a different analyzer, a different
/// vector metric, a different semantic configuration — every number the harness produces would be
/// unattributable. Making the schema impossible to vary per index removes that whole class of
/// error.
/// </para>
/// </remarks>
public static class BookIndexSchema
{
    public const string SemanticConfigurationName = "books-semantic";
    public const string VectorProfileName = "books-vector-profile";
    public const string VectorAlgorithmName = "books-hnsw";
    public const string VectorFieldName = "contentVector";

    /// <summary>Fields returned by default. Excludes the vector, which is large and never displayed.</summary>
    public static readonly string[] DefaultSelect =
    [
        "id", "title", "authors", "blurb", "genre", "genres",
        "publicationYear", "averageRating", "ratingsCount", "language", "stripe",
    ];

    /// <summary>
    /// Fields BM25 searches, in one place so that queries, document-frequency probes and the
    /// offline statistics sidecar all agree on what "the text" of a document is.
    /// </summary>
    /// <remarks>
    /// Divergence here would be invisible and would corrupt every corpus statistic the sample
    /// computes, because a term's document frequency is only meaningful relative to a fixed set of
    /// fields.
    /// </remarks>
    public static readonly string[] TextSearchFields = ["title", "authors", "blurb"];

    public static SearchIndex Create(string name, int vectorDimensions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfLessThan(vectorDimensions, 1);

        var index = new SearchIndex(name)
        {
            Fields =
            {
                new SimpleField("id", SearchFieldDataType.String) { IsKey = true, IsFilterable = true },

                // Title and blurb carry the lexical signal. en.microsoft lemmatizes, so "running"
                // matches "run" - closer to how a real catalogue behaves than the default analyzer.
                new SearchableField("title") { AnalyzerName = LexicalAnalyzerName.EnMicrosoft },
                new SearchableField("blurb") { AnalyzerName = LexicalAnalyzerName.EnMicrosoft },

                new SearchableField("authors", collection: true)
                {
                    AnalyzerName = LexicalAnalyzerName.EnMicrosoft,
                    IsFilterable = true,
                    IsFacetable = true,
                },

                // Genre is the striping axis, so it must be filterable and facetable to support
                // the diagnostics that explain a fusion strategy's behaviour.
                new SearchableField("genre") { IsFilterable = true, IsFacetable = true, IsSortable = true },
                new SearchableField("genres", collection: true) { IsFilterable = true, IsFacetable = true },

                new SimpleField("publicationYear", SearchFieldDataType.Int32)
                    { IsFilterable = true, IsSortable = true, IsFacetable = true },
                new SimpleField("averageRating", SearchFieldDataType.Double)
                    { IsFilterable = true, IsSortable = true },
                new SimpleField("ratingsCount", SearchFieldDataType.Int32)
                    { IsFilterable = true, IsSortable = true },
                new SimpleField("language", SearchFieldDataType.String)
                    { IsFilterable = true, IsFacetable = true },

                // Present in the oracle too, which is what lets a single facet query answer
                // "how was the true top-N distributed across stripes?".
                new SimpleField("stripe", SearchFieldDataType.String)
                    { IsFilterable = true, IsFacetable = true },

                new VectorSearchField(VectorFieldName, vectorDimensions, VectorProfileName),
            },
            VectorSearch = new VectorSearch
            {
                Algorithms =
                {
                    // Cosine, because the raw similarity it produces is a property of the two
                    // vectors alone. Unlike BM25 it does not depend on the corpus, which is
                    // precisely why vector scores survive being compared across indexes.
                    new HnswAlgorithmConfiguration(VectorAlgorithmName)
                    {
                        Parameters = new HnswParameters
                        {
                            Metric = VectorSearchAlgorithmMetric.Cosine,
                            M = 4,
                            EfConstruction = 400,
                            EfSearch = 500,
                        },
                    },
                },
                Profiles = { new VectorSearchProfile(VectorProfileName, VectorAlgorithmName) },
            },
            SemanticSearch = new SemanticSearch
            {
                Configurations =
                {
                    new SemanticConfiguration(
                        SemanticConfigurationName,
                        new SemanticPrioritizedFields
                        {
                            TitleField = new SemanticField("title"),
                            ContentFields = { new SemanticField("blurb") },
                            KeywordsFields = { new SemanticField("genres"), new SemanticField("authors") },
                        }),
                },
            },
        };

        return index;
    }
}
