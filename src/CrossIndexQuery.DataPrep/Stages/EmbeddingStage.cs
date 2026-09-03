using System.ClientModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.AI.OpenAI;
using Azure.Identity;
using CrossIndexQuery.Core.Models;
using OpenAI.Embeddings;

namespace CrossIndexQuery.DataPrep.Stages;

/// <summary>
/// Embeds every book once, client-side, and writes the vectors into the committed corpus.
/// </summary>
/// <remarks>
/// <para>
/// The alternative — an integrated vectorizer on each index — would embed the same document
/// separately per index. That is fine in production but wrong here: it introduces a second,
/// invisible source of difference between the stripes, and this sample exists to measure a
/// specific one. Embedding once and writing identical bytes everywhere means any divergence in
/// vector results is attributable to the ANN graph, not to the vectors.
/// </para>
/// <para>
/// The model and dimension count go into the manifest so the query path can refuse to run against
/// a corpus embedded with a different model. Silently mismatched embedding spaces produce results
/// that look plausible and are meaningless, which is the worst possible failure mode.
/// </para>
/// </remarks>
internal sealed class EmbeddingStage(string dataDirectory)
{
    private const int BatchSize = 96;

    public async Task<int> RunAsync(
        string endpoint,
        string? apiKey,
        string deployment,
        int dimensions,
        string? blurbModel,
        CancellationToken cancellationToken)
    {
        string basePath = Path.Combine(dataDirectory, "books.base.json");
        if (!File.Exists(basePath))
        {
            Console.Error.WriteLine("data/books.base.json is missing. Run 'prepare' first.");
            return 1;
        }

        List<BookDocument> books;
        await using (FileStream stream = File.OpenRead(basePath))
        {
            books = await JsonSerializer.DeserializeAsync<List<BookDocument>>(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false) ?? [];
        }

        Dictionary<string, string> blurbs = await LoadBlurbsAsync(cancellationToken).ConfigureAwait(false);
        if (blurbs.Count == 0)
        {
            Console.Error.WriteLine("data/books.blurbs.json is missing or empty. Run 'blurbs collect' first.");
            return 1;
        }

        List<BookDocument> ready = [];
        foreach (BookDocument book in books)
        {
            if (blurbs.TryGetValue(book.Id, out string? blurb) && !string.IsNullOrWhiteSpace(blurb))
            {
                book.Blurb = blurb;
                ready.Add(book);
            }
        }

        Console.WriteLine($"{ready.Count:N0} of {books.Count:N0} books have a blurb and will be embedded.");
        if (ready.Count == 0)
        {
            return 1;
        }

        AzureOpenAIClient client = string.IsNullOrWhiteSpace(apiKey)
            ? new AzureOpenAIClient(new Uri(endpoint), new DefaultAzureCredential())
            : new AzureOpenAIClient(new Uri(endpoint), new ApiKeyCredential(apiKey));

        EmbeddingClient embeddings = client.GetEmbeddingClient(deployment);
        var options = new EmbeddingGenerationOptions { Dimensions = dimensions };

        int done = 0;
        for (int offset = 0; offset < ready.Count; offset += BatchSize)
        {
            List<BookDocument> slice = ready.GetRange(offset, Math.Min(BatchSize, ready.Count - offset));
            string[] inputs = [.. slice.Select(b => b.BuildEmbeddingInput())];

            OpenAIEmbeddingCollection response = await SendWithRetryAsync(
                embeddings, inputs, options, cancellationToken).ConfigureAwait(false);

            for (int i = 0; i < slice.Count; i++)
            {
                slice[i].ContentVector = response[i].ToFloats().ToArray();
            }

            done += slice.Count;
            Console.Write($"\r  embedded {done:N0}/{ready.Count:N0}");
        }

        Console.WriteLine();

        string enrichedPath = Path.Combine(dataDirectory, CorpusFile.FileName);
        await CorpusFile.SaveAsync(enrichedPath, ready, cancellationToken).ConfigureAwait(false);

        var manifest = new CorpusManifest
        {
            GeneratedUtc = DateTimeOffset.UtcNow,
            DocumentCount = ready.Count,
            EmbeddingModel = deployment,
            EmbeddingDimensions = dimensions,
            BlurbModel = blurbModel,
            GenreCounts = ready
                .Where(b => !string.IsNullOrWhiteSpace(b.Genre))
                .GroupBy(b => b.Genre, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
        };

        await using (FileStream output = File.Create(Path.Combine(dataDirectory, "corpus-manifest.json")))
        {
            await JsonSerializer.SerializeAsync(
                output, manifest, new JsonSerializerOptions { WriteIndented = true }, cancellationToken)
                .ConfigureAwait(false);
        }

        Console.WriteLine($"Wrote {enrichedPath} ({new FileInfo(enrichedPath).Length / 1024 / 1024:N0} MB) and corpus-manifest.json");
        return 0;
    }

    /// <summary>
    /// Retries on throttling. Embedding ten thousand documents will hit the per-minute token limit
    /// on almost any deployment, so treating 429 as fatal would make the stage unusable.
    /// </summary>
    private static async Task<OpenAIEmbeddingCollection> SendWithRetryAsync(
        EmbeddingClient client,
        string[] inputs,
        EmbeddingGenerationOptions options,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await client.GenerateEmbeddingsAsync(inputs, options, cancellationToken).ConfigureAwait(false);
            }
            catch (ClientResultException ex) when (ex.Status is 429 or >= 500 && attempt < 6)
            {
                TimeSpan delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                Console.Write($"\r  throttled ({ex.Status}); retrying in {delay.TotalSeconds:N0}s   ");
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<Dictionary<string, string>> LoadBlurbsAsync(CancellationToken cancellationToken)
    {
        string path = Path.Combine(dataDirectory, "books.blurbs.json");
        if (!File.Exists(path))
        {
            return [];
        }

        await using FileStream stream = File.OpenRead(path);
        return await JsonSerializer
            .DeserializeAsync<Dictionary<string, string>>(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false) ?? [];
    }
}
