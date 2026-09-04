using CrossIndexQuery.Core.Configuration;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace CrossIndexQuery.Tests;

/// <summary>
/// Tests that index names follow the split they describe.
/// </summary>
/// <remarks>
/// Indexes built for one stripe mode hold the wrong documents for another, and a stale index answers
/// queries perfectly happily rather than failing — so a name collision between scenarios produces
/// confidently wrong measurements instead of an error. Deriving the names is what makes the
/// scenarios independent, and these tests pin that.
/// </remarks>
public sealed class ConfigurationLoaderTests
{
    private static CrossIndexOptions Bind(params (string Key, string Value)[] settings) =>
        ConfigurationLoader.Bind(new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s =>
                new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build());

    [Fact]
    public void StripeIndexNamesCarryTheSplitTheyHold()
    {
        CrossIndexOptions genre = Bind(("Corpus:StripeMode", "Genre"));

        Assert.Equal("books-genre-a", genre.Search.StripeAIndex);
        Assert.Equal("books-genre-b", genre.Search.StripeBIndex);
    }

    [Fact]
    public void TemporalSplitsAreDistinguishedByTheirCutPoint()
    {
        CrossIndexOptions early = Bind(
            ("Corpus:StripeMode", "Temporal"), ("Corpus:StripeYearCut", "2004"));

        CrossIndexOptions late = Bind(
            ("Corpus:StripeMode", "Temporal"), ("Corpus:StripeYearCut", "2013"));

        Assert.Equal("books-temporal-2004-a", early.Search.StripeAIndex);
        Assert.Equal("books-temporal-2013-a", late.Search.StripeAIndex);
        Assert.NotEqual(early.Search.StripeAIndex, late.Search.StripeAIndex);
    }

    /// <summary>
    /// Two different splits must never share an index name.
    /// </summary>
    /// <remarks>
    /// This is the property that actually matters. Every other test here is a specific instance of
    /// it, and the failure it guards against — one scenario silently reading another's indexes — is
    /// invisible at run time.
    /// </remarks>
    [Fact]
    public void NoTwoSplitsShareAnIndexName()
    {
        CrossIndexOptions[] splits =
        [
            Bind(("Corpus:StripeMode", "Genre")),
            Bind(("Corpus:StripeMode", "Random")),
            Bind(("Corpus:StripeMode", "Temporal"), ("Corpus:StripeYearCut", "2004")),
            Bind(("Corpus:StripeMode", "Temporal"), ("Corpus:StripeYearCut", "2013")),
            Bind(("Corpus:StripeMode", "Temporal"), ("Corpus:StripeYearCut", "2016")),
        ];

        string[] names = [.. splits.SelectMany(s => s.Search.StripeIndexes)];

        Assert.Equal(names.Length, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    /// <summary>
    /// The knowledge base names the indexes it federates, so it is split-scoped too.
    /// </summary>
    [Fact]
    public void KnowledgeBaseNameCarriesTheSplitAsWell()
    {
        Assert.Equal("books-genre-kb", Bind(("Corpus:StripeMode", "Genre")).Search.KnowledgeBaseName);

        Assert.Equal(
            "books-temporal-2013-kb",
            Bind(("Corpus:StripeMode", "Temporal"), ("Corpus:StripeYearCut", "2013"))
                .Search.KnowledgeBaseName);
    }

    /// <summary>
    /// The oracle holds the whole corpus however the stripes are cut, so it is shared.
    /// </summary>
    /// <remarks>
    /// Qualifying it would mean rebuilding the same 10,000-document index for every scenario, and on
    /// a serverless service the repeated bulk load throttles subsequent queries.
    /// </remarks>
    [Fact]
    public void OracleIndexIsSharedAcrossSplits()
    {
        Assert.Equal(
            Bind(("Corpus:StripeMode", "Genre")).Search.OracleIndex,
            Bind(("Corpus:StripeMode", "Temporal")).Search.OracleIndex);
    }

    [Fact]
    public void AnExplicitlyConfiguredNameIsHonoured()
    {
        CrossIndexOptions options = Bind(
            ("Corpus:StripeMode", "Genre"),
            ("Search:StripeAIndex", "my-existing-index"));

        Assert.Equal("my-existing-index", options.Search.StripeAIndex);

        // The one that was not overridden still follows the split.
        Assert.Equal("books-genre-b", options.Search.StripeBIndex);
    }

    [Fact]
    public void CandidateBudgetSplitsTheAllowanceAcrossStripes()
    {
        var equalized = new EvaluationOptions
        {
            PerStripeK = 50,
            CandidateBudget = CandidateBudget.Equalized,
        };

        var perIndex = new EvaluationOptions
        {
            PerStripeK = 50,
            CandidateBudget = CandidateBudget.PerIndex,
        };

        Assert.Equal(25, equalized.CandidatesPerStripe(2));
        Assert.Equal(50, perIndex.CandidatesPerStripe(2));

        // One stripe is not a split at all, so there is nothing to divide.
        Assert.Equal(50, equalized.CandidatesPerStripe(1));
    }
}
