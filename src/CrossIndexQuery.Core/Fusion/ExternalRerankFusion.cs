using System.ClientModel;
using Azure.AI.OpenAI;
using Azure.Identity;
using CrossIndexQuery.Core.Configuration;
using CrossIndexQuery.Core.Models;
using CrossIndexQuery.Core.Retrieval;
using OpenAI.Chat;

namespace CrossIndexQuery.Core.Fusion;

/// <summary>
/// Re-scores the merged candidate pool with a language model running outside the search service.
/// </summary>
/// <remarks>
/// <para>
/// This is the self-hosted rerank pattern: retrieval stays cheap and approximate, and a second
/// model you own decides the final order. It stands in for anything in that shape — a hosted rerank
/// API, an ONNX cross-encoder on your own hardware, a fine-tuned scorer. What they share is the
/// property that matters here: <em>one</em> model scores <em>every</em> candidate, so the resulting
/// numbers are commensurable by construction and it is irrelevant which index a document came from.
/// </para>
/// <para>
/// That makes it structurally immune to the striping problem rather than merely resistant to it. No
/// corpus statistic enters the calculation, so there is nothing for a split corpus to distort. The
/// price is that it is the most expensive option in the catalog on every axis except one: a model
/// call per candidate, latency proportional to pool size, and a bill from whoever runs the model —
/// but it needs no service tier and no index feature, which is why it remains the answer for anyone
/// whose search service will not do it for them.
/// </para>
/// <para>
/// Judgments are requested on a coarse integer scale rather than as a continuous score. Language
/// models are markedly more consistent choosing among a few labelled grades than emitting a float,
/// and the ordering is all that survives into the metric anyway. Ties are broken by the retrieval
/// score the document arrived with, which is the only additional information available and is at
/// least locally meaningful.
/// </para>
/// </remarks>
public sealed class ExternalRerankFusion : IFusionStrategy, IDisposable
{
    private const string SystemPrompt =
        """
        Rate how well the book answers the search query.

        3 - excellent: exactly what the query asks for
        2 - good: clearly on topic
        1 - weak: related but not what was asked for
        0 - irrelevant

        Reply with one digit and nothing else.
        """;

    private readonly ChatClient _client;
    private readonly int _maxCandidates;
    private readonly int _maxConcurrency;

    public ExternalRerankFusion(CrossIndexOptions options, int maxCandidates = 50, int maxConcurrency = 8)
    {
        ArgumentNullException.ThrowIfNull(options);

        var endpoint = new Uri(options.Embedding.Endpoint);
        AzureOpenAIClient client = string.IsNullOrWhiteSpace(options.Embedding.ApiKey)
            ? new AzureOpenAIClient(endpoint, new DefaultAzureCredential())
            : new AzureOpenAIClient(endpoint, new ApiKeyCredential(options.Embedding.ApiKey));

        _client = client.GetChatClient(options.Embedding.RerankDeployment);
        _maxCandidates = maxCandidates;
        _maxConcurrency = maxConcurrency;
    }

    public string Name => "external-rerank";

    public string Description =>
        "Re-score the merged pool with a model you host. Cross-index safe, and the most expensive.";

    public bool Supports(RetrievalMode mode) => true;

    /// <summary>
    /// Reranking replaces the ranking function, so it is only comparable against a reranked
    /// baseline. See the harness for why a mismatched baseline makes this look worse than it is.
    /// </summary>
    public bool RequiresSemanticRanker => true;

    public async ValueTask<IReadOnlyList<FusedDocument>> FuseAsync(
        FanOutResult fanOut,
        FusionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fanOut);
        ArgumentNullException.ThrowIfNull(context);

        // Interleaved by rank so that truncating the pool takes the strongest candidates from every
        // stripe rather than exhausting one before reaching the next. Taking them in fan-out order
        // would let a single stripe consume the whole budget and quietly turn this into a
        // single-index rerank.
        List<ScoredDocument> candidates = [.. Interleave(fanOut).Take(_maxCandidates)];

        if (candidates.Count == 0)
        {
            return [];
        }

        var grades = new int[candidates.Count];
        using var throttle = new SemaphoreSlim(_maxConcurrency);

        await Task.WhenAll(candidates.Select(async (candidate, index) =>
        {
            await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                grades[index] = await GradeAsync(fanOut.Query, candidate.Document, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                throttle.Release();
            }
        })).ConfigureAwait(false);

        List<FusedDocument> scored = [];

        for (int i = 0; i < candidates.Count; i++)
        {
            ScoredDocument candidate = candidates[i];

            // The grade dominates; the retrieval score only separates documents the model graded
            // equally. Scaling it down keeps it strictly subordinate rather than competing.
            double tieBreak = Math.Clamp(candidate.Score, 0, 1000) / 100_000d;

            scored.Add(new FusedDocument(
                candidate,
                grades[i] + tieBreak,
                $"external grade {grades[i]}/3 (from {candidate.SourceIndex})"));
        }

        return FusionHelpers.RankAndTruncate(scored, context.TopK);
    }

    private static IEnumerable<ScoredDocument> Interleave(FanOutResult fanOut)
    {
        int depth = fanOut.Stripes.Count == 0 ? 0 : fanOut.Stripes.Max(s => s.Documents.Count);

        for (int rank = 0; rank < depth; rank++)
        {
            foreach (StripeResultSet stripe in fanOut.Stripes)
            {
                if (rank < stripe.Documents.Count)
                {
                    yield return stripe.Documents[rank];
                }
            }
        }
    }

    private async Task<int> GradeAsync(
        string query,
        BookDocument document,
        CancellationToken cancellationToken)
    {
        var user =
            $"""
             Query: {query}

             Title: {document.Title}
             Author(s): {string.Join(", ", document.Authors)}
             Description: {document.Blurb}
             """;

        try
        {
            ClientResult<ChatCompletion> result = await _client.CompleteChatAsync(
                [new SystemChatMessage(SystemPrompt), new UserChatMessage(user)],
                new ChatCompletionOptions(),
                cancellationToken).ConfigureAwait(false);

            string text = result.Value.Content.Count > 0 ? result.Value.Content[0].Text : string.Empty;

            foreach (char c in text)
            {
                if (c is >= '0' and <= '3')
                {
                    return c - '0';
                }
            }

            return 0;
        }
        catch (ClientResultException)
        {
            // One refused or throttled judgment should not discard the other forty-nine. An
            // ungraded document falls to the bottom, which is the same treatment an irrelevant one
            // gets — conservative, and visible in the results as a missing document rather than as
            // a confidently wrong ranking.
            return 0;
        }
    }

    public void Dispose()
    {
    }
}
