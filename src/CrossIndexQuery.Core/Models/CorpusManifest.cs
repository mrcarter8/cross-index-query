using System.Text.Json.Serialization;

namespace CrossIndexQuery.Core.Models;

/// <summary>
/// Metadata written alongside the corpus describing how it was produced.
/// </summary>
/// <remarks>
/// The preflight validator compares this against live configuration before any cross-index query
/// runs. Cross-index vector comparison is only valid when every index was populated from the same
/// embedding model at the same dimensionality — mix two models and the cosine scores are drawn
/// from different spaces, so merging them produces confident nonsense rather than a visible error.
/// </remarks>
public sealed class CorpusManifest
{
    [JsonPropertyName("generatedUtc")]
    public DateTimeOffset GeneratedUtc { get; set; }

    [JsonPropertyName("documentCount")]
    public int DocumentCount { get; set; }

    [JsonPropertyName("embeddingModel")]
    public string EmbeddingModel { get; set; } = string.Empty;

    [JsonPropertyName("embeddingDimensions")]
    public int EmbeddingDimensions { get; set; }

    [JsonPropertyName("blurbModel")]
    public string? BlurbModel { get; set; }

    [JsonPropertyName("sourceDataset")]
    public string SourceDataset { get; set; } = "goodbooks-10k";

    [JsonPropertyName("genreCounts")]
    public Dictionary<string, int> GenreCounts { get; set; } = [];

    /// <summary>
    /// How <c>contentVector</c> is encoded in the committed corpus file.
    /// </summary>
    /// <remarks>
    /// Recorded so the encoding is discoverable from the data rather than only from source. A
    /// consumer inspecting the corpus sees base64 text where they expected numbers, and this is
    /// the field that explains why.
    /// </remarks>
    [JsonPropertyName("vectorEncoding")]
    public string VectorEncoding { get; set; } = "int8-base64";
}
