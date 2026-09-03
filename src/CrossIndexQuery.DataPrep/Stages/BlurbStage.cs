using System.Text.Json;
using System.Text.Json.Nodes;
using CrossIndexQuery.Core.Models;

namespace CrossIndexQuery.DataPrep.Stages;

/// <summary>
/// Generates the per-book description that goodbooks-10k does not ship.
/// </summary>
/// <remarks>
/// <para>
/// Without a body of text there is nothing to demonstrate: BM25 over a bare title is degenerate,
/// and an embedding of a title alone carries too little signal to separate ten thousand documents.
/// The blurb gives every document a paragraph of distinctive vocabulary, which is what makes
/// keyword, vector, and hybrid retrieval behave differently enough to be worth comparing.
/// </para>
/// <para>
/// Output is committed to the repository. Consumers of the sample pay nothing to reproduce it, and
/// — more importantly — everyone evaluates against byte-identical text, so published relevance
/// numbers are actually comparable.
/// </para>
/// </remarks>
internal sealed class BlurbStage
{
    private const string SystemPrompt =
        """
        You write catalogue descriptions for a library search system.

        Given a book's metadata, write a single paragraph of 100-140 words describing it.

        Rules:
        - Describe subject matter: premise, setting, central conflict or argument, and themes.
        - Write in third person, present tense, neutral and specific.
        - Use concrete, distinctive nouns. This text is the only searchable content for the book,
          so vague phrasing makes the book unfindable.
        - Do not open with the title, "This book", "In this", or the author's name.
        - No marketing language, no ratings, no review quotes, no comparisons to other books.
        - No spoilers beyond what jacket copy would reveal.
        - If you are not confident about the specific plot, describe the book at the level you are
          confident about - its subject, genre conventions, and setting - rather than inventing
          specific characters or events.
        - Output only the paragraph. No preamble, no title, no quotation marks.
        """;

    private readonly string _dataDirectory;
    private readonly string _batchDirectory;

    public BlurbStage(string dataDirectory)
    {
        _dataDirectory = dataDirectory;
        _batchDirectory = Path.Combine(dataDirectory, "batch");
    }

    public async Task<int> SubmitAsync(
        string endpoint,
        string? apiKey,
        string deployment,
        int limit,
        string jobName,
        CancellationToken cancellationToken)
    {
        List<BookDocument> books = await LoadBaseCorpusAsync(cancellationToken).ConfigureAwait(false);
        if (books.Count == 0)
        {
            Console.Error.WriteLine("data/books.base.json is missing or empty. Run 'prepare' first.");
            return 1;
        }

        Dictionary<string, string> existing = await LoadExistingBlurbsAsync(cancellationToken).ConfigureAwait(false);

        // Sampling evenly across the corpus rather than taking the first N keeps a smoke test
        // representative: the first rows of goodbooks-10k are all blockbuster fiction.
        List<BookDocument> pending = [.. books.Where(b => !existing.ContainsKey(b.Id))];
        if (limit > 0 && limit < pending.Count)
        {
            int stride = pending.Count / limit;
            pending = [.. pending.Where((_, i) => i % stride == 0).Take(limit)];
        }

        if (pending.Count == 0)
        {
            Console.WriteLine("Every book already has a blurb. Nothing to submit.");
            return 0;
        }

        Directory.CreateDirectory(_batchDirectory);
        string requestPath = Path.Combine(_batchDirectory, $"{jobName}.requests.jsonl");

        await using (var writer = new StreamWriter(requestPath, append: false, AzureOpenAiBatchClient.JsonlEncoding))
        {
            foreach (BookDocument book in pending)
            {
                await writer.WriteLineAsync(BuildRequestLine(book, deployment)).ConfigureAwait(false);
            }
        }

        Console.WriteLine($"Wrote {pending.Count:N0} requests to {requestPath}");

        using var client = new AzureOpenAiBatchClient(endpoint, apiKey);

        Console.WriteLine("Uploading...");
        string fileId = await client.UploadAsync(requestPath, cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"  file id: {fileId}");

        Console.WriteLine("Creating batch job...");
        JsonNode batch = await client.CreateBatchAsync(fileId, cancellationToken).ConfigureAwait(false);

        string jobPath = Path.Combine(_batchDirectory, $"{jobName}.job.json");
        await File.WriteAllTextAsync(jobPath, AzureOpenAiBatchClient.Pretty(batch), cancellationToken).ConfigureAwait(false);

        Console.WriteLine($"  batch id: {batch["id"]}");
        Console.WriteLine($"  status:   {batch["status"]}");
        Console.WriteLine($"  saved to  {jobPath}");
        Console.WriteLine($"\nCheck with: cross-index-dataprep blurbs status --job {jobName}");
        return 0;
    }

    public async Task<int> StatusAsync(string endpoint, string? apiKey, string jobName, CancellationToken cancellationToken)
    {
        JsonNode? job = await LoadJobAsync(jobName, cancellationToken).ConfigureAwait(false);
        if (job is null)
        {
            return 1;
        }

        using var client = new AzureOpenAiBatchClient(endpoint, apiKey);
        JsonNode batch = await client.GetBatchAsync(job["id"]!.GetValue<string>(), cancellationToken).ConfigureAwait(false);

        Console.WriteLine($"status:  {batch["status"]}");
        Console.WriteLine($"counts:  {batch["request_counts"]?.ToJsonString()}");
        if (batch["errors"] is { } errors)
        {
            Console.WriteLine($"errors:  {errors.ToJsonString()}");
        }

        await File.WriteAllTextAsync(
            Path.Combine(_batchDirectory, $"{jobName}.job.json"),
            AzureOpenAiBatchClient.Pretty(batch),
            cancellationToken).ConfigureAwait(false);

        return 0;
    }

    public async Task<int> CollectAsync(string endpoint, string? apiKey, string jobName, CancellationToken cancellationToken)
    {
        JsonNode? job = await LoadJobAsync(jobName, cancellationToken).ConfigureAwait(false);
        if (job is null)
        {
            return 1;
        }

        using var client = new AzureOpenAiBatchClient(endpoint, apiKey);
        JsonNode batch = await client.GetBatchAsync(job["id"]!.GetValue<string>(), cancellationToken).ConfigureAwait(false);

        string status = batch["status"]?.GetValue<string>() ?? "unknown";
        if (!string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"Batch is '{status}', not 'completed'. Nothing to collect yet.");
            return 1;
        }

        if (batch["error_file_id"]?.GetValue<string>() is { Length: > 0 } errorFileId)
        {
            string errorText = await client.DownloadFileAsync(errorFileId, cancellationToken).ConfigureAwait(false);
            string errorPath = Path.Combine(_batchDirectory, $"{jobName}.errors.jsonl");
            await File.WriteAllTextAsync(errorPath, errorText, cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"Wrote failures to {errorPath}");
        }

        string outputFileId = batch["output_file_id"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Completed batch has no output_file_id.");

        string raw = await client.DownloadFileAsync(outputFileId, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Combine(_batchDirectory, $"{jobName}.results.jsonl"),
            raw,
            cancellationToken).ConfigureAwait(false);

        Dictionary<string, string> blurbs = await LoadExistingBlurbsAsync(cancellationToken).ConfigureAwait(false);
        int added = 0;
        int failed = 0;

        foreach (string line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            JsonNode? node = JsonNode.Parse(line);
            string? customId = node?["custom_id"]?.GetValue<string>();
            string? text = node?["response"]?["body"]?["choices"]?[0]?["message"]?["content"]?.GetValue<string>();

            if (customId is null || string.IsNullOrWhiteSpace(text))
            {
                failed++;
                continue;
            }

            blurbs[customId] = text.Trim();
            added++;
        }

        string blurbPath = Path.Combine(_dataDirectory, "books.blurbs.json");
        await using (FileStream output = File.Create(blurbPath))
        {
            await JsonSerializer.SerializeAsync(
                output,
                blurbs.OrderBy(kvp => kvp.Key, StringComparer.Ordinal).ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
                new JsonSerializerOptions { WriteIndented = false },
                cancellationToken).ConfigureAwait(false);
        }

        Console.WriteLine($"Collected {added:N0} blurbs ({failed:N0} unusable). Total on disk: {blurbs.Count:N0}");
        Console.WriteLine($"Wrote {blurbPath}");
        return 0;
    }

    private static string BuildRequestLine(BookDocument book, string deployment)
    {
        string authors = book.Authors.Length > 0 ? string.Join(", ", book.Authors) : "unknown";
        string genres = book.Genres.Length > 0 ? string.Join(", ", book.Genres.Take(3)) : "unclassified";
        string year = book.PublicationYear?.ToString() ?? "unknown";

        var userPrompt =
            $"""
             Title: {book.Title}
             Author(s): {authors}
             First published: {year}
             Genre(s): {genres}
             """;

        var request = new JsonObject
        {
            ["custom_id"] = book.Id,
            ["method"] = "POST",
            ["url"] = "/chat/completions",
            ["body"] = new JsonObject
            {
                ["model"] = deployment,
                ["max_completion_tokens"] = 2048,
                ["reasoning_effort"] = "low",
                ["messages"] = new JsonArray
                {
                    new JsonObject { ["role"] = "system", ["content"] = SystemPrompt },
                    new JsonObject { ["role"] = "user", ["content"] = userPrompt },
                },
            },
        };

        return request.ToJsonString();
    }

    private async Task<List<BookDocument>> LoadBaseCorpusAsync(CancellationToken cancellationToken)
    {
        string path = Path.Combine(_dataDirectory, "books.base.json");
        if (!File.Exists(path))
        {
            return [];
        }

        await using FileStream stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<List<BookDocument>>(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false) ?? [];
    }

    private async Task<Dictionary<string, string>> LoadExistingBlurbsAsync(CancellationToken cancellationToken)
    {
        string path = Path.Combine(_dataDirectory, "books.blurbs.json");
        if (!File.Exists(path))
        {
            return [];
        }

        await using FileStream stream = File.OpenRead(path);
        return await JsonSerializer
            .DeserializeAsync<Dictionary<string, string>>(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false) ?? [];
    }

    private async Task<JsonNode?> LoadJobAsync(string jobName, CancellationToken cancellationToken)
    {
        string path = Path.Combine(_batchDirectory, $"{jobName}.job.json");
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"No job file at {path}. Submit the job first.");
            return null;
        }

        return JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));
    }
}
