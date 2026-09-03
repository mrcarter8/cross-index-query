using System.Security.Cryptography;
using System.Text;
using CrossIndexQuery.Core.Configuration;
using CrossIndexQuery.Core.Models;

namespace CrossIndexQuery.Core.Indexing;

/// <summary>
/// Decides which stripe index a document belongs to.
/// </summary>
/// <remarks>
/// <para>
/// The routing rule is the sample's independent variable, because it controls how far the two
/// stripes' corpus statistics drift apart:
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///     <see cref="StripeMode.Random"/> — a deterministic hash split. Both stripes end up as
///     random samples of the same distribution, so their document frequencies and average
///     document lengths stay close and even naive score merging looks acceptable. This is the
///     control condition, and it is the one most likely to lull you into shipping a naive merge.
///     </description>
///   </item>
///   <item>
///     <description>
///     <see cref="StripeMode.Genre"/> — a split along a business axis. This is what actually
///     happens when a corpus outgrows one index: it gets divided by tenant, product line, or
///     subject. Vocabulary diverges, IDF diverges with it, and identical BM25 scores from the two
///     stripes stop meaning the same thing.
///     </description>
///   </item>
///   <item>
///     <description>
///     <see cref="StripeMode.Temporal"/> — a split by arrival time, which is what actually happens
///     when a corpus outgrows one index in production. Nobody redistributes terabytes to balance
///     two indexes; they freeze the full one and point new writes at a new one. The stripes end up
///     wildly unequal in size, and BM25's IDF depends on index size, so the smaller index judges
///     every term it holds to be commoner than it really is.
///     </description>
///   </item>
/// </list>
/// <para>
/// Routing is deterministic in both modes so that rebuilding the indexes reproduces the same
/// assignment and evaluation runs stay comparable.
/// </para>
/// </remarks>
public sealed class StripeRouter
{
    private readonly StripeMode _mode;
    private readonly int _seed;
    private readonly int _yearCut;
    private readonly string _stripeA;
    private readonly string _stripeB;
    private readonly HashSet<string> _stripeAGenres;
    private readonly HashSet<string> _stripeBGenres;

    public StripeRouter(SearchServiceOptions search, CorpusOptions corpus, GenreMap genreMap)
    {
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(corpus);
        ArgumentNullException.ThrowIfNull(genreMap);

        _mode = corpus.StripeMode;
        _seed = corpus.StripeSeed;
        _yearCut = corpus.StripeYearCut;
        _stripeA = search.StripeAIndex;
        _stripeB = search.StripeBIndex;
        _stripeAGenres = new HashSet<string>(genreMap.StripeAGenres, StringComparer.OrdinalIgnoreCase);
        _stripeBGenres = new HashSet<string>(genreMap.StripeBGenres, StringComparer.OrdinalIgnoreCase);
    }

    public StripeMode Mode => _mode;

    /// <summary>Returns the index name that should hold <paramref name="document"/>.</summary>
    public string Route(BookDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (_mode == StripeMode.Genre && !string.IsNullOrEmpty(document.Genre))
        {
            if (_stripeAGenres.Contains(document.Genre))
            {
                return _stripeA;
            }

            if (_stripeBGenres.Contains(document.Genre))
            {
                return _stripeB;
            }
        }

        // Everything already indexed stays put; anything newer goes to the index that was added to
        // hold it. A document with no publication year has no position on this axis, so it falls
        // through to the hash split rather than being assigned arbitrarily to the frozen index.
        if (_mode == StripeMode.Temporal && document.PublicationYear is { } year)
        {
            return year <= _yearCut ? _stripeA : _stripeB;
        }

        // Random mode, or a document the active mode could not place.
        return HashToStripe(document.Id);
    }

    private string HashToStripe(string id)
    {
        // Runs once per document at index time, so clarity beats micro-optimization.
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes($"{id}:{_seed}"), hash);
        return (hash[0] & 1) == 0 ? _stripeA : _stripeB;
    }
}
