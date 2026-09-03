using System.Diagnostics;
using Azure;
using CrossIndexQuery.Core.Configuration;

namespace CrossIndexQuery.Core.Retrieval;

/// <summary>
/// Issues the same query against every stripe at once and collects the results.
/// </summary>
/// <remarks>
/// <para>
/// The fan-out is concurrent, which is the only reason striping is viable at all. Two indexes
/// queried in sequence cost the sum of their latencies; queried together they cost the slower of
/// the two, so the user-visible penalty for splitting a corpus is the difference between the
/// stripes rather than a doubling. Compute, on the other hand, does add up — both indexes really
/// did the work — and the sample reports the two separately rather than letting the pleasant
/// latency number obscure the real cost.
/// </para>
/// <para>
/// A stripe that fails does not fail the query. Returning results from one index is worse than
/// returning results from both and better than returning nothing, so failures are captured per
/// stripe and reported alongside the results. Silently degrading would be worse than either, which
/// is why the failure travels with the response instead of being swallowed.
/// </para>
/// </remarks>
public sealed class MultiStripeRetriever(StripeRetriever retriever, CrossIndexOptions options)
{
    /// <summary>Queries both stripes concurrently.</summary>
    public Task<FanOutResult> SearchStripesAsync(
        RetrievalRequest request,
        CancellationToken cancellationToken = default)
        => SearchAsync(options.Search.StripeIndexes, request, cancellationToken);

    /// <summary>
    /// Queries the oracle index, which holds the entire corpus.
    /// </summary>
    /// <remarks>
    /// Deliberately routed through the identical code path as the stripes. The oracle is the
    /// benchmark's control, and a control is only meaningful if the only thing that differs is the
    /// variable under test — here, how many indexes the corpus occupies. Any difference in query
    /// construction, field selection or option handling would contaminate the comparison.
    /// </remarks>
    public Task<FanOutResult> SearchOracleAsync(
        RetrievalRequest request,
        CancellationToken cancellationToken = default)
        => SearchAsync([options.Search.OracleIndex], request, cancellationToken);

    public async Task<FanOutResult> SearchAsync(
        IReadOnlyList<string> indexNames,
        RetrievalRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(indexNames);
        ArgumentNullException.ThrowIfNull(request);

        long start = Stopwatch.GetTimestamp();

        StripeOutcome[] outcomes = await Task.WhenAll(
            indexNames.Select(name => RunAsync(name, request, cancellationToken)))
            .ConfigureAwait(false);

        TimeSpan wallClock = Stopwatch.GetElapsedTime(start);

        List<StripeResultSet> succeeded = [];
        List<StripeFailure> failed = [];

        foreach (StripeOutcome outcome in outcomes)
        {
            if (outcome.Result is not null)
            {
                succeeded.Add(outcome.Result);
            }
            else if (outcome.Failure is not null)
            {
                failed.Add(outcome.Failure);
            }
        }

        if (succeeded.Count == 0 && failed.Count > 0)
        {
            throw new InvalidOperationException(
                $"Every index failed for query '{request.Query}': "
                + string.Join("; ", failed.Select(f => $"{f.IndexName}: {f.Message}")));
        }

        return new FanOutResult(
            Query: request.Query,
            Mode: request.Mode,
            Stripes: succeeded,
            Failures: failed,
            WallClock: wallClock);
    }

    private async Task<StripeOutcome> RunAsync(
        string indexName,
        RetrievalRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            StripeResultSet result = await retriever
                .SearchAsync(indexName, request, cancellationToken)
                .ConfigureAwait(false);

            return new StripeOutcome(result, null);
        }
        catch (RequestFailedException ex)
        {
            return new StripeOutcome(null, new StripeFailure(indexName, ex.Status, ex.Message));
        }
    }

    private readonly record struct StripeOutcome(StripeResultSet? Result, StripeFailure? Failure);
}

/// <summary>One index's failure to answer, kept rather than swallowed.</summary>
public sealed record StripeFailure(string IndexName, int Status, string Message);
