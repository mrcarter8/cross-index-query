using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CrossIndexQuery.Core.Statistics;

/// <summary>
/// Splits text into terms the same way everywhere it is needed.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately not an attempt to reproduce the <c>en.microsoft</c> analyzer. It does not
/// need to be. Every consumer of these statistics — the offline sidecar builder, the document
/// frequency probes, and the correction applied at query time — uses this same tokenizer, so the
/// document frequencies and the query terms are drawn from one consistent vocabulary.
/// </para>
/// <para>
/// What matters for correcting cross-index score bias is the <em>ratio</em> between one index's
/// view of a term and the whole corpus's view of it. That ratio is preserved under any consistent
/// tokenization, even one coarser than the analyzer the service actually used.
/// </para>
/// </remarks>
public static class TextTokenizer
{
    /// <summary>
    /// Common English words carry almost no discriminative weight, and their document frequencies
    /// are so high that including them adds noise to the correction without changing the ordering.
    /// </summary>
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "are", "as", "at", "be", "but", "by", "for", "from", "has", "have",
        "he", "her", "his", "in", "is", "it", "its", "of", "on", "or", "she", "that", "the",
        "their", "them", "they", "this", "to", "was", "were", "what", "when", "where", "which",
        "who", "will", "with", "you", "your",
    };

    public static List<string> Tokenize(string? text)
    {
        List<string> tokens = [];
        if (string.IsNullOrWhiteSpace(text))
        {
            return tokens;
        }

        var current = new StringBuilder();

        foreach (char c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                current.Append(char.ToLowerInvariant(c));
            }
            else if (current.Length > 0)
            {
                Emit(tokens, current);
            }
        }

        if (current.Length > 0)
        {
            Emit(tokens, current);
        }

        return tokens;
    }

    /// <summary>Distinct query terms, preserving first-appearance order.</summary>
    public static List<string> TokenizeQuery(string? text) =>
        [.. Tokenize(text).Distinct(StringComparer.Ordinal)];

    private static void Emit(List<string> tokens, StringBuilder current)
    {
        string token = current.ToString();
        current.Clear();

        // Single characters are never useful and stop words are actively unhelpful.
        if (token.Length > 1 && !StopWords.Contains(token))
        {
            tokens.Add(token);
        }
    }
}

/// <summary>
/// Corpus-level statistics needed to compute BM25 the way a single index would have.
/// </summary>
public sealed class CorpusStatistics
{
    /// <summary>Total documents across the whole logical corpus, not one stripe.</summary>
    [JsonPropertyName("documentCount")]
    public int DocumentCount { get; set; }

    /// <summary>Mean document length in tokens, across the whole corpus.</summary>
    [JsonPropertyName("averageDocumentLength")]
    public double AverageDocumentLength { get; set; }

    /// <summary>
    /// Which partitioning this sidecar describes, e.g. <c>genre</c> or <c>temporal-2013</c>.
    /// </summary>
    /// <remarks>
    /// Provenance, and a safety catch. A sidecar built for one split applied to a different one
    /// produces confidently wrong IDF corrections rather than an error, because every lookup still
    /// succeeds — the numbers are simply about a partitioning that no longer exists.
    /// </remarks>
    [JsonPropertyName("splitDescriptor")]
    public string SplitDescriptor { get; set; } = string.Empty;

    /// <summary>
    /// Mean document length within each stripe.
    /// </summary>
    /// <remarks>
    /// BM25 has two corpus-dependent terms, not one. IDF is the famous one, but the length
    /// normalisation <c>1 - b + b|d|/avgdl</c> is also computed per index, so two stripes with
    /// different average document lengths disagree about scores even when they agree perfectly
    /// about term rarity. Recording it per stripe is what makes that divergence measurable rather
    /// than assumed absent.
    /// </remarks>
    [JsonPropertyName("perIndexAverageDocumentLength")]
    public Dictionary<string, double> PerIndexAverageDocumentLength { get; set; } =
        new(StringComparer.Ordinal);

    /// <summary>
    /// How many documents in the whole corpus contain each term. This is the number each stripe
    /// cannot know and therefore the root of the incomparability being corrected.
    /// </summary>
    [JsonPropertyName("documentFrequencies")]
    public Dictionary<string, int> DocumentFrequencies { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Per-index document counts, used to reconstruct each stripe's local view.</summary>
    [JsonPropertyName("perIndexDocumentCounts")]
    public Dictionary<string, int> PerIndexDocumentCounts { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Per-index document frequencies, so a stripe's local IDF can be recomputed offline.</summary>
    [JsonPropertyName("perIndexDocumentFrequencies")]
    public Dictionary<string, Dictionary<string, int>> PerIndexDocumentFrequencies { get; set; } =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Okapi BM25 inverse document frequency, in the form Azure AI Search uses.
    /// </summary>
    /// <remarks>
    /// The <c>+1</c> inside the logarithm keeps the result non-negative even for a term that
    /// appears in more than half the corpus, which the classical formulation does not.
    /// </remarks>
    public static double Idf(int documentFrequency, int documentCount)
    {
        double df = Math.Max(documentFrequency, 0);
        double n = Math.Max(documentCount, 1);
        return Math.Log(1 + ((n - df + 0.5) / (df + 0.5)));
    }

    public int GlobalDocumentFrequency(string term) =>
        DocumentFrequencies.TryGetValue(term, out int value) ? value : 0;

    public int LocalDocumentFrequency(string indexName, string term) =>
        PerIndexDocumentFrequencies.TryGetValue(indexName, out Dictionary<string, int>? map)
        && map.TryGetValue(term, out int value)
            ? value
            : 0;

    public int LocalDocumentCount(string indexName) =>
        PerIndexDocumentCounts.TryGetValue(indexName, out int value) ? value : 0;

    /// <summary>Mean document length within one stripe, falling back to the global mean.</summary>
    public double LocalAverageDocumentLength(string indexName) =>
        PerIndexAverageDocumentLength.TryGetValue(indexName, out double value) && value > 0
            ? value
            : AverageDocumentLength;

    /// <summary>Conventional file name for the committed sidecar.</summary>
    public const string FileName = "corpus-statistics.json";

    /// <summary>File name for the sidecar describing a particular split.</summary>
    public static string FileNameFor(string splitDescriptor) =>
        string.IsNullOrWhiteSpace(splitDescriptor)
            ? FileName
            : $"corpus-statistics.{splitDescriptor}.json";

    /// <summary>
    /// Loads the sidecar for a split, preferring the split-specific file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The sidecar is optional by design. Its absence disables exactly one fusion strategy and the
    /// probe-based equivalent still runs, so a missing file is a reduced experiment rather than a
    /// broken one.
    /// </para>
    /// <para>
    /// The unqualified file is accepted as a fallback so existing corpora keep working, but it is
    /// rejected when it names a different split — silently correcting IDF with statistics from
    /// another partitioning is worse than not correcting at all, because the result looks fine.
    /// </para>
    /// </remarks>
    public static bool TryLoad(string dataDirectory, string splitDescriptor, out CorpusStatistics? statistics)
    {
        statistics = null;

        string[] candidates =
        [
            Path.Combine(dataDirectory, FileNameFor(splitDescriptor)),
            Path.Combine(dataDirectory, FileName),
        ];

        foreach (string path in candidates)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            using FileStream stream = File.OpenRead(path);
            CorpusStatistics? loaded = JsonSerializer.Deserialize<CorpusStatistics>(stream);

            if (loaded is null)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(loaded.SplitDescriptor)
                && !string.Equals(loaded.SplitDescriptor, splitDescriptor, StringComparison.Ordinal))
            {
                Console.Error.WriteLine(
                    $"Ignoring {Path.GetFileName(path)}: it describes the '{loaded.SplitDescriptor}' "
                    + $"split but the current configuration is '{splitDescriptor}'. Re-run "
                    + "'dataprep stats' for this split.");
                continue;
            }

            statistics = loaded;
            return true;
        }

        return false;
    }

    public async Task SaveAsync(
        string dataDirectory,
        string splitDescriptor,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(dataDirectory);
        SplitDescriptor = splitDescriptor;

        await using FileStream stream = File.Create(
            Path.Combine(dataDirectory, FileNameFor(splitDescriptor)));

        await JsonSerializer
            .SerializeAsync(stream, this, SerializerOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
}
