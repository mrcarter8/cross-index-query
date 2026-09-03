// Pattern 2 — self-rerank, external.
//
// Retrieval stays cheap and approximate; a model you own decides the final order. This stands in
// for anything of that shape: a hosted rerank API, an ONNX cross-encoder on your own hardware, a
// fine-tuned scorer, or an LLM as shown here.
//
// Why it sidesteps the striping problem entirely: one model scores every candidate, so the numbers
// are commensurable by construction. No corpus statistic enters the calculation, so a split corpus
// has nothing to distort. You are not repairing the scores — you are replacing them.
//
// What it costs: a model call per candidate. Measured at ~24 seconds p50 for a 50-document pool
// against a small model at concurrency 8, against ~55 ms for pattern 1. That is the trade.

using Azure.AI.OpenAI;
using Azure.Identity;
using OpenAI.Chat;

namespace CrossIndexQuery.Samples;

public static class Pattern2ExternalRerank
{
    // A coarse integer scale, not a continuous score. Language models are markedly more consistent
    // choosing among a few labelled grades than emitting a float, and only the ordering survives
    // into the final result anyway.
    private const string SystemPrompt =
        """
        Rate how well the document answers the search query.

        3 - excellent: exactly what the query asks for
        2 - good: clearly on topic
        1 - weak: related but not what was asked for
        0 - irrelevant

        Reply with one digit and nothing else.
        """;

    public static async Task<List<Ranked>> RerankAsync(
        IReadOnlyList<Candidate> candidates,
        string query,
        string endpoint,
        string deployment,
        int topK,
        int maxConcurrency = 8,
        CancellationToken cancellationToken = default)
    {
        var client = new AzureOpenAIClient(new Uri(endpoint), new DefaultAzureCredential())
            .GetChatClient(deployment);

        var grades = new int[candidates.Count];
        using var throttle = new SemaphoreSlim(maxConcurrency);

        await Task.WhenAll(candidates.Select(async (candidate, index) =>
        {
            await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                grades[index] = await GradeAsync(client, query, candidate, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                throttle.Release();
            }
        })).ConfigureAwait(false);

        return [.. candidates
            .Select((candidate, index) => new Ranked(
                candidate.Id,
                candidate.SourceIndex,

                // The grade dominates. The retrieval score only separates documents the model
                // graded equally, so it is scaled down to stay strictly subordinate rather than
                // competing with the grade.
                grades[index] + (Math.Clamp(candidate.RetrievalScore, 0, 1000) / 100_000d)))
            .OrderByDescending(r => r.Score)
            .Take(topK)];
    }

    /// <summary>
    /// Chooses which candidates are worth paying to rerank.
    /// </summary>
    /// <remarks>
    /// Interleaving by rank matters more than it looks. Taking candidates in fan-out order lets one
    /// index consume the whole budget before the other is reached, which quietly turns a cross-index
    /// rerank into a single-index one — and the failure is invisible, because the results still look
    /// reasonable.
    /// </remarks>
    public static List<Candidate> SelectPool(
        IReadOnlyList<IReadOnlyList<Candidate>> perIndex,
        int budget)
    {
        var pool = new List<Candidate>();
        var depth = perIndex.Count == 0 ? 0 : perIndex.Max(list => list.Count);

        for (var rank = 0; rank < depth && pool.Count < budget; rank++)
        {
            foreach (var list in perIndex)
            {
                if (rank < list.Count && pool.Count < budget)
                {
                    pool.Add(list[rank]);
                }
            }
        }

        return pool;
    }

    private static async Task<int> GradeAsync(
        ChatClient client,
        string query,
        Candidate candidate,
        CancellationToken cancellationToken)
    {
        var user = $"Query: {query}\n\n{candidate.Text}";

        try
        {
            var result = await client.CompleteChatAsync(
                [new SystemChatMessage(SystemPrompt), new UserChatMessage(user)],
                new ChatCompletionOptions(),
                cancellationToken).ConfigureAwait(false);

            var text = result.Value.Content.Count > 0 ? result.Value.Content[0].Text : string.Empty;

            foreach (var c in text)
            {
                if (c is >= '0' and <= '3')
                {
                    return c - '0';
                }
            }

            return 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // One refused or throttled judgment should not discard the other forty-nine. An
            // ungraded document sinks to the bottom, which is the same treatment an irrelevant one
            // gets: conservative, and visible as a missing document rather than a confidently wrong
            // ranking.
            return 0;
        }
    }

    public sealed record Candidate(string Id, string SourceIndex, string Text, double RetrievalScore);

    public sealed record Ranked(string Id, string SourceIndex, double Score);
}
