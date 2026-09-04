using Microsoft.Extensions.Configuration;

namespace CrossIndexQuery.Core.Configuration;

/// <summary>
/// Builds <see cref="CrossIndexOptions"/> from the standard layered sources.
/// </summary>
/// <remarks>
/// Precedence, lowest to highest: <c>appsettings.json</c> (committed, placeholders only),
/// <c>appsettings.Development.json</c> (git-ignored, your service), user secrets, then
/// environment variables prefixed <c>CIQ_</c> — which a git-ignored <c>.env</c> file at the
/// repository root populates if one exists. The environment layer is what <c>azd</c>, CI and
/// <c>.env</c> all feed, so an automated run never needs a file on disk.
/// </remarks>
public static class ConfigurationLoader
{
    public const string SectionName = "CrossIndexQuery";

    public static CrossIndexOptions Load(string? basePath = null)
    {
        IConfigurationRoot configuration = BuildConfiguration(basePath);
        return Bind(configuration);
    }

    public static IConfigurationRoot BuildConfiguration(string? basePath = null)
    {
        basePath ??= FindSettingsDirectory();

        // Loaded into the process environment first, so it feeds the environment layer below rather
        // than forming a layer of its own. Values already exported in the shell win, which keeps a
        // one-off override working.
        DotEnvFile.Load(Path.Combine(basePath, DotEnvFile.FileName));

        IConfigurationRoot configuration = new ConfigurationBuilder()
            .SetBasePath(basePath)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: false)
            .AddUserSecrets(typeof(ConfigurationLoader).Assembly, optional: true)
            .AddEnvironmentVariables(CrossIndexOptions.EnvironmentVariablePrefix)
            .Build();

        RejectRenamedSettings(configuration);

        return configuration;
    }

    /// <summary>
    /// Fails loudly on settings from before the Foundry section was unified.
    /// </summary>
    /// <remarks>
    /// The <c>Embedding</c> section became <c>Foundry</c>, and the knowledge base model settings
    /// moved there from <c>Search</c>. Configuration binding ignores keys it does not recognise, so
    /// without this an old file would bind to defaults and the run would fail later with something
    /// unrelated — an empty endpoint, or agentic retrieval quietly dropping to minimal reasoning
    /// effort. Naming the old key and its replacement costs a few lines and saves the guess.
    /// </remarks>
    private static void RejectRenamedSettings(IConfiguration configuration)
    {
        (string Old, string New)[] renamed =
        [
            ("Embedding:Endpoint", "Foundry:Endpoint"),
            ("Embedding:ApiKey", "Foundry:ApiKey"),
            ("Embedding:Deployment", "Foundry:EmbeddingDeployment"),
            ("Embedding:ModelName", "Foundry:EmbeddingModel"),
            ("Embedding:Dimensions", "Foundry:EmbeddingDimensions"),
            ("Embedding:BlurbDeployment", "Foundry:BatchDeployment"),
            ("Embedding:RerankDeployment", "Foundry:ChatDeployment"),
            ("Search:KnowledgeBaseModelDeployment", "Foundry:QueryPlanningDeployment"),
            ("Search:KnowledgeBaseModelName", "Foundry:QueryPlanningModel"),
            ("Search:KnowledgeBaseModelEndpoint", "Foundry:Endpoint"),
            ("Search:KnowledgeBaseModelApiKey", "Foundry:ApiKey"),
        ];

        List<string> found = [];

        foreach ((string old, string replacement) in renamed)
        {
            foreach (string prefix in new[] { string.Empty, SectionName + ":" })
            {
                if (configuration[prefix + old] is not null)
                {
                    found.Add($"  {prefix + old}  ->  {prefix + replacement}");
                }
            }
        }

        if (found.Count > 0)
        {
            throw new InvalidOperationException(
                "These settings were renamed when the Foundry configuration was unified into one "
                + "endpoint and one key. Update them and re-run:"
                + Environment.NewLine
                + string.Join(Environment.NewLine, found)
                + Environment.NewLine
                + "Environment variables use double underscores, e.g. CIQ_Foundry__Endpoint.");
        }
    }

    /// <summary>
    /// Finds the directory holding <c>appsettings.json</c> by walking up from the running assembly.
    /// </summary>
    /// <remarks>
    /// Settings live at the repository root rather than inside each project, so the CLI and the
    /// data-prep tool read one file and you configure your service exactly once.
    /// </remarks>
    public static string FindSettingsDirectory()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "appsettings.json")))
            {
                return dir.FullName;
            }
        }

        return AppContext.BaseDirectory;
    }

    public static CrossIndexOptions Bind(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new CrossIndexOptions();

        // Accept both a nested "CrossIndexQuery" section and top-level keys, so that
        // CIQ_Search__Endpoint works without callers having to spell the section name.
        IConfigurationSection section = configuration.GetSection(SectionName);
        if (section.Exists())
        {
            section.Bind(options);
        }

        configuration.Bind(options);
        ApplySplitToIndexNames(options);
        return options;
    }

    /// <summary>
    /// Qualifies the stripe index names with the split they hold, unless they were set explicitly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each stripe mode produces a different partitioning of the same corpus, so indexes built for
    /// one are wrong for another. With a single fixed pair of names, switching modes either
    /// overwrites the previous scenario's indexes or — far worse — silently evaluates them, because
    /// stale indexes answer queries perfectly happily. The corpus statistics are already
    /// split-qualified for the same reason.
    /// </para>
    /// <para>
    /// Deriving the names instead makes the scenarios independent by construction: each can be
    /// built once and re-run in any order. The oracle is deliberately not qualified — it holds the
    /// entire corpus regardless of how the stripes are cut, so every scenario shares one baseline
    /// and it is only built once.
    /// </para>
    /// <para>
    /// An explicitly configured name always wins, so pointing the sample at existing indexes still
    /// works.
    /// </para>
    /// </remarks>
    private static void ApplySplitToIndexNames(CrossIndexOptions options)
    {
        string split = options.Corpus.SplitDescriptor;

        if (string.Equals(options.Search.StripeAIndex, SearchServiceOptions.DefaultStripeAIndex, StringComparison.Ordinal))
        {
            options.Search.StripeAIndex = $"books-{split}-a";
        }

        if (string.Equals(options.Search.StripeBIndex, SearchServiceOptions.DefaultStripeBIndex, StringComparison.Ordinal))
        {
            options.Search.StripeBIndex = $"books-{split}-b";
        }

        if (string.Equals(
                options.Search.KnowledgeBaseName,
                SearchServiceOptions.DefaultKnowledgeBaseName,
                StringComparison.Ordinal))
        {
            options.Search.KnowledgeBaseName = $"books-{split}-kb";
        }
    }
}
