using System.Text.Json.Serialization;

namespace CrossIndexQuery.Core.Models;

/// <summary>
/// One book, as stored in every index. The schema is identical in all three indexes —
/// both stripes and the oracle — because comparing results across indexes is only meaningful
/// when the documents are shaped the same way.
/// </summary>
public sealed class BookDocument
{
    /// <summary>
    /// Stable document key. Azure AI Search keys allow only letters, digits, underscore, dash and
    /// equals, so the numeric goodbooks id is prefixed rather than used bare.
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("authors")]
    public string[] Authors { get; set; } = [];

    /// <summary>
    /// LLM-generated ~120 word description. goodbooks-10k ships no description field, and a
    /// title-only corpus cannot demonstrate anything interesting about text relevance or
    /// vector similarity, so the blurb is generated once, committed, and reused by everyone.
    /// </summary>
    [JsonPropertyName("blurb")]
    public string Blurb { get; set; } = string.Empty;

    /// <summary>Primary genre, and the axis the corpus is striped along in Genre mode.</summary>
    [JsonPropertyName("genre")]
    public string Genre { get; set; } = string.Empty;

    /// <summary>All allow-listed genres for this book, most-tagged first.</summary>
    [JsonPropertyName("genres")]
    public string[] Genres { get; set; } = [];

    [JsonPropertyName("publicationYear")]
    public int? PublicationYear { get; set; }

    [JsonPropertyName("averageRating")]
    public double AverageRating { get; set; }

    [JsonPropertyName("ratingsCount")]
    public int RatingsCount { get; set; }

    [JsonPropertyName("language")]
    public string? Language { get; set; }

    /// <summary>
    /// Embedding of title + authors + genre + blurb. Generated client-side with one model so the
    /// identical vector is written to every index; see <c>EmbeddingModel</c> on the manifest.
    /// </summary>
    [JsonPropertyName("contentVector")]
    public float[]? ContentVector { get; set; }

    /// <summary>
    /// Which stripe this document was routed to.
    /// </summary>
    /// <remarks>
    /// Stored as a real field, including in the oracle index. That makes the single most useful
    /// diagnostic in the sample a one-line facet query: of the true top-N for a query, how were
    /// they split across stripes? A fusion strategy's error is almost always explained by that
    /// distribution being lopsided.
    /// </remarks>
    [JsonPropertyName("stripe")]
    public string? AssignedStripe { get; set; }

    /// <summary>Text that gets embedded. Kept in one place so indexing and querying cannot drift.</summary>
    public string BuildEmbeddingInput() =>
        $"{Title}\nBy {string.Join(", ", Authors)}\nGenre: {Genre}\n\n{Blurb}";
}
