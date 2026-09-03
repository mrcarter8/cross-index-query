using System.Text.Json;
using System.Text.Json.Serialization;

namespace CrossIndexQuery.Core.Models;

/// <summary>
/// Curated mapping from Goodreads user shelf tags to canonical genres, loaded from
/// <c>data/genre-map.json</c>.
/// </summary>
/// <remarks>
/// Kept as data rather than code so the taxonomy can be retuned — or swapped wholesale for a
/// different corpus — without touching the sample's logic.
/// </remarks>
public sealed class GenreMap
{
    private readonly Dictionary<string, string> _tagToGenre;

    private GenreMap(Dictionary<string, string> tagToGenre, IReadOnlyList<string> stripeA, IReadOnlyList<string> stripeB)
    {
        _tagToGenre = tagToGenre;
        StripeAGenres = stripeA;
        StripeBGenres = stripeB;
    }

    /// <summary>Genres routed to the first stripe under <c>StripeMode.Genre</c>.</summary>
    public IReadOnlyList<string> StripeAGenres { get; }

    /// <summary>Genres routed to the second stripe under <c>StripeMode.Genre</c>.</summary>
    public IReadOnlyList<string> StripeBGenres { get; }

    /// <summary>All canonical genres, in stripe order.</summary>
    public IEnumerable<string> AllGenres => StripeAGenres.Concat(StripeBGenres);

    public static GenreMap Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Genre map not found at '{path}'.", path);
        }

        using FileStream stream = File.OpenRead(path);
        GenreMapFile file = JsonSerializer.Deserialize<GenreMapFile>(stream)
            ?? throw new InvalidOperationException($"Genre map at '{path}' is empty or malformed.");

        var tagToGenre = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string genre, string[] tags) in file.Genres)
        {
            foreach (string tag in tags)
            {
                // First mapping wins, so an accidental duplicate cannot silently reassign a tag.
                tagToGenre.TryAdd(tag, genre);
            }
        }

        IReadOnlyList<string> a = file.StripeGroups.TryGetValue("a", out string[]? ga) ? ga : [];
        IReadOnlyList<string> b = file.StripeGroups.TryGetValue("b", out string[]? gb) ? gb : [];

        return new GenreMap(tagToGenre, a, b);
    }

    /// <summary>Resolves a raw shelf tag to a canonical genre, or null when it is not subject matter.</summary>
    public string? Resolve(string tagName) =>
        _tagToGenre.TryGetValue(tagName, out string? genre) ? genre : null;

    /// <summary>Number of distinct tag strings in the allow-list.</summary>
    public int TagCount => _tagToGenre.Count;

    private sealed class GenreMapFile
    {
        [JsonPropertyName("genres")]
        public Dictionary<string, string[]> Genres { get; set; } = [];

        [JsonPropertyName("stripeGroups")]
        public Dictionary<string, string[]> StripeGroups { get; set; } = [];
    }
}
