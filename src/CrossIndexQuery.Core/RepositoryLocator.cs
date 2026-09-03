namespace CrossIndexQuery.Core;

/// <summary>
/// Locates the sample's <c>data</c> directory.
/// </summary>
/// <remarks>
/// The corpus is committed at the repository root but the binaries run from
/// <c>src/&lt;project&gt;/bin/Debug/net10.0</c>, so a relative path only works if you happen to
/// launch from the right place. Walking up from the assembly location makes <c>dotnet run</c>,
/// <c>dotnet test</c>, and a published binary all behave the same.
/// </remarks>
public static class RepositoryLocator
{
    private const string MarkerFile = "genre-map.json";

    /// <summary>
    /// Resolves a data directory. An absolute <paramref name="configured"/> path is honoured as-is;
    /// otherwise the nearest ancestor directory containing <c>data/genre-map.json</c> wins.
    /// </summary>
    public static string ResolveDataDirectory(string configured = "data")
    {
        if (Path.IsPathRooted(configured))
        {
            return configured;
        }

        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, configured);
            if (File.Exists(Path.Combine(candidate, MarkerFile)))
            {
                return candidate;
            }
        }

        // Also try the current working directory, which covers running from a copied output folder.
        string fromCwd = Path.GetFullPath(configured);
        if (File.Exists(Path.Combine(fromCwd, MarkerFile)))
        {
            return fromCwd;
        }

        return fromCwd;
    }

    /// <summary>
    /// Resolves the sample's root directory — the one that contains <c>data</c>.
    /// </summary>
    /// <remarks>
    /// Derived from the resolved data directory rather than searched for separately, so the root
    /// and the corpus can never disagree about where the sample lives. Callers that need somewhere
    /// to write output should use this instead of a relative path: the working directory depends on
    /// how the process was launched, and resolving output against it silently scatters files
    /// outside the sample.
    /// </remarks>
    public static string ResolveRepositoryRoot(string configuredDataDirectory = "data")
    {
        string dataDirectory = Path.GetFullPath(ResolveDataDirectory(configuredDataDirectory));

        return Path.GetDirectoryName(dataDirectory.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            ?? Directory.GetCurrentDirectory();
    }
}
