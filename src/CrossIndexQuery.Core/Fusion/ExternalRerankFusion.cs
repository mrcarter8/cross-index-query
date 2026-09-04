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
public sealed class ExternalRerankFusion : IFusionStrategy
{
    /// <summary>Attempts per candidate before giving up and reporting it ungradable.</summary>
    private const int MaxAttempts = 4;

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
        int ungradable = 0;

        await Task.WhenAll(candidates.Select(async (candidate, index) =>
        {
            await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                grades[index] = await GradeAsync(fanOut.Query, candidate.Document, cancellationToken)
                    .ConfigureAwait(false);

                if (grades[index] < 0)
                {
                    Interlocked.Increment(ref ungradable);
                }
            }
            finally
            {
                throttle.Release();
            }
        })).ConfigureAwait(false);

        if (ungradable > 0)
        {
            // Surfaced rather than absorbed. A run where the model never answered is a degraded
            // measurement, and reporting it as poor relevance would be indistinguishable from the
            // model having judged those documents irrelevant.
            Console.Error.WriteLine(
                $"  [{Name}] {ungradable}/{candidates.Count} candidates could not be graded for "
                + $"'{fanOut.Query}'; treated as irrelevant, so this result is understated.");
        }

        List<FusedDocument> scored = [];

        for (int i = 0; i < candidates.Count; i++)
        {
            ScoredDocument candidate = candidates[i];
            int grade = Math.Max(grades[i], 0);

            // The grade dominates; the retrieval score only separates documents the model graded
            // equally. Scaling it down keeps it strictly subordinate rather than competing.
            double tieBreak = Math.Clamp(candidate.Score, 0, 1000) / 100_000d;

            scored.Add(new FusedDocument(
                candidate,
                grade + tieBreak,
                grades[i] < 0
                    ? $"ungraded (from {candidate.SourceIndex})"
                    : $"external grade {grade}/3 (from {candidate.SourceIndex})"));
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

    /// <summary>
    /// Grades one candidate, retrying transient failures.
    /// </summary>
    /// <returns>A grade of 0-3, or -1 when the model could not be made to answer.</returns>
    /// <remarks>
    /// Throttling is the expected condition here, not the exceptional one: a full evaluation issues
    /// one completion per candidate per query per mode against a single deployment. Converting a 429
    /// into a grade of zero would publish "the service was busy" as "this document is irrelevant",
    /// which is the failure mode this whole study exists to avoid — a plausible number with nothing
    /// behind it.
    /// </remarks>
    private async Task<int> GradeAsync(
        string query,
        BookDocument document,
        CancellationToken cancellationToken)
    {
        string authors = document.Authors.Length > 0 ? string.Join(", ", document.Authors) : "unknown";

        var user =
            $"""
             Query: {query}

             Title: {document.Title}
             Author(s): {authors}
             Description: {document.Blurb}
             """;

        var delay = TimeSpan.FromSeconds(1);

        for (int attempt = 0; attempt < MaxAttempts; attempt++)
        {
            try
            {
                ClientResult<ChatCompletion> result = await _client.CompleteChatAsync(
                    [new SystemChatMessage(SystemPrompt), new UserChatMessage(user)],
                    new ChatCompletionOptions(),
                    cancellationToken).ConfigureAwait(false);

                string text = result.Value.Content.Count > 0 ? result.Value.Content[0].Text : string.Empty;
                return TryParseGrade(text, out int grade) ? grade : -1;
            }
            catch (ClientResultException ex)
                when (IsTransient(ex.Status) && attempt < MaxAttempts - 1)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
            }
            catch (ClientResultException)
            {
                return -1;
            }
        }

        return -1;
    }

    private static bool IsTransient(int status) => status is 408 or 429 or 500 or 502 or 503 or 504;

    /// <summary>
    /// Reads the grade out of the reply.
    /// </summary>
    /// <remarks>
    /// A digit outside the scale means the model answered a different question than the one asked,
    /// so the reply is discarded rather than mined for the first usable character. Without that
    /// guard a reply of "10" scores 1 and "Grade: 4" scores 0 — values nobody assigned.
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
}
