using Microsoft.Extensions.Configuration;

namespace CrossIndexQuery.Core.Configuration;

/// <summary>
/// Builds <see cref="CrossIndexOptions"/> from the standard layered sources.
/// </summary>
/// <remarks>
/// Precedence, lowest to highest: <c>appsettings.json</c> (committed, placeholders only),
/// <c>appsettings.Development.json</c> (git-ignored, your service), user secrets, then
/// environment variables prefixed <c>CIQ_</c>. The environment layer is what <c>azd</c> and CI
/// populate, so an automated run never needs a file on disk.
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

        return new ConfigurationBuilder()
            .SetBasePath(basePath)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: false)
            .AddUserSecrets(typeof(ConfigurationLoader).Assembly, optional: true)
            .AddEnvironmentVariables(CrossIndexOptions.EnvironmentVariablePrefix)
            .Build();
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
