using System.ClientModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.AI.OpenAI;
using Azure.Identity;
using CrossIndexQuery.Core.Configuration;
using CrossIndexQuery.Core.Models;
using OpenAI.Chat;

namespace CrossIndexQuery.DataPrep.Stages;

/// <summary>
/// Scores pooled (query, document) pairs for relevance with an independent judge.
/// </summary>
/// <remarks>
/// <para>
/// Every other number this sample reports measures <em>fidelity to the oracle</em>: how closely a
/// fused result reproduced what a single index returned. That is the right question for "what does
/// striping cost me", and it is the wrong question for "was the single index actually the best
/// answer", because it assumes the thing it would need to prove. Under an oracle-as-truth metric a
/// striped result that surfaces a genuinely better document is scored as an error, since the
/// document is absent from the ground truth by construction.
/// </para>
/// <para>
/// These judgments break that circularity. The candidate pool is the union of the top-k returned by
/// every approach plus the oracle's own top-k, judged once, blind to which approach produced what.
/// The oracle then becomes one more system being measured rather than the definition of correct.
/// </para>
/// <para>
/// The output is committed, for the same reason <c>data/queries.json</c> and the blurbs are: LLM
/// judgments are not reproducible, so a consumer regenerating them would be comparing their judge
/// rather than their search service. Consumers pay nothing and everyone measures against identical
/// ground truth. The stage ships anyway so a different corpus, or an added strategy that widens the
/// pool, can be judged the same way.
/// </para>
/// </remarks>
public sealed class JudgmentStage(string dataDirectory, string batchDirectory, string resultsDirectory)
{
    /// <summary>
    /// Graded relevance on the four-point scale TREC uses.
    /// </summary>
    /// <remarks>
    /// Graded rather than binary because nDCG needs to distinguish "the best available answer" from
    /// "defensible but mediocre". Collapsing to binary would make nDCG degenerate toward recall and
    /// throw away exactly the resolution needed to tell two good rankings apart.
    /// </remarks>
    private const string SystemPrompt =
        """
        You are a search relevance judge. You will be shown a search query and one book. Rate how
        well the book satisfies the query, using this scale:

        3 - Highly relevant. A user issuing this query would be delighted to see this book near the
            top of the results.
        2 - Relevant. Clearly on topic and a reasonable result, but not among the very best answers.
        1 - Marginally relevant. Touches the query's subject but would disappoint as a top result.
        0 - Not relevant. Does not satisfy the query.

        Judge only how well the book matches the query. Do not reward or penalise a book for being
        famous, well written, highly rated, or recent. Do not speculate beyond the description you
        are given.

        Reply with a single digit: 0, 1, 2, or 3. No other text.
        """;

    public async Task<int> SubmitAsync(
        CrossIndexOptions options,
        CancellationToken cancellationToken = default)
    {
        string poolPath = Path.Combine(resultsDirectory, "judgment-pool.json");
        List<PooledCandidate> pool = await ReadPoolsAsync(cancellationToken).ConfigureAwait(false);

        if (pool.Count == 0)
        {
            Console.Error.WriteLine(
                $"No judgment-pool*.json found in {resultsDirectory}. Run 'cli evaluate' first — "
                + "the pool is produced by a run.");
            return 1;
        }

        Dictionary<string, BookDocument> corpus = await LoadCorpusAsync(cancellationToken)
            .ConfigureAwait(false);

        string? deployment = options.Embedding.BlurbDeployment;
        if (string.IsNullOrWhiteSpace(deployment))
        {
            Console.Error.WriteLine("Embedding:BlurbDeployment is not configured; it names the judge model.");
            return 1;
        }

        Directory.CreateDirectory(batchDirectory);
        string requestsPath = Path.Combine(batchDirectory, "judgments.requests.jsonl");

        // Judgments already collected are kept. A (query, document) grade does not depend on which
        // run surfaced the document, so re-judging a pair would pay twice for the same number and,
        // because the judge is not deterministic, would also quietly change results computed
        // earlier.
        Dictionary<string, Dictionary<string, int>> existing =
            await LoadJudgmentsAsync(cancellationToken).ConfigureAwait(false);

        var lines = new List<string>();
        int missing = 0;
        int alreadyJudged = 0;

        foreach (PooledCandidate candidate in pool)
        {
            existing.TryGetValue(candidate.QueryId, out Dictionary<string, int>? judged);

            foreach (string documentId in candidate.DocumentIds)
            {
                if (judged is not null && judged.ContainsKey(documentId))
                {
                    alreadyJudged++;
                    continue;
                }

                if (!corpus.TryGetValue(documentId, out BookDocument? book))
                {
                    missing++;
                    continue;
                }

                lines.Add(BuildRequest(candidate, book, deployment));
            }
        }

        if (alreadyJudged > 0)
        {
            Console.WriteLine($"Skipped {alreadyJudged:N0} pairs that already have judgments.");
        }

        if (missing > 0)
        {
            Console.WriteLine($"Skipped {missing} pooled ids not present in the corpus.");
        }

        if (lines.Count == 0)
        {
            Console.WriteLine("Every pooled pair is already judged. Nothing to submit.");
            return 0;
        }

        await File.WriteAllLinesAsync(
            requestsPath, lines, AzureOpenAiBatchClient.JsonlEncoding, cancellationToken)
            .ConfigureAwait(false);

        Console.WriteLine($"Wrote {lines.Count:N0} judgment requests to {requestsPath}.");

        using var client = new AzureOpenAiBatchClient(options.Embedding.Endpoint, options.Embedding.ApiKey);

        string fileId = await client.UploadAsync(requestsPath, cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"Uploaded as {fileId}.");

        JsonNode batch = await client.CreateBatchAsync(fileId, cancellationToken).ConfigureAwait(false);
        string batchId = batch["id"]!.GetValue<string>();

        await File.WriteAllTextAsync(
            Path.Combine(batchDirectory, "judgments.job.json"),
            AzureOpenAiBatchClient.Pretty(batch),
            cancellationToken).ConfigureAwait(false);

        Console.WriteLine($"Created batch {batchId} ({batch["status"]}).");
        Console.WriteLine("Poll with: dotnet run --project src\\CrossIndexQuery.DataPrep -- judge status");
        return 0;
    }

    public async Task<int> StatusAsync(
        CrossIndexOptions options,
        CancellationToken cancellationToken = default)
    {
        string? batchId = await ReadBatchIdAsync(cancellationToken).ConfigureAwait(false);
        if (batchId is null)
        {
            return 1;
        }

        using var client = new AzureOpenAiBatchClient(options.Embedding.Endpoint, options.Embedding.ApiKey);
        JsonNode batch = await client.GetBatchAsync(batchId, cancellationToken).ConfigureAwait(false);

        JsonNode? counts = batch["request_counts"];
        Console.WriteLine($"batch     {batchId}");
        Console.WriteLine($"status    {batch["status"]}");
        Console.WriteLine(
            $"requests  {counts?["completed"]} completed, {counts?["failed"]} failed, {counts?["total"]} total");

        return 0;
    }

    public async Task<int> CollectAsync(
        CrossIndexOptions options,
        CancellationToken cancellationToken = default)
    {
        string? batchId = await ReadBatchIdAsync(cancellationToken).ConfigureAwait(false);
        if (batchId is null)
        {
            return 1;
        }

        using var client = new AzureOpenAiBatchClient(options.Embedding.Endpoint, options.Embedding.ApiKey);
        JsonNode batch = await client.GetBatchAsync(batchId, cancellationToken).ConfigureAwait(false);

        string status = batch["status"]?.GetValue<string>() ?? "unknown";
        if (status != "completed")
        {
            Console.Error.WriteLine($"Batch {batchId} is '{status}', not 'completed'.");
            return 1;
        }

        string outputFileId = batch["output_file_id"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Completed batch has no output_file_id.");

        string raw = await client.DownloadFileAsync(outputFileId, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Combine(batchDirectory, "judgments.results.jsonl"), raw, cancellationToken)
            .ConfigureAwait(false);

        var judgments = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        int parsed = 0;
        int failed = 0;
        long promptTokens = 0;
        long completionTokens = 0;

        // Merge into whatever is already on disk rather than replacing it, so an incremental batch
        // extends the judged set instead of narrowing it to just the newest pairs.
        foreach ((string queryId, Dictionary<string, int> grades)
            in await LoadJudgmentsAsync(cancellationToken).ConfigureAwait(false))
        {
            judgments[queryId] = new Dictionary<string, int>(grades, StringComparer.Ordinal);
        }

        int carriedOver = judgments.Sum(kv => kv.Value.Count);

        foreach (string line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            JsonNode? node = JsonNode.Parse(line);
            string? customId = node?["custom_id"]?.GetValue<string>();
            JsonNode? body = node?["response"]?["body"];
            string? text = body?["choices"]?[0]?["message"]?["content"]?.GetValue<string>();

            promptTokens += body?["usage"]?["prompt_tokens"]?.GetValue<int>() ?? 0;
            completionTokens += body?["usage"]?["completion_tokens"]?.GetValue<int>() ?? 0;

            if (customId is null || !TrySplitCustomId(customId, out string queryId, out string documentId)
                || !TryParseGrade(text, out int grade))
            {
                failed++;
                continue;
            }

            if (!judgments.TryGetValue(queryId, out Dictionary<string, int>? perQuery))
            {
                perQuery = new Dictionary<string, int>(StringComparer.Ordinal);
                judgments[queryId] = perQuery;
            }

            perQuery[documentId] = grade;
            parsed++;
        }

        string path = Path.Combine(dataDirectory, "judgments.json");
        await using (FileStream output = File.Create(path))
        {
            await JsonSerializer.SerializeAsync(
                output,
                judgments.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                    .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
                new JsonSerializerOptions { WriteIndented = true },
                cancellationToken).ConfigureAwait(false);
        }

        Console.WriteLine($"Parsed {parsed:N0} new judgments ({failed:N0} failed).");
        Console.WriteLine(
            $"Total {judgments.Sum(kv => kv.Value.Count):N0} judgments over {judgments.Count:N0} queries "
            + $"({carriedOver:N0} carried over).");
        Console.WriteLine($"Tokens: {promptTokens:N0} prompt, {completionTokens:N0} completion.");
        Console.WriteLine($"Wrote {path}.");

        ReportDistribution(judgments);
        return 0;
    }

    private static void ReportDistribution(Dictionary<string, Dictionary<string, int>> judgments)
    {
        var histogram = new int[4];
        foreach (Dictionary<string, int> perQuery in judgments.Values)
        {
            foreach (int grade in perQuery.Values)
            {
                histogram[Math.Clamp(grade, 0, 3)]++;
            }
        }

        int total = histogram.Sum();
        if (total == 0)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine("Grade distribution:");
        for (int grade = 3; grade >= 0; grade--)
        {
            Console.WriteLine(
                $"  {grade}  {histogram[grade],6:N0}  {histogram[grade] / (double)total:P1}");
        }

        // A pool where almost everything is relevant cannot separate the approaches, and one where
        // almost nothing is means the retrieval never had a chance. Either extreme invalidates the
        // comparison, so the shape of this distribution is worth seeing before trusting any number
        // computed from it.
        double relevant = (histogram[2] + histogram[3]) / (double)total;
        Console.WriteLine($"  relevant (2-3): {relevant:P1}");
    }

    /// <summary>
    /// Builds one judging request.
    /// </summary>
    /// <remarks>
    /// The prompt carries the query and the document and nothing else. No strategy name, no rank, no
    /// index of origin — a judge that can tell which system retrieved a document can prefer one, and
    /// pooling exists precisely to obtain judgments that none of the compared approaches influenced.
    /// </remarks>
    private static string BuildRequest(PooledCandidate candidate, BookDocument book, string deployment)
    {
        string authors = book.Authors.Length > 0 ? string.Join(", ", book.Authors) : "unknown";

        var userPrompt =
            $"""
             Query: {candidate.QueryText}

             Book
             Title: {book.Title}
             Author(s): {authors}
             Description: {book.Blurb}
             """;

        var request = new JsonObject
        {
            ["custom_id"] = $"{candidate.QueryId}~{book.Id}",
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

    private static bool TrySplitCustomId(string customId, out string queryId, out string documentId)
    {
        int separator = customId.IndexOf('~', StringComparison.Ordinal);
        if (separator <= 0 || separator == customId.Length - 1)
        {
            queryId = string.Empty;
            documentId = string.Empty;
            return false;
        }

        queryId = customId[..separator];
        documentId = customId[(separator + 1)..];
        return true;
    }

    /// <summary>
    /// Reads the grade out of the reply.
    /// </summary>
    /// <remarks>
    /// The first digit in range is taken rather than requiring the whole reply to be one character,
    /// because a judge that occasionally answers "Rating: 2" should not be discarded. Anything with
    /// no digit at all is counted as a failure rather than defaulted, since a silent default would
    /// quietly become a relevance judgment nobody made.
    /// </remarks>
    private static bool TryParseGrade(string? text, out int grade)
    {
        grade = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (char c in text)
        {
            if (c is >= '0' and <= '3')
            {
                grade = c - '0';
                return true;
            }

            if (char.IsDigit(c))
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Re-judges a sample of already-graded pairs with a second model and reports agreement.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The judge and the corpus came from the same model family, so the judge is grading text its
    /// own family wrote. That is a real threat to every number derived from these judgments, and
    /// naming it in a limitations section is weaker than measuring it.
    /// </para>
    /// <para>
    /// Note what a uniform bias would and would not do. Every document in this corpus carries a
    /// description from the same generator, so a judge that systematically over-rates that style
    /// over-rates every document equally — and since every strategy draws from the same pool, a
    /// uniform effect cancels in the comparison between strategies. The threat that survives is a
    /// bias that correlates with whatever distinguishes the strategies. Agreement statistics detect
    /// the first; only recomputing the strategy comparisons under the second judge detects the
    /// second, which is why this writes its grades out rather than only printing a kappa.
    /// </para>
    /// </remarks>
    public async Task<int> AgreementAsync(
        CrossIndexOptions options,
        int sampleSize,
        int maxConcurrency,
        CancellationToken cancellationToken = default)
    {
        Dictionary<string, Dictionary<string, int>> primary =
            await LoadJudgmentsAsync(cancellationToken).ConfigureAwait(false);

        if (primary.Count == 0)
        {
            Console.Error.WriteLine("No data/judgments.json found. Run 'judge collect' first.");
            return 1;
        }

        Dictionary<string, BookDocument> corpus = await LoadCorpusAsync(cancellationToken)
            .ConfigureAwait(false);

        Dictionary<string, string> queryText = await LoadQueryTextAsync(cancellationToken)
            .ConfigureAwait(false);

        // Sampled deterministically so the check is reproducible, and spread evenly across the pool
        // rather than taken from the front, which would over-weight whichever queries happen to
        // sort first.
        List<(string QueryId, string DocumentId, int Grade)> all =
        [
            .. primary
                .OrderBy(q => q.Key, StringComparer.Ordinal)
                .SelectMany(q => q.Value
                    .OrderBy(d => d.Key, StringComparer.Ordinal)
                    .Select(d => (q.Key, d.Key, d.Value)))
        ];

        int take = Math.Min(sampleSize, all.Count);
        double stride = all.Count / (double)take;

        List<(string QueryId, string DocumentId, int Grade)> sample =
            [.. Enumerable.Range(0, take).Select(i => all[(int)(i * stride)])];

        Console.WriteLine(
            $"Re-judging {sample.Count:N0} of {all.Count:N0} pairs with '{options.Embedding.RerankDeployment}'.");

        var client = new AzureOpenAIClient(
                new Uri(options.Embedding.Endpoint), new DefaultAzureCredential())
            .GetChatClient(options.Embedding.RerankDeployment);

        var second = new int[sample.Count];
        var failed = 0;
        using var throttle = new SemaphoreSlim(maxConcurrency);

        await Task.WhenAll(sample.Select(async (pair, index) =>
        {
            await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!corpus.TryGetValue(pair.DocumentId, out BookDocument? book)
                    || !queryText.TryGetValue(pair.QueryId, out string? text))
                {
                    second[index] = -1;
                    return;
                }

                second[index] = await GradeLiveAsync(client, text, book, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                throttle.Release();
            }
        })).ConfigureAwait(false);

        var pairs = new List<(int First, int Second)>();
        var secondJudgments = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);

        for (int i = 0; i < sample.Count; i++)
        {
            if (second[i] < 0)
            {
                failed++;
                continue;
            }

            pairs.Add((sample[i].Grade, second[i]));

            if (!secondJudgments.TryGetValue(sample[i].QueryId, out Dictionary<string, int>? map))
            {
                map = new Dictionary<string, int>(StringComparer.Ordinal);
                secondJudgments[sample[i].QueryId] = map;
            }

            map[sample[i].DocumentId] = second[i];
        }

        string path = Path.Combine(dataDirectory, "judgments.second-judge.json");
        await using (FileStream stream = File.Create(path))
        {
            await JsonSerializer.SerializeAsync(
                stream, secondJudgments, new JsonSerializerOptions { WriteIndented = true },
                cancellationToken).ConfigureAwait(false);
        }

        ReportAgreement(pairs, failed, path);
        return 0;
    }

    /// <summary>
    /// Reports how far two judges agree, on the measures that matter for a graded relevance scale.
    /// </summary>
    /// <remarks>
    /// Exact agreement alone is misleading on an ordinal scale, because confusing "highly relevant"
    /// with "relevant" is a much smaller error than confusing it with "irrelevant" and raw agreement
    /// treats them alike. Quadratically weighted kappa charges for the size of the disagreement and
    /// corrects for the agreement that chance alone would produce, which is why it is the headline
    /// number here.
    /// </remarks>
    private static void ReportAgreement(
        IReadOnlyList<(int First, int Second)> pairs,
        int failed,
        string path)
    {
        if (pairs.Count == 0)
        {
            Console.Error.WriteLine("No comparable judgments were produced.");
            return;
        }

        int n = pairs.Count;
        int exact = pairs.Count(p => p.First == p.Second);
        int adjacent = pairs.Count(p => Math.Abs(p.First - p.Second) <= 1);

        double meanFirst = pairs.Average(p => (double)p.First);
        double meanSecond = pairs.Average(p => (double)p.Second);

        // Quadratically weighted Cohen's kappa.
        var observed = new double[4, 4];
        var firstCounts = new double[4];
        var secondCounts = new double[4];

        foreach ((int first, int secondGrade) in pairs)
        {
            observed[first, secondGrade]++;
            firstCounts[first]++;
            secondCounts[secondGrade]++;
        }

        double numerator = 0;
        double denominator = 0;

        for (int i = 0; i < 4; i++)
        {
            for (int j = 0; j < 4; j++)
            {
                double weight = Math.Pow(i - j, 2) / 9d;
                double expected = firstCounts[i] * secondCounts[j] / n;

                numerator += weight * observed[i, j];
                denominator += weight * expected;
            }
        }

        double kappa = denominator > double.Epsilon ? 1 - (numerator / denominator) : 0;

        // Pearson correlation over the grades, which on a four-point scale is close enough to
        // Spearman to serve the same purpose and is not distorted by the heavy ties that ranking
        // four values inevitably produces.
        double covariance = pairs.Sum(p => (p.First - meanFirst) * (p.Second - meanSecond));
        double varianceFirst = pairs.Sum(p => Math.Pow(p.First - meanFirst, 2));
        double varianceSecond = pairs.Sum(p => Math.Pow(p.Second - meanSecond, 2));

        double correlation = varianceFirst > 0 && varianceSecond > 0
            ? covariance / Math.Sqrt(varianceFirst * varianceSecond)
            : 0;

        Console.WriteLine();
        Console.WriteLine($"Compared {n:N0} pairs ({failed:N0} skipped).");
        Console.WriteLine($"  exact agreement        {exact / (double)n:P1}");
        Console.WriteLine($"  within one grade       {adjacent / (double)n:P1}");
        Console.WriteLine($"  weighted kappa         {kappa:F3}");
        Console.WriteLine($"  correlation            {correlation:F3}");
        Console.WriteLine($"  mean grade  primary    {meanFirst:F3}");
        Console.WriteLine($"  mean grade  second     {meanSecond:F3}");
        Console.WriteLine($"  mean shift             {meanSecond - meanFirst:+0.000;-0.000}");
        Console.WriteLine();
        Console.WriteLine("Confusion (rows: primary judge, columns: second judge)");
        Console.WriteLine($"        {"0",6}{"1",6}{"2",6}{"3",6}");

        for (int i = 0; i < 4; i++)
        {
            Console.WriteLine(
                $"  {i,3}   {observed[i, 0],6:N0}{observed[i, 1],6:N0}{observed[i, 2],6:N0}{observed[i, 3],6:N0}");
        }

        Console.WriteLine();
        Console.WriteLine($"Wrote {path}.");
        Console.WriteLine(
            "A mean shift near zero means the two judges disagree about individual documents without "
            + "one being systematically more generous — which is the pattern that leaves relative "
            + "comparisons between strategies intact.");
    }

    private async Task<Dictionary<string, string>> LoadQueryTextAsync(CancellationToken cancellationToken)
    {
        string path = Path.Combine(dataDirectory, "queries.json");
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        if (!File.Exists(path))
        {
            return map;
        }

        await using FileStream stream = File.OpenRead(path);
        using JsonDocument document = await JsonDocument
            .ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        foreach (JsonElement element in document.RootElement.EnumerateArray())
        {
            if (element.TryGetProperty("id", out JsonElement id)
                && element.TryGetProperty("text", out JsonElement text))
            {
                map[id.GetString() ?? string.Empty] = text.GetString() ?? string.Empty;
            }
        }

        return map;
    }

    private static async Task<int> GradeLiveAsync(
        ChatClient client,
        string query,
        BookDocument book,
        CancellationToken cancellationToken)
    {
        string authors = book.Authors.Length > 0 ? string.Join(", ", book.Authors) : "unknown";

        var user =
            $"""
             Query: {query}

             Book
             Title: {book.Title}
             Author(s): {authors}
             Description: {book.Blurb}
             """;

        try
        {
            ClientResult<ChatCompletion> result = await client.CompleteChatAsync(
                [new SystemChatMessage(SystemPrompt), new UserChatMessage(user)],
                new ChatCompletionOptions(),
                cancellationToken).ConfigureAwait(false);

            string text = result.Value.Content.Count > 0 ? result.Value.Content[0].Text : string.Empty;
            return TryParseGrade(text, out int grade) ? grade : -1;
        }
        catch (ClientResultException)
        {
            return -1;
        }
    }

    private async Task<Dictionary<string, Dictionary<string, int>>> LoadJudgmentsAsync(
        CancellationToken cancellationToken)
    {
        string path = Path.Combine(dataDirectory, "judgments.json");

        if (!File.Exists(path))
        {
            return new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        }

        await using FileStream stream = File.OpenRead(path);

        return await JsonSerializer
            .DeserializeAsync<Dictionary<string, Dictionary<string, int>>>(
                stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false)
            ?? new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
    }

    private async Task<string?> ReadBatchIdAsync(CancellationToken cancellationToken)
    {
        string path = Path.Combine(batchDirectory, "judgments.job.json");
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"{path} not found. Run 'judge submit' first.");
            return null;
        }

        JsonNode? node = JsonNode.Parse(
            await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));

        return node?["id"]?.GetValue<string>();
    }

    /// <summary>
    /// Unions every pool file in the results directory.
    /// </summary>
    /// <remarks>
    /// Each evaluation run writes its own pool, because a semantic run and a lexical one surface
    /// different documents and judging only one of them would leave the other's results partly
    /// unjudged. Unioning also means adding a strategy and re-running extends the judged set instead
    /// of invalidating it.
    /// </remarks>
    private async Task<List<PooledCandidate>> ReadPoolsAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(resultsDirectory))
        {
            return [];
        }

        var merged = new Dictionary<string, (string Text, SortedSet<string> Ids)>(StringComparer.Ordinal);
        var serializerOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        foreach (string file in Directory
            .EnumerateFiles(resultsDirectory, "judgment-pool*.json")
            .Order(StringComparer.Ordinal))
        {
            await using FileStream stream = File.OpenRead(file);
            List<PooledCandidate> pool = await JsonSerializer
                .DeserializeAsync<List<PooledCandidate>>(stream, serializerOptions, cancellationToken)
                .ConfigureAwait(false) ?? [];

            foreach (PooledCandidate candidate in pool)
            {
                if (!merged.TryGetValue(candidate.QueryId, out (string Text, SortedSet<string> Ids) entry))
                {
                    entry = (candidate.QueryText, new SortedSet<string>(StringComparer.Ordinal));
                    merged[candidate.QueryId] = entry;
                }

                foreach (string id in candidate.DocumentIds)
                {
                    entry.Ids.Add(id);
                }
            }

            Console.WriteLine($"  pooled {Path.GetFileName(file)}: {pool.Count} queries");
        }

        return
        [
            .. merged
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new PooledCandidate(kv.Key, kv.Value.Text, [.. kv.Value.Ids]))
        ];
    }

    private async Task<Dictionary<string, BookDocument>> LoadCorpusAsync(CancellationToken cancellationToken)
    {
        string path = Path.Combine(dataDirectory, CorpusFile.FileName);
        List<BookDocument> books = await CorpusFile.LoadAsync(path, cancellationToken).ConfigureAwait(false);
        return books.ToDictionary(b => b.Id, StringComparer.Ordinal);
    }

    /// <summary>One query and the documents pooled for it, as written by the harness.</summary>
    private sealed record PooledCandidate(string QueryId, string QueryText, IReadOnlyList<string> DocumentIds);
}
