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

    public FoundryOptions Foundry { get; set; } = new();

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

    /// <summary>Default stripe index names, before the split descriptor is applied.</summary>
    internal const string DefaultStripeAIndex = "books-stripe-a";

    /// <summary>Second stripe's default name.</summary>
    internal const string DefaultStripeBIndex = "books-stripe-b";

    /// <summary>First stripe of the split corpus.</summary>
    public string StripeAIndex { get; set; } = DefaultStripeAIndex;

    /// <summary>Second stripe of the split corpus.</summary>
    public string StripeBIndex { get; set; } = DefaultStripeBIndex;

    /// <summary>
    /// Ground-truth index holding the entire corpus in one place. Used only by the
    /// evaluation harness to measure how much relevance striping costs.
    /// </summary>
    public string OracleIndex { get; set; } = "books-oracle";

    /// <summary>Default knowledge base name, before the split descriptor is applied.</summary>
    internal const string DefaultKnowledgeBaseName = "books-kb";

    /// <summary>
    /// Knowledge base used by the agentic-retrieval strategy.
    /// </summary>
    /// <remarks>
    /// Split-qualified like the stripe indexes, because a knowledge base names the specific indexes
    /// it federates. Reusing one across splits would leave it pointing at the previous scenario's
    /// indexes and answering from them without complaint.
    /// </remarks>
    public string KnowledgeBaseName { get; set; } = DefaultKnowledgeBaseName;

    /// <summary>Semantic configuration name, identical across all three indexes.</summary>
    public string SemanticConfigurationName { get; set; } = "books-semantic";

    /// <summary>Vector profile name, identical across all three indexes.</summary>
    public string VectorProfileName { get; set; } = "books-vector-profile";

    /// <summary>All stripe index names, in stable order.</summary>
    public IReadOnlyList<string> StripeIndexes => [StripeAIndex, StripeBIndex];
}

/// <summary>
/// The Microsoft Foundry account, and the deployments on it that this sample uses.
/// </summary>
/// <remarks>
/// <para>
/// One account, one endpoint, one optional key. Every model this sample touches — embeddings, the
/// offline batch jobs, the external reranker, and the optional query-planning model — lives on the
/// same Foundry resource, so splitting them across several endpoint settings would invent a
/// distinction the deployment topology does not have.
/// </para>
/// <para>
/// Deployments are named for what they are rather than for the one place they happen to be used,
/// because most of them are already used in more than one: the batch deployment writes the corpus
/// blurbs <em>and</em> grades relevance judgments, and the chat deployment backs both the external
/// reranking strategy and the second judge in the agreement check.
/// </para>
/// </remarks>
public sealed class FoundryOptions
{
    /// <summary>Account endpoint, e.g. <c>https://my-account.openai.azure.com/</c>.</summary>
    [Required]
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>
    /// Account key. Leave empty to authenticate with <c>DefaultAzureCredential</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not needed for anything the sample itself calls: <c>az login</c> covers embeddings, the
    /// batch jobs and the external reranker, and role-based access is the recommended path.
    /// </para>
    /// <para>
    /// It becomes necessary for exactly one thing, and only on some tiers. When
    /// <see cref="QueryPlanningDeployment"/> is set, the <em>search service</em> calls Foundry on
    /// its own behalf rather than on yours, so your credential is irrelevant to that hop. The
    /// service would normally use a managed identity holding <b>Cognitive Services User</b>, but
    /// managed identity requires the <b>Basic tier or higher</b> — a serverless search service
    /// cannot hold one. On serverless this key is the only route; on Basic or above, assign the
    /// role and leave it empty.
    /// </para>
    /// </remarks>
    public string? ApiKey { get; set; }

    /// <summary>Deployment name for the embedding model.</summary>
    public string EmbeddingDeployment { get; set; } = "text-embedding-3-small";

    /// <summary>
    /// Model identifier recorded alongside the corpus. The preflight validator refuses to
    /// query if the indexes were built with a different model, because cross-index vector
    /// comparison is only valid within a single embedding space.
    /// </summary>
    public string EmbeddingModel { get; set; } = "text-embedding-3-small";

    /// <summary>Vector dimensions produced by <see cref="EmbeddingDeployment"/>.</summary>
    public int EmbeddingDimensions { get; set; } = 1536;

    /// <summary>
    /// A <c>GlobalBatch</c> chat deployment, used by the offline jobs.
    /// </summary>
    /// <remarks>
    /// Writes the corpus blurbs and grades the relevance judgments. Neither is needed to run the
    /// sample — both outputs are committed — so this stays empty unless you are regenerating the
    /// corpus or extending the judged set. The batch SKU is what makes those jobs affordable at
    /// several thousand documents.
    /// </remarks>
    public string? BatchDeployment { get; set; }

    /// <summary>
    /// A standard chat deployment, used wherever the sample calls a model interactively.
    /// </summary>
    /// <remarks>
    /// Backs the external reranking strategy and the second judge in the agreement check.
    /// Deliberately a small, fast model: the external rerank pattern is characterised by paying per
    /// candidate at query time, so its cost profile is only representative if the model is one you
    /// would actually put in a query path.
    /// </remarks>
    public string ChatDeployment { get; set; } = "gpt-5-nano";

    /// <summary>
    /// A standard chat deployment attached to the knowledge base for query planning.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Empty by default, and the sample is fully functional without it. Left empty, the retrieval
    /// engine is pinned to <c>minimal</c> reasoning effort, where the documentation is explicit
    /// that there is "no LLM for intelligent query planning or answer synthesis" — the query goes
    /// straight to search and ordering comes from the semantic ranker. Every agentic number this
    /// study has published so far was measured in that state.
    /// </para>
    /// <para>
    /// Setting it attaches a model and unlocks <c>low</c> and <c>medium</c> reasoning effort, which
    /// is where a query is actually decomposed into subqueries. That is the capability most likely
    /// to help short, low-context queries — the "one company name and nothing else" case that
    /// motivates cross-index relevance work in the first place.
    /// </para>
    /// <para>
    /// Kept separate from <see cref="ChatDeployment"/> rather than folded into it for two reasons.
    /// Attaching a model changes what pattern 4 measures, so it has to be opt-in rather than a
    /// side effect of configuring the reranker. And the service validates this one against a
    /// supported-model list that the reranker is not held to: <c>gpt-5</c>, <c>gpt-5-mini</c>,
    /// <c>gpt-5-nano</c>, <c>gpt-5.1</c>, <c>gpt-5.2</c>, <c>gpt-5.4</c>, <c>gpt-5.4-mini</c>,
    /// <c>gpt-5.4-nano</c>, <c>gpt-5.5</c> and the <c>gpt-5.6</c> family. A batch deployment will
    /// not do — query planning is interactive.
    /// </para>
    /// </remarks>
    public string QueryPlanningDeployment { get; set; } = string.Empty;

    /// <summary>
    /// Model name behind <see cref="QueryPlanningDeployment"/>.
    /// </summary>
    /// <remarks>
    /// The service validates the model, not the deployment name, against its supported list. The
    /// two are usually the same string, so this falls back to the deployment when left empty — but
    /// a deployment named for its purpose rather than its model would fail validation with a
    /// confusing message without it.
    /// </remarks>
    public string QueryPlanningModel { get; set; } = string.Empty;

    /// <summary>Whether a query-planning model has been configured.</summary>
    public bool HasQueryPlanningModel => !string.IsNullOrWhiteSpace(QueryPlanningDeployment);
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
    /// Forces exact nearest-neighbour search in vector and hybrid modes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Defaults to false, because HNSW is what production workloads run and the headline numbers
    /// should describe what people will actually experience.
    /// </para>
    /// <para>
    /// Set it to true to separate two effects that otherwise arrive as a single number. Vector
    /// similarity consults no corpus statistics, so splitting a corpus cannot change how any two
    /// documents compare — and the measured Kendall τ of 1.000 confirms it, with no rank inversions
    /// at all. Yet recall@10 is 0.959, because HNSW is approximate and traversing two smaller
    /// proximity graphs does not visit the same neighbours as traversing one large one. That
    /// shortfall is an artefact of the search algorithm, not a cost of striping, and running with
    /// this flag is how you demonstrate the difference rather than assert it.
    /// </para>
    /// </remarks>
    public bool ExhaustiveVectorSearch { get; set; }

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
