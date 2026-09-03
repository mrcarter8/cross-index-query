using System.Globalization;
using System.Text.Json;
using CrossIndexQuery.Core.Models;
using CsvHelper;

namespace CrossIndexQuery.DataPrep.Stages;

/// <summary>
/// Joins the raw goodbooks-10k CSVs into a single base corpus with a resolved primary genre.
/// </summary>
/// <remarks>
/// goodbooks-10k ships titles and ratings but no description and no genre. Genre has to be
/// recovered by joining <c>book_tags.csv</c> to <c>tags.csv</c> and filtering the result through a
/// curated allow-list, because the raw tag vocabulary is overwhelmingly shelf management
/// ("to-read", "kindle", "read-in-2015") rather than subject matter.
/// </remarks>
internal sealed class PrepareCorpusStage
{
    private readonly string _rawDirectory;
    private readonly string _dataDirectory;

    public PrepareCorpusStage(string dataDirectory)
    {
        _dataDirectory = dataDirectory;
        _rawDirectory = Path.Combine(dataDirectory, "raw");
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        string booksPath = Path.Combine(_rawDirectory, "books.csv");
        string tagsPath = Path.Combine(_rawDirectory, "tags.csv");
        string bookTagsPath = Path.Combine(_rawDirectory, "book_tags.csv");

        foreach (string required in new[] { booksPath, tagsPath, bookTagsPath })
        {
            if (!File.Exists(required))
            {
                Console.Error.WriteLine($"Missing {required}. Run 'cross-index-dataprep download' first.");
                return 1;
            }
        }

        GenreMap genreMap = GenreMap.Load(Path.Combine(_dataDirectory, "genre-map.json"));
        Console.WriteLine($"Loaded genre map: {genreMap.TagCount} tag aliases across {genreMap.AllGenres.Count()} genres.");

        Dictionary<int, string> tagIdToGenre = LoadTagIdToGenre(tagsPath, genreMap);
        Console.WriteLine($"Matched {tagIdToGenre.Count} tag ids to canonical genres.");

        Dictionary<long, List<(string Genre, long Count)>> genresByGoodreadsId =
            AggregateGenresPerBook(bookTagsPath, tagIdToGenre);
        Console.WriteLine($"Resolved genres for {genresByGoodreadsId.Count} books.");

        List<BookDocument> documents = ReadBooks(booksPath, genresByGoodreadsId);
        Console.WriteLine($"Parsed {documents.Count} books.");

        string outputPath = Path.Combine(_dataDirectory, "books.base.json");
        await using (FileStream output = File.Create(outputPath))
        {
            await JsonSerializer.SerializeAsync(
                output,
                documents,
                new JsonSerializerOptions { WriteIndented = false },
                cancellationToken).ConfigureAwait(false);
        }

        ReportDistribution(documents);
        Console.WriteLine($"\nWrote {outputPath}");
        return 0;
    }

    private static Dictionary<int, string> LoadTagIdToGenre(string tagsPath, GenreMap genreMap)
    {
        var result = new Dictionary<int, string>();
        using var reader = new StreamReader(tagsPath);
        _ = reader.ReadLine();

        while (reader.ReadLine() is { } line)
        {
            int comma = line.IndexOf(',', StringComparison.Ordinal);
            if (comma <= 0 || !int.TryParse(line.AsSpan(0, comma), out int tagId))
            {
                continue;
            }

            if (genreMap.Resolve(line[(comma + 1)..]) is { } genre)
            {
                result[tagId] = genre;
            }
        }

        return result;
    }

    private static Dictionary<long, List<(string Genre, long Count)>> AggregateGenresPerBook(
        string bookTagsPath,
        Dictionary<int, string> tagIdToGenre)
    {
        var totals = new Dictionary<long, Dictionary<string, long>>();
        using var reader = new StreamReader(bookTagsPath);
        _ = reader.ReadLine();

        while (reader.ReadLine() is { } line)
        {
            // Fixed three-column numeric file, so a plain split is safe and much faster than a CSV parser.
            string[] parts = line.Split(',');
            if (parts.Length < 3 ||
                !long.TryParse(parts[0], out long goodreadsId) ||
                !int.TryParse(parts[1], out int tagId) ||
                !long.TryParse(parts[2], out long count))
            {
                continue;
            }

            if (!tagIdToGenre.TryGetValue(tagId, out string? genre))
            {
                continue;
            }

            if (!totals.TryGetValue(goodreadsId, out Dictionary<string, long>? perGenre))
            {
                perGenre = [];
                totals[goodreadsId] = perGenre;
            }

            perGenre[genre] = perGenre.GetValueOrDefault(genre) + count;
        }

        return totals.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value
                .OrderByDescending(g => g.Value)
                .ThenBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => (g.Key, g.Value))
                .ToList());
    }

    private static List<BookDocument> ReadBooks(
        string booksPath,
        Dictionary<long, List<(string Genre, long Count)>> genresByGoodreadsId)
    {
        var documents = new List<BookDocument>(10_000);

        using var reader = new StreamReader(booksPath);
        using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);

        // Titles contain commas inside quotes, so this needs a real CSV parser rather than Split.
        csv.Read();
        csv.ReadHeader();

        while (csv.Read())
        {
            string Field(string name) => csv.TryGetField(name, out string? value) ? value ?? string.Empty : string.Empty;

            if (!long.TryParse(Field("book_id"), out long bookId))
            {
                continue;
            }

            _ = long.TryParse(Field("goodreads_book_id"), out long goodreadsId);
            List<(string Genre, long Count)> genres = genresByGoodreadsId.GetValueOrDefault(goodreadsId) ?? [];

            documents.Add(new BookDocument
            {
                Id = $"book-{bookId}",
                Title = Field("title").Trim(),
                Authors = Field("authors")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                Genre = genres.Count > 0 ? genres[0].Genre : string.Empty,
                Genres = genres.Select(g => g.Genre).ToArray(),
                PublicationYear = ParseYear(Field("original_publication_year")),
                AverageRating = ParseDouble(Field("average_rating")),
                RatingsCount = (int)ParseDouble(Field("ratings_count")),
                Language = NormalizeLanguage(Field("language_code")),
            });
        }

        return documents;
    }

    private static int? ParseYear(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double year)
            ? (int)year
            : null;

    private static double ParseDouble(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double result) ? result : 0d;

    private static string? NormalizeLanguage(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        // The dataset mixes 'eng', 'en-US', 'en-GB' and friends; collapse to the base language.
        string trimmed = value.Trim();
        return trimmed.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "en" : trimmed;
    }

    private static void ReportDistribution(List<BookDocument> documents)
    {
        int missing = documents.Count(d => string.IsNullOrEmpty(d.Genre));
        Console.WriteLine($"\nGenre distribution ({documents.Count - missing} classified, {missing} unclassified):");

        foreach (IGrouping<string, BookDocument> group in documents
            .Where(d => !string.IsNullOrEmpty(d.Genre))
            .GroupBy(d => d.Genre)
            .OrderByDescending(g => g.Count()))
        {
            Console.WriteLine($"  {group.Count(),6}  {group.Key}");
        }
    }
}
