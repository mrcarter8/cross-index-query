using System.CommandLine;
using CrossIndexQuery.Cli.Commands;
using CrossIndexQuery.Core.Configuration;
using CrossIndexQuery.Core.Evaluation;
using CrossIndexQuery.Core.Retrieval;

// Three indexes, one result set. This CLI is the front door to the sample: 'init' builds the
// indexes, 'query' shows what fusion does to a single query, and 'evaluate' measures every strategy
// against the oracle so the trade-offs are numbers rather than assertions.
CrossIndexOptions options = ConfigurationLoader.Load();

var root = new RootCommand(
    "Collate results across striped Azure AI Search indexes, and measure what it costs you.");

// ---- init -----------------------------------------------------------------------------------
var recreateOption = new Option<bool>("--recreate")
{
    Description = "Delete and rebuild the indexes instead of updating them in place.",
};

var skipOracleOption = new Option<bool>("--skip-oracle")
{
    Description = "Leave the oracle index alone. Use when sweeping stripe configurations against "
        + "a baseline that is already built.",
};

var knowledgeBaseOnlyOption = new Option<bool>("--knowledge-base-only")
{
    Description = "Register only the agentic-retrieval knowledge base. Skips the corpus upload, "
        + "which is what you want when the indexes are already built.",
};

var init = new Command("init", "Create the two stripe indexes plus the oracle, and load the corpus.");
init.Options.Add(recreateOption);
init.Options.Add(skipOracleOption);
init.Options.Add(knowledgeBaseOnlyOption);
init.SetAction((parse, ct) =>
    new InitCommand(options).RunAsync(
        parse.GetValue(recreateOption),
        parse.GetValue(skipOracleOption),
        parse.GetValue(knowledgeBaseOnlyOption),
        ct));
root.Subcommands.Add(init);

// ---- query ----------------------------------------------------------------------------------
var queryArgument = new Argument<string>("query")
{
    Description = "The search text.",
};

var modeOption = new Option<RetrievalMode>("--mode", "-m")
{
    Description = "keyword, vector, or hybrid.",
    DefaultValueFactory = _ => RetrievalMode.Hybrid,
};

var strategyOption = new Option<string>("--strategy", "-s")
{
    Description = "Fusion strategy name. Use an unknown value to list them all.",
    DefaultValueFactory = _ => "rrf",
};

var semanticOption = new Option<bool>("--semantic")
{
    Description = "Apply the semantic ranker to each stripe before fusing.",
};

var explainOption = new Option<bool>("--explain")
{
    Description = "Show per-stripe scores and how the strategy transformed them.",
};

var query = new Command("query", "Run one query across the stripes and fuse the results.");
query.Arguments.Add(queryArgument);
query.Options.Add(modeOption);
query.Options.Add(strategyOption);
query.Options.Add(semanticOption);
query.Options.Add(explainOption);
query.SetAction((parse, ct) => new QueryCommand(options).RunAsync(
    parse.GetValue(queryArgument)!,
    parse.GetValue(modeOption),
    parse.GetValue(strategyOption)!,
    parse.GetValue(semanticOption),
    parse.GetValue(explainOption),
    ct));
root.Subcommands.Add(query);

// ---- evaluate -------------------------------------------------------------------------------
var modesOption = new Option<RetrievalMode[]>("--modes")
{
    Description = "Retrieval modes to evaluate.",
    DefaultValueFactory = _ => [RetrievalMode.Keyword, RetrievalMode.Vector, RetrievalMode.Hybrid],
    AllowMultipleArgumentsPerToken = true,
};

var limitOption = new Option<int>("--limit")
{
    Description = "Evaluate only N queries, sampled evenly. 0 means all of them.",
    DefaultValueFactory = _ => 0,
};

var evaluate = new Command(
    "evaluate",
    "Run every applicable fusion strategy over the query set and score it against the oracle.");
evaluate.Options.Add(modesOption);
evaluate.Options.Add(semanticOption);
evaluate.Options.Add(limitOption);
evaluate.SetAction((parse, ct) => new EvaluateCommand(options).RunAsync(
    parse.GetValue(modesOption) ?? [RetrievalMode.Hybrid],
    parse.GetValue(semanticOption),
    parse.GetValue(limitOption),
    ct));
root.Subcommands.Add(evaluate);

// ---- doctor ---------------------------------------------------------------------------------
var doctor = new Command(
    "doctor",
    "Verify the service, indexes, embedding model, and query features before running anything.");
doctor.SetAction((_, ct) => new DoctorCommand(options).RunAsync(ct));
root.Subcommands.Add(doctor);

// ---- compare --------------------------------------------------------------------------------
// Offline and free. Everything it needs is committed, so a sceptical reader can re-run any
// pairwise test in this study without an Azure subscription.
var resultsOption = new Option<string>("--results")
{
    Description = "Results CSV written by evaluate.",
    Required = true,
};

var baselineOption = new Option<string>("--baseline")
{
    Description = "Strategy to treat as the reference.",
    DefaultValueFactory = _ => EvaluationHarness.SingleIndexBaseline,
};

var candidateOption = new Option<string>("--candidate")
{
    Description = "Strategy to measure against the baseline.",
    Required = true,
};

var metricOption = new Option<string>("--metric")
{
    Description = "Column to compare. judgedNdcg is absolute relevance; ndcg is fidelity to the "
        + "single index.",
    DefaultValueFactory = _ => "judgedNdcg",
};

var compareModeOption = new Option<string?>("--mode")
{
    Description = "Restrict to one retrieval mode. Defaults to every mode in the file.",
};

var compare = new Command(
    "compare",
    "Paired significance test between two strategies in a results file. Needs no Azure access.");
compare.Options.Add(resultsOption);
compare.Options.Add(baselineOption);
compare.Options.Add(candidateOption);
compare.Options.Add(metricOption);
compare.Options.Add(compareModeOption);
compare.SetAction((parse, ct) => new CompareCommand().RunAsync(
    parse.GetValue(resultsOption)!,
    parse.GetValue(baselineOption)!,
    parse.GetValue(candidateOption)!,
    parse.GetValue(metricOption)!,
    parse.GetValue(compareModeOption),
    ct));
root.Subcommands.Add(compare);

return await root.Parse(args).InvokeAsync().ConfigureAwait(false);
