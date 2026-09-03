using System.CommandLine;
using CrossIndexQuery.Cli.Commands;
using CrossIndexQuery.Core.Configuration;
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

var init = new Command("init", "Create the two stripe indexes plus the oracle, and load the corpus.");
init.Options.Add(recreateOption);
init.Options.Add(skipOracleOption);
init.SetAction((parse, ct) =>
    new InitCommand(options).RunAsync(
        parse.GetValue(recreateOption), parse.GetValue(skipOracleOption), ct));
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

return await root.Parse(args).InvokeAsync().ConfigureAwait(false);
