using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using CrossIndexQuery.Core.Clients;
using CrossIndexQuery.Core.Configuration;
using CrossIndexQuery.Core.Models;

namespace CrossIndexQuery.Core.Indexing;

/// <summary>
/// Creates the three indexes and loads the corpus into them.
/// </summary>
/// <remarks>
/// <para>
/// Two stripes hold a disjoint half of the corpus each; the oracle holds all of it. The oracle is
/// the point of the sample. Without a single index containing everything, "how much relevance did
/// striping cost?" has no answer — you can compare fusion strategies to each other but not to the
/// result the customer would have had if the data fit.
/// </para>
/// <para>
/// Nothing here enumerates indexes. Serverless services reject unpaged enumeration outright, and
/// addressing indexes by name is the correct habit regardless of tier.
/// </para>
/// </remarks>
public sealed class IndexProvisioner(SearchClientFactory factory, SearchServiceOptions options)
{
    private const int UploadBatchSize = 500;

    /// <summary>
    /// How long to keep retrying a create that collides with an in-flight delete.
    /// </summary>
    /// <remarks>
    /// Index deletion is asynchronous. <c>DeleteIndexAsync</c> returns as soon as the request is
    /// accepted, but the name stays reserved until the delete completes, and creating it in that
    /// window fails with 409 <c>ResourceNameAlreadyInUse</c> — reported as "already exists and is
    /// currently being deleted", which reads like a naming conflict rather than a race. Deletes here
    /// have been observed to take a few seconds.
    /// </remarks>
    private static readonly TimeSpan DeleteSettleTimeout = TimeSpan.FromSeconds(90);

    public async Task CreateIndexesAsync(int vectorDimensions, CancellationToken cancellationToken = default)
    {
        SearchIndexClient client = factory.CreateIndexClient();

        foreach (string name in AllIndexes())
        {
            SearchIndex definition = BookIndexSchema.Create(name, vectorDimensions);
            await CreateWhenNameIsFreeAsync(client, definition, cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"  index ready: {name}");
        }
    }

    /// <summary>
    /// Creates an index, waiting out a delete that has been accepted but not yet applied.
    /// </summary>
    /// <remarks>
    /// Only the name-reservation conflict is retried. Any other failure is a real problem and is
    /// allowed to surface immediately rather than being retried until the timeout expires.
    /// </remarks>
    private static async Task CreateWhenNameIsFreeAsync(
        SearchIndexClient client,
        SearchIndex definition,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + DeleteSettleTimeout;
        var delay = TimeSpan.FromSeconds(2);

        while (true)
        {
            try
            {
                await client.CreateOrUpdateIndexAsync(definition, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            catch (RequestFailedException ex)
                when (ex.Status == 409 && DateTimeOffset.UtcNow < deadline)
            {
                Console.WriteLine(
                    $"  waiting for '{definition.Name}' to finish deleting ({(int)delay.TotalSeconds}s)...");

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 1.5, 10));
            }
        }
    }

    public async Task DeleteIndexesAsync(CancellationToken cancellationToken = default)
    {
        SearchIndexClient client = factory.CreateIndexClient();

        foreach (string name in AllIndexes())
        {
            try
            {
                await client.DeleteIndexAsync(name, cancellationToken).ConfigureAwait(false);
                Console.WriteLine($"  deleted: {name}");
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                Console.WriteLine($"  not present: {name}");
            }
        }
    }

    /// <summary>
    /// Routes each document to a stripe and uploads it there, and uploads every document to the
    /// oracle. Stripe assignment is recorded on the document in all three indexes.
    /// </summary>
    /// <param name="skipOracle">
    /// Leaves the oracle untouched. The oracle holds the whole corpus regardless of how the stripes
    /// are cut, so when sweeping several stripe configurations against one baseline it is the same
    /// index every time. Re-uploading it per configuration is not merely wasted work — on a
    /// serverless service the repeated bulk load throttles subsequent queries into 503s.
    /// </param>
    public async Task<UploadSummary> UploadAsync(
        IReadOnlyList<BookDocument> books,
        StripeRouter router,
        bool skipOracle = false,
        CancellationToken cancellationToken = default)
    {
        Dictionary<string, List<BookDocument>> byIndex = new(StringComparer.Ordinal)
        {
            [options.StripeAIndex] = [],
            [options.StripeBIndex] = [],
            [options.OracleIndex] = [],
        };

        foreach (BookDocument book in books)
        {
            string target = router.Route(book);

            book.AssignedStripe = target;
            byIndex[target].Add(book);
            byIndex[options.OracleIndex].Add(book);
        }

        foreach ((string indexName, List<BookDocument> documents) in byIndex)
        {
            if (skipOracle && string.Equals(indexName, options.OracleIndex, StringComparison.Ordinal))
            {
                Console.WriteLine($"  {indexName}: skipped (already holds the full corpus)");
                continue;
            }

            await UploadToIndexAsync(indexName, documents, cancellationToken).ConfigureAwait(false);
        }

        return new UploadSummary(
            byIndex[options.StripeAIndex].Count,
            byIndex[options.StripeBIndex].Count,
            skipOracle ? 0 : byIndex[options.OracleIndex].Count);
    }

    private async Task UploadToIndexAsync(
        string indexName,
        List<BookDocument> documents,
        CancellationToken cancellationToken)
    {
        SearchClient client = factory.GetSearchClient(indexName);
        int uploaded = 0;

        for (int offset = 0; offset < documents.Count; offset += UploadBatchSize)
        {
            List<BookDocument> slice = documents.GetRange(
                offset, Math.Min(UploadBatchSize, documents.Count - offset));

            await UploadBatchWithRetryAsync(client, slice, indexName, cancellationToken)
                .ConfigureAwait(false);

            uploaded += slice.Count;
            Console.Write($"\r  {indexName}: {uploaded:N0}/{documents.Count:N0}");
        }

        Console.WriteLine();
    }

    /// <summary>
    /// Uploads one batch, resending only the documents the service rejected transiently.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A busy service rejects individual documents rather than the whole request. The SDK reports
    /// that as HTTP 207 with a per-document result list and — because <c>ThrowOnAnyError</c>
    /// defaults to false — does <em>not</em> raise an exception. Partial failure therefore has to be
    /// detected by inspecting <see cref="IndexDocumentsResult.Results"/>; code that only wraps the
    /// call in a try/catch will never see it.
    /// </para>
    /// <para>
    /// Treating any rejection as fatal would be worse than useless here: a single throttled document
    /// would abandon a 10,000-document load that is otherwise fine. Only the rejected documents are
    /// resent, which is both correct and far cheaper than resending the batch.
    /// </para>
    /// <para>
    /// Per-document status codes are separated by whether resending can help. 429 and 5xx are the
    /// service asking for backpressure and are retried. A 4xx such as 400 means the document itself
    /// is malformed, and resending it unchanged will fail identically forever, so it fails fast with
    /// the service's own message rather than after five pointless round trips.
    /// </para>
    /// </remarks>
    private static async Task UploadBatchWithRetryAsync(
        SearchClient client,
        IReadOnlyList<BookDocument> batch,
        string indexName,
        CancellationToken cancellationToken)
    {
        const int MaxAttempts = 5;

        Dictionary<string, BookDocument> byKey =
            batch.ToDictionary(d => d.Id, StringComparer.Ordinal);
        IReadOnlyList<BookDocument> pending = batch;

        for (int attempt = 0; ; attempt++)
        {
            IndexDocumentsResult result;

            try
            {
                result = await client
                    .UploadDocumentsAsync(pending, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (RequestFailedException ex) when (IsTransient(ex.Status) && attempt < MaxAttempts)
            {
                // The whole request failed rather than individual documents. Same backoff, same
                // pending set.
                await DelayAsync(attempt, cancellationToken).ConfigureAwait(false);
                continue;
            }

            IndexingResult[] failures = [.. result.Results.Where(r => !r.Succeeded)];
            if (failures.Length == 0)
            {
                return;
            }

            IndexingResult[] permanent = [.. failures.Where(f => !IsTransient(f.Status))];
            if (permanent.Length > 0)
            {
                throw new InvalidOperationException(
                    $"{permanent.Length} document(s) were rejected by {indexName} for reasons that "
                    + $"retrying cannot fix. First: {permanent[0].Key} returned "
                    + $"{permanent[0].Status} - {permanent[0].ErrorMessage}");
            }

            if (attempt >= MaxAttempts)
            {
                throw new InvalidOperationException(
                    $"{failures.Length} document(s) were still being throttled by {indexName} after "
                    + $"{MaxAttempts + 1} attempts. First: {failures[0].Key} returned "
                    + $"{failures[0].Status} - {failures[0].ErrorMessage}");
            }

            // Narrow to just the rejected documents before backing off.
            pending = [.. failures
                .Select(f => byKey.TryGetValue(f.Key, out BookDocument? doc) ? doc : null)
                .OfType<BookDocument>()];

            if (pending.Count == 0)
            {
                // The service named a key that was not in the batch. Retrying an empty set would
                // spin forever, so surface it rather than hang.
                throw new InvalidOperationException(
                    $"{indexName} reported failures for keys that were not in the submitted batch. "
                    + $"First: {failures[0].Key}");
            }

            await DelayAsync(attempt, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Whether a status is the service asking for backpressure rather than rejecting the content.
    /// </summary>
    private static bool IsTransient(int status) => status == 429 || status >= 500;

    private static Task DelayAsync(int attempt, CancellationToken cancellationToken) =>
        Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), cancellationToken);

    private IEnumerable<string> AllIndexes()
    {
        yield return options.StripeAIndex;
        yield return options.StripeBIndex;
        yield return options.OracleIndex;
    }
}

/// <summary>Document counts written to each index.</summary>
public readonly record struct UploadSummary(int StripeA, int StripeB, int Oracle);

