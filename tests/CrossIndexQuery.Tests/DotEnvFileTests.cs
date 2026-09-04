using CrossIndexQuery.Core.Configuration;
using Xunit;

namespace CrossIndexQuery.Tests;

/// <summary>
/// Covers the <c>.env</c> parser, which holds credentials and therefore has to be boring.
/// </summary>
public class DotEnvFileTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"ciq-{Guid.NewGuid():N}.env");
    private readonly List<string> _touched = [];

    [Fact]
    public void ParsesKeyValuePairs()
    {
        Write("CIQ_Search__Endpoint=https://example.search.windows.net");

        Assert.Equal(1, DotEnvFile.Load(_path));
        Assert.Equal("https://example.search.windows.net", Read("CIQ_Search__Endpoint"));
    }

    [Fact]
    public void IgnoresBlankLinesAndComments()
    {
        Write(
            "# a comment",
            string.Empty,
            "   ",
            "CIQ_Evaluation__TopK=10",
            "# CIQ_Evaluation__TopK=999");

        Assert.Equal(1, DotEnvFile.Load(_path));
        Assert.Equal("10", Read("CIQ_Evaluation__TopK"));
    }

    /// <summary>
    /// A value already exported must win, so a one-off override is not defeated by a file.
    /// </summary>
    [Fact]
    public void DoesNotOverrideAnExistingVariable()
    {
        Set("CIQ_Corpus__StripeMode", "Temporal");
        Write("CIQ_Corpus__StripeMode=Genre");

        Assert.Equal(0, DotEnvFile.Load(_path));
        Assert.Equal("Temporal", Read("CIQ_Corpus__StripeMode"));
    }

    [Fact]
    public void StripsSurroundingQuotes()
    {
        Write(
            "CIQ_Search__ApiKey=\"quoted value\"",
            "CIQ_Search__OracleIndex='single quoted'");

        DotEnvFile.Load(_path);

        Assert.Equal("quoted value", Read("CIQ_Search__ApiKey"));
        Assert.Equal("single quoted", Read("CIQ_Search__OracleIndex"));
    }

    /// <summary>
    /// A trailing comment is stripped only from unquoted values.
    /// </summary>
    /// <remarks>
    /// The load-bearing case for a file holding secrets: API keys and connection strings contain
    /// <c>#</c>, and truncating one at that character would produce a credential that is wrong in a
    /// way no error message would explain.
    /// </remarks>
    [Fact]
    public void KeepsHashesInsideQuotedValues()
    {
        Write(
            "CIQ_Search__ApiKey=\"abc#def#ghi\"",
            "CIQ_Search__OracleIndex=books  # the baseline index");

        DotEnvFile.Load(_path);

        Assert.Equal("abc#def#ghi", Read("CIQ_Search__ApiKey"));
        Assert.Equal("books", Read("CIQ_Search__OracleIndex"));
    }

    /// <summary>
    /// Accepts an <c>export</c> prefix, so the same file can be sourced by a shell.
    /// </summary>
    [Fact]
    public void AcceptsExportPrefix()
    {
        Write("export CIQ_Search__Endpoint=https://exported.example.net");

        DotEnvFile.Load(_path);

        Assert.Equal("https://exported.example.net", Read("CIQ_Search__Endpoint"));
    }

    [Fact]
    public void PreservesEqualsSignsInsideValues()
    {
        Write("CIQ_Search__ApiKey=a=b=c==");

        DotEnvFile.Load(_path);

        Assert.Equal("a=b=c==", Read("CIQ_Search__ApiKey"));
    }

    [Fact]
    public void SkipsMalformedLines()
    {
        Write("no equals sign here", "=novalue", "CIQ_Evaluation__TopK=5");

        Assert.Equal(1, DotEnvFile.Load(_path));
        Assert.Equal("5", Read("CIQ_Evaluation__TopK"));
    }

    /// <summary>
    /// A missing file is the normal case, not an error.
    /// </summary>
    [Fact]
    public void MissingFileIsNotAnError() =>
        Assert.Equal(0, DotEnvFile.Load(Path.Combine(Path.GetTempPath(), "ciq-absent.env")));

    private void Write(params string[] lines) => File.WriteAllLines(_path, lines);

    private string? Read(string key)
    {
        _touched.Add(key);
        return Environment.GetEnvironmentVariable(key);
    }

    private void Set(string key, string value)
    {
        _touched.Add(key);
        Environment.SetEnvironmentVariable(key, value);
    }

    public void Dispose()
    {
        // The parser writes to real process state, so every variable a test touched has to be
        // cleared or it leaks into whichever test runs next.
        foreach (string key in _touched)
        {
            Environment.SetEnvironmentVariable(key, null);
        }

        if (File.Exists(_path))
        {
            File.Delete(_path);
        }

        GC.SuppressFinalize(this);
    }
}
