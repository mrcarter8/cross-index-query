namespace CrossIndexQuery.Core.Evaluation;

/// <summary>A single query in the committed evaluation set.</summary>
/// <param name="Id">Stable identifier so results can be joined across runs.</param>
/// <param name="Text">The query as a user would type it.</param>
/// <param name="Shape">Head, torso or tail — how much of the corpus plausibly matches.</param>
/// <param name="Span">Whether the query's likely answers sit inside one stripe or cross both.</param>
/// <param name="Intent">Whether the query rewards lexical matching or conceptual similarity.</param>
public sealed record EvaluationQuery(
    string Id,
    string Text,
    QueryShape Shape,
    QuerySpan Span,
    QueryIntent Intent);

/// <summary>How much of the corpus a query plausibly matches.</summary>
public enum QueryShape
{
    /// <summary>Broad, matches a large fraction of the corpus.</summary>
    Head,

    /// <summary>Moderately specific.</summary>
    Torso,

    /// <summary>Highly specific, few true matches.</summary>
    Tail,
}

/// <summary>
/// Whether a query's answers are concentrated in one stripe or spread across both.
/// </summary>
/// <remarks>
/// The single most important dimension in the query set. When every good answer lives in one
/// stripe, the other stripe contributes noise that any reasonable strategy discards, and fusion
/// barely matters. Damage from striping concentrates almost entirely in queries whose answers
/// straddle the split, because those are the only ones where two incompatible score scales have to
/// be reconciled rather than merely compared to nothing. Reporting a single average across both
/// kinds would hide the effect the sample exists to demonstrate.
/// </remarks>
public enum QuerySpan
{
    /// <summary>Good answers are concentrated in a single stripe.</summary>
    StripeLocal,

    /// <summary>Good answers exist in both stripes.</summary>
    CrossStripe,
}

/// <summary>What kind of matching a query rewards.</summary>
public enum QueryIntent
{
    /// <summary>Specific terms that should be matched literally.</summary>
    Lexical,

    /// <summary>Describes an idea whose wording will not appear verbatim.</summary>
    Conceptual,

    /// <summary>Rewards both literal terms and conceptual similarity.</summary>
    Mixed,
}
