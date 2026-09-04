using System.Diagnostics.CodeAnalysis;

namespace CrossIndexQuery.Core.Configuration;

/// <summary>
/// Loads a <c>.env</c> file into the process environment.
/// </summary>
/// <remarks>
/// <para>
/// The sample already reads configuration from environment variables prefixed <c>CIQ_</c>, so the
/// simplest way to support a <c>.env</c> file is to populate that same layer before configuration
/// is built. Nothing downstream needs to know the file exists.
/// </para>
/// <para>
/// Existing environment variables always win. A value exported in the shell is a deliberate
/// override for one command, and a file on disk should not silently defeat it — that is the
/// behaviour every other dotenv implementation has, and surprising people here would be
/// particularly unkind given the file's whole purpose is to hold credentials.
/// </para>
/// <para>
/// This is deliberately a small hand-rolled parser rather than a dependency. The format it needs to
/// support is <c>KEY=VALUE</c> with comments and optional quoting; adding a package for that would
/// be more supply chain than the problem deserves.
/// </para>
/// </remarks>
public static class DotEnvFile
{
    /// <summary>Conventional file name.</summary>
    public const string FileName = ".env";

    /// <summary>
    /// Loads <c>.env</c> from the repository root, if one exists.
    /// </summary>
    /// <returns>The number of variables set, or zero when no file was found.</returns>
    public static int LoadFromRepositoryRoot(string dataDirectoryHint)
    {
        string root;
        try
        {
            root = RepositoryLocator.ResolveRepositoryRoot(dataDirectoryHint);
        }
        catch (DirectoryNotFoundException)
        {
            return 0;
        }

        return Load(Path.Combine(root, FileName));
    }

    /// <summary>
    /// Loads one <c>.env</c> file.
    /// </summary>
    /// <returns>The number of variables set, or zero when the file does not exist.</returns>
    public static int Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            return 0;
        }

        int applied = 0;

        foreach (string line in File.ReadLines(path))
        {
            if (!TryParse(line, out string? key, out string? value))
            {
                continue;
            }

            // Already set in the real environment: leave it alone.
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)))
            {
                continue;
            }

            Environment.SetEnvironmentVariable(key, value);
            applied++;
        }

        return applied;
    }

    /// <summary>
    /// Parses one line into a key and value.
    /// </summary>
    /// <remarks>
    /// Handles the subset of the format that matters here: blank lines, <c>#</c> comments, an
    /// optional <c>export</c> prefix so a file can double as a shell script, and single or double
    /// quotes around values. A trailing comment is only stripped from unquoted values, because a
    /// <c>#</c> inside quotes is part of the value — and keys and connection strings contain them.
    /// </remarks>
    private static bool TryParse(
        string line,
        [NotNullWhen(true)] out string? key,
        [NotNullWhen(true)] out string? value)
    {
        key = null;
        value = null;

        ReadOnlySpan<char> span = line.AsSpan().Trim();

        if (span.IsEmpty || span[0] == '#')
        {
            return false;
        }

        if (span.StartsWith("export ", StringComparison.Ordinal))
        {
            span = span["export ".Length..].TrimStart();
        }

        int separator = span.IndexOf('=');
        if (separator <= 0)
        {
            return false;
        }

        key = span[..separator].Trim().ToString();
        if (key.Length == 0)
        {
            return false;
        }

        ReadOnlySpan<char> raw = span[(separator + 1)..].Trim();

        if (raw.Length >= 2
            && ((raw[0] == '"' && raw[^1] == '"') || (raw[0] == '\'' && raw[^1] == '\'')))
        {
            value = raw[1..^1].ToString();
            return true;
        }

        int comment = raw.IndexOf('#');
        if (comment >= 0)
        {
            raw = raw[..comment].TrimEnd();
        }

        value = raw.ToString();
        return true;
    }
}
