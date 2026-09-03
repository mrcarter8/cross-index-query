using System.Text.Json;
using System.Text.Json.Serialization;

namespace CrossIndexQuery.Core.Models;

/// <summary>
/// Single entry point for reading and writing <c>data/books.enriched.json</c>.
/// </summary>
/// <remarks>
/// The vector encoding lives here rather than as an attribute on
/// <see cref="BookDocument.ContentVector"/> deliberately. The same type is handed to the Azure AI
/// Search SDK for upload, and that path must serialise vectors as ordinary JSON numbers so the
/// service receives a <c>Collection(Edm.Single)</c>. Scoping the converter to these options keeps
/// the compact on-disk form from ever reaching the wire.
/// </remarks>
public static class CorpusFile
{
    public const string FileName = "books.enriched.json";

    private static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        options.Converters.Add(new QuantizedVectorConverter());
        return options;
    }

    public static async Task<List<BookDocument>> LoadAsync(
        string path, CancellationToken cancellationToken = default)
    {
        await using FileStream stream = File.OpenRead(path);
        return await JsonSerializer
            .DeserializeAsync<List<BookDocument>>(stream, Options, cancellationToken)
            .ConfigureAwait(false) ?? [];
    }

    public static async Task SaveAsync(
        string path, IReadOnlyList<BookDocument> books, CancellationToken cancellationToken = default)
    {
        await using FileStream stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, books, Options, cancellationToken).ConfigureAwait(false);
    }
}
