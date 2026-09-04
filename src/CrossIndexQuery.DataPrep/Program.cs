using System.CommandLine;
using CrossIndexQuery.Core;
using CrossIndexQuery.Core.Configuration;
using CrossIndexQuery.DataPrep.Stages;

// Offline corpus pipeline. Everything here runs once, and its output is committed so that anyone
// cloning the sample can index and evaluate without paying for blurb generation or embeddings.
var dataOption = new Option<string>("--data")
{
    Description = "Data directory holding genre-map.json and the generated corpus.",
    DefaultValueFactory = _ => "data",
    Recursive = true,
};

var root = new RootCommand("Builds the cross-index-query book corpus from goodbooks-10k.");
root.Options.Add(dataOption);

CrossIndexOptions config = ConfigurationLoader.Load();

// ---- download -------------------------------------------------------------------------------
var forceOption = new Option<bool>("--force")
{
    Description = "Re-download files that are already present.",
};

var download = new Command("download", "Fetch the raw goodbooks-10k CSVs into data/raw.");
download.Options.Add(forceOption);
download.SetAction((parse, ct) =>
    new DownloadStage(Resolve(parse, dataOption)).RunAsync(parse.GetValue(forceOption), ct));
root.Subcommands.Add(download);

// ---- prepare --------------------------------------------------------------------------------
var prepare = new Command(
    "prepare",
    "Join the CSVs, resolve a primary genre per book, and write data/books.base.json.");
prepare.SetAction((parse, ct) => new PrepareCorpusStage(Resolve(parse, dataOption)).RunAsync(ct));
root.Subcommands.Add(prepare);

// ---- blurbs ---------------------------------------------------------------------------------
var jobOption = new Option<string>("--job")
{
    Description = "Name for this batch job, used for the on-disk request/result files.",
    DefaultValueFactory = _ => "blurbs",
};

var limitOption = new Option<int>("--limit")
{
    Description = "Sample only N books, spread evenly across the corpus. 0 means all of them.",
    DefaultValueFactory = _ => 0,
};

var blurbs = new Command("blurbs", "Generate book descriptions with the Azure OpenAI Batch API.");

var submit = new Command("submit", "Build the JSONL request file, upload it, and start a batch job.");
submit.Options.Add(jobOption);
submit.Options.Add(limitOption);
submit.SetAction((parse, ct) => new BlurbStage(Resolve(parse, dataOption)).SubmitAsync(
    config.Foundry.Endpoint,
    config.Foundry.ApiKey,
    RequireBlurbDeployment(config),
    parse.GetValue(limitOption),
    parse.GetValue(jobOption)!,
    ct));
blurbs.Subcommands.Add(submit);

var status = new Command("status", "Poll a submitted batch job.");
status.Options.Add(jobOption);
status.SetAction((parse, ct) => new BlurbStage(Resolve(parse, dataOption)).StatusAsync(
    config.Foundry.Endpoint, config.Foundry.ApiKey, parse.GetValue(jobOption)!, ct));
blurbs.Subcommands.Add(status);

var collect = new Command("collect", "Download a completed batch and merge it into data/books.blurbs.json.");
collect.Options.Add(jobOption);
collect.SetAction((parse, ct) => new BlurbStage(Resolve(parse, dataOption)).CollectAsync(
    config.Foundry.Endpoint, config.Foundry.ApiKey, parse.GetValue(jobOption)!, ct));
blurbs.Subcommands.Add(collect);

root.Subcommands.Add(blurbs);

// ---- embed ----------------------------------------------------------------------------------
var embed = new Command(
    "embed",
    "Embed every book with one model and write data/books.enriched.json plus the manifest.");
embed.SetAction((parse, ct) => new EmbeddingStage(Resolve(parse, dataOption)).RunAsync(
    config.Foundry.Endpoint,
    config.Foundry.ApiKey,
    config.Foundry.EmbeddingDeployment,
    config.Foundry.EmbeddingDimensions,
    config.Foundry.BatchDeployment,
    ct));
root.Subcommands.Add(embed);

// ---- stats ----------------------------------------------------------------------------------
var stats = new Command(
    "stats",
    "Compute global and per-stripe document frequencies into data/corpus-statistics.json.");
stats.SetAction((parse, ct) =>
    new CorpusStatisticsStage(Resolve(parse, dataOption)).RunAsync(config, ct));
root.Subcommands.Add(stats);

// ---- judge ----------------------------------------------------------------------------------
// Relevance judgments break the circularity in the rest of the harness: every other metric scores a
// fused result against the oracle's ordering, which cannot answer whether the oracle was itself the
// best available answer. Judged separately, the oracle becomes one more measured system.
var judge = new Command(
    "judge",
    "Score the pooled (query, document) pairs for relevance with an independent judge.");

var judgeSubmit = new Command("submit", "Build the JSONL request file from results/judgment-pool.json and start a batch.");
judgeSubmit.SetAction((parse, ct) => NewJudgmentStage(parse, dataOption).SubmitAsync(config, ct));
judge.Subcommands.Add(judgeSubmit);

var judgeStatus = new Command("status", "Poll the submitted judging batch.");
judgeStatus.SetAction((parse, ct) => NewJudgmentStage(parse, dataOption).StatusAsync(config, ct));
judge.Subcommands.Add(judgeStatus);

var judgeCollect = new Command("collect", "Download a completed judging batch into data/judgments.json.");
judgeCollect.SetAction((parse, ct) => NewJudgmentStage(parse, dataOption).CollectAsync(config, ct));
judge.Subcommands.Add(judgeCollect);

var sampleOption = new Option<int>("--sample")
{
    Description = "How many already-judged pairs to re-judge with the second model.",
    DefaultValueFactory = _ => 1200,
};

var agreement = new Command(
    "agreement",
    "Re-judge a sample with a second model and report inter-judge agreement.");
agreement.Options.Add(sampleOption);
agreement.SetAction((parse, ct) => NewJudgmentStage(parse, dataOption)
    .AgreementAsync(config, parse.GetValue(sampleOption), maxConcurrency: 16, ct));
judge.Subcommands.Add(agreement);

root.Subcommands.Add(judge);

return await root.Parse(args).InvokeAsync().ConfigureAwait(false);

static JudgmentStage NewJudgmentStage(ParseResult parse, Option<string> dataOption)
{
    string data = Resolve(parse, dataOption);
    string repositoryRoot = RepositoryLocator.ResolveRepositoryRoot();
    return new JudgmentStage(data, Path.Combine(data, "batch"), Path.Combine(repositoryRoot, "results"));
}

static string Resolve(ParseResult parse, Option<string> dataOption) =>
    RepositoryLocator.ResolveDataDirectory(parse.GetValue(dataOption) ?? "data");

static string RequireBlurbDeployment(CrossIndexOptions config) =>
    string.IsNullOrWhiteSpace(config.Foundry.BatchDeployment)
        ? throw new InvalidOperationException(
            "Foundry:BatchDeployment is not configured. Point it at a GlobalBatch chat deployment.")
        : config.Foundry.BatchDeployment;
