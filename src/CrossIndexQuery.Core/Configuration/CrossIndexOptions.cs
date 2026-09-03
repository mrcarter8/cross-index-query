using System.ComponentModel.DataAnnotations;

namespace CrossIndexQuery.Core.Configuration;

/// <summary>
/// Root configuration for the sample. Bound from appsettings.json,
/// appsettings.Development.json, user secrets, and environment variables
/// (prefix <c>CIQ_</c>, e.g. <c>CIQ_Search__Endpoint</c>).
/// </summary>
public sealed class CrossIndexOptions
{
    public const string EnvironmentVariablePrefix = "CIQ_";

    public SearchServiceOptions Search { get; set; } = new();

    public EmbeddingOptions Embedding { get; set; } = new();

    public CorpusOptions Corpus { get; set; } = new();

    public EvaluationOptions Evaluation { get; set; } = new();
}

/// <summary>
/// Connection and index-naming settings for the single Azure AI Search service
/// that hosts all three indexes.
/// </summary>
public sealed class SearchServiceOptions
{
    /// <summary>Service endpoint, e.g. <c>https://my-service.search.windows.net</c>.</summary>
    [Required]
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>
    /// Optional admin key. Leave empty to authenticate with
    /// <c>DefaultAzureCredential</c>, which is the recommended path.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>First stripe of the split corpus.</summary>
    public string StripeAIndex { get; set; } = "books-stripe-a";

    /// <summary>Second stripe of the split corpus.</summary>
    public string StripeBIndex { get; set; } = "books-stripe-b";

    /// <summary>
    /// Ground-truth index holding the entire corpus in one place. Used only by the
    /// evaluation harness to measure how much relevance striping costs.
    /// </summary>
    public string OracleIndex { get; set; } = "books-oracle";

    /// <summary>Knowledge base used by the agentic-retrieval fusion strategy.</summary>
    public string KnowledgeBaseName { get; set; } = "books-kb";

    /// <summary>Semantic configuration name, identical across all three indexes.</summary>
    public string SemanticConfigurationName { get; set; } = "books-semantic";

    /// <summary>Vector profile name, identical across all three indexes.</summary>
    public string VectorProfileName { get; set; } = "books-vector-profile";

    /// <summary>All stripe index names, in stable order.</summary>
    public IReadOnlyList<string> StripeIndexes => [StripeAIndex, StripeBIndex];
}

/// <summary>
/// Azure OpenAI settings. Embeddings are generated client-side so the exact same vector
/// is sent to every index; see the embedding-consistency rule in the README.
/// </summary>
public sealed class EmbeddingOptions
{
    /// <summary>Account endpoint, e.g. <c>https://my-account.openai.azure.com/</c>.</summary>
    [Required]
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Optional key. Leave empty to use <c>DefaultAzureCredential</c>.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Deployment name for the embedding model.</summary>
    public string Deployment { get; set; } = "text-embedding-3-small";

    /// <summary>
    /// Model identifier recorded alongside the corpus. The preflight validator refuses to
    /// query if the indexes were built with a different model, because cross-index vector
    /// comparison is only valid within a single embedding space.
    /// </summary>
    public string ModelName { get; set; } = "text-embedding-3-small";

    /// <summary>Vector dimensions produced by <see cref="Deployment"/>.</summary>
    public int Dimensions { get; set; } = 1536;

    /// <summary>Deployment used by the offline blurb-generation batch job. Not needed at query time.</summary>
    public string? BlurbDeployment { get; set; }

    /// <summary>
    /// Chat deployment used by the external reranking strategy.
    /// </summary>
    /// <remarks>
    /// Deliberately a small, fast model. The external rerank pattern is characterised by paying per
    /// candidate at query time, so its cost profile is only representative if the model is one you
    /// would actually put in a query path.
    /// </remarks>
    public string RerankDeployment { get; set; } = "gpt-5-nano";
}

/// <summary>Corpus location and how documents are divided between the two stripes.</summary>
public sealed class CorpusOptions
{
    /// <summary>Directory holding the committed corpus and query set.</summary>
    public string DataDirectory { get; set; } = "data";

    /// <summary>
    /// How documents are assigned to stripes. This is the sample's key independent variable:
    /// a random split keeps both stripes statistically similar, while a genre split makes
    /// their term distributions diverge — which is exactly when naive score merging breaks.
    /// </summary>
    /// <remarks>
    /// Defaults to <see cref="StripeMode.Genre"/> because that is the configuration the sample
    /// exists to measure. <see cref="StripeMode.Random"/> is the control: it produces stripes so
    /// statistically similar that naive merging looks acceptable, so defaulting to it would
    /// quietly measure the wrong arm of the experiment.
    /// </remarks>
    public StripeMode StripeMode { get; set; } = StripeMode.Genre;

    /// <summary>Seed for the random striping mode, so runs are reproducible.</summary>
    public int StripeSeed { get; set; } = 20260401;

    /// <summary>
    /// Publication year that divides the two stripes under <see cref="StripeMode.Temporal"/>.
    /// Documents published in this year or earlier route to stripe A; later ones to stripe B.
    /// </summary>
    /// <remarks>
    /// Models the migration a customer actually performs when they hit the index size limit: the
    /// existing index is frozen for writes and every new document goes to the new one. The split
    /// axis is arrival time, and the resulting stripes are extremely unequal — which is the point,
    /// because size imbalance distorts BM25 independently of anything thematic.
    /// </remarks>
    public int StripeYearCut { get; set; } = 2013;

    /// <summary>
    /// Short, stable name for the current split, used to keep derived artifacts from colliding.
    /// </summary>
    /// <remarks>
    /// The corpus-statistics sidecar describes one specific partitioning. Applying a sidecar built
    /// for a different split silently produces wrong IDF corrections rather than an error, so the
    /// split identity travels in the filename and inside the file.
    /// </remarks>
    public string SplitDescriptor => StripeMode switch
    {
        StripeMode.Genre => "genre",
        StripeMode.Temporal => $"temporal-{StripeYearCut}",
        _ => $"random-{StripeSeed}",
    };
}

/// <summary>How the corpus is partitioned across stripe indexes.</summary>
public enum StripeMode
{
    /// <summary>Deterministic hash split. Stripes stay statistically comparable.</summary>
    Random,

    /// <summary>
    /// Split along genre boundaries. Produces divergent corpus statistics, which is the
    /// realistic case when a corpus outgrows one index and is divided along a business axis.
    /// </summary>
    Genre,

    /// <summary>
    /// Split by publication year: everything up to a cut-off in one stripe, everything after it in
    /// the other.
    /// </summary>
    /// <remarks>
    /// The "stripe to scale" case. Nobody redistributes terabytes to balance two indexes, so the
    /// real migration freezes the existing index and sends new documents to a new one. That yields
    /// a pair of indexes with mild vocabulary drift and severe size imbalance — the opposite profile
    /// to <see cref="Genre"/>, and a different failure mode.
    /// </remarks>
    Temporal,
}

/// <summary>How many candidates each arm of the comparison is allowed to retrieve.</summary>
/// <remarks>
/// <para>
/// This is an experimental control, not a tuning knob. The striped arm queries N indexes and the
/// oracle queries one, so asking every index for the same number of candidates gives the striped
/// arm N times the candidate depth. Any difference in the results is then attributable to two
/// changes at once — how the corpus is split, and how many documents were considered — and the
/// experiment cannot separate them.
/// </para>
/// </remarks>
public enum CandidateBudget
{
    /// <summary>
    /// Every index returns <c>PerStripeK</c> candidates. The striped arm therefore considers
    /// N x <c>PerStripeK</c> documents against the oracle's <c>PerStripeK</c>. This is the
    /// condition under which striping might beat the oracle purely by seeing more.
    /// </summary>
    PerIndex,

    /// <summary>
    /// <c>PerStripeK</c> is the total for the arm. The oracle takes all of it; each of N stripes
    /// takes <c>PerStripeK</c>/N. Both arms consider the same number of documents, so what remains
    /// is the cost of the split itself.
    /// </summary>
    Equalized,
}

/// <summary>Settings for the evaluation harness.</summary>
public sealed class EvaluationOptions
{
    /// <summary>
    /// Queries issued and discarded before measurement begins. Serverless scales compute to
    /// zero after idle, so an unwarmed first query reports cold-start latency and inflated
    /// compute-unit cost. Without this, whichever strategy runs first always looks worst.
    /// </summary>
    public int WarmupQueries { get; set; } = 10;

    /// <summary>Repetitions per query used for latency percentiles.</summary>
    public int Repetitions { get; set; } = 3;

    /// <summary>Final result-set size the fused list is truncated to.</summary>
    public int TopK { get; set; } = 10;

    /// <summary>Documents requested from each stripe before fusion.</summary>
    public int PerStripeK { get; set; } = 50;

    /// <summary>
    /// Whether <see cref="PerStripeK"/> is per index or the total for the arm.
    /// </summary>
    /// <remarks>
    /// Defaults to <see cref="CandidateBudget.Equalized"/> so the published comparison holds
    /// candidate depth constant and isolates the split. Switch to
    /// <see cref="CandidateBudget.PerIndex"/> to measure the opposite condition deliberately.
    /// </remarks>
    public CandidateBudget CandidateBudget { get; set; } = CandidateBudget.Equalized;

    /// <summary>
    /// Candidates each stripe may retrieve, given the budget and how many stripes there are.
    /// </summary>
    /// <remarks>
    /// Rounded up, so an odd budget is never silently truncated below the oracle's depth. With two
    /// stripes and a budget of 50 this yields 25 each.
    /// </remarks>
    public int CandidatesPerStripe(int stripeCount) =>
        CandidateBudget is CandidateBudget.PerIndex || stripeCount <= 1
            ? PerStripeK
            : (int)Math.Ceiling(PerStripeK / (double)stripeCount);

    /// <summary>Directory for results.csv / results.md.</summary>
    public string OutputDirectory { get; set; } = "results";

    /// <summary>
    /// Judgment file used to compute absolute relevance, relative to the data directory.
    /// </summary>
    /// <remarks>
    /// Overridable so a run can be re-scored against a second judge's grades without re-querying
    /// the service. Whether the conclusions survive a change of judge is a property of the study,
    /// and the only way to establish it is to compute them both ways.
    /// </remarks>
    public string JudgmentsFile { get; set; } = "judgments.json";
}
