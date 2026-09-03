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
        return options;
    }
}
