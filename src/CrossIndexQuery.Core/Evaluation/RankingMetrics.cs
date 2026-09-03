namespace CrossIndexQuery.Core.Evaluation;

/// <summary>
/// Metrics comparing a fused result list against the list a single index produced.
/// </summary>
/// <remarks>
/// <para>
/// Every metric here treats the oracle index — one index holding the entire corpus — as ground
/// truth. That is a deliberate choice and it defines what the sample can and cannot claim. These
/// numbers measure <em>fidelity to the single-index result</em>, not absolute relevance. A fusion
/// strategy scoring 1.0 has reproduced what one index would have returned; it has not been shown
/// to have returned the objectively best documents, because no human judged them.
/// </para>
/// <para>
/// That is the right target. The question this sample answers is "my data no longer fits in one
/// index — how much do I lose, and how much can I get back?" The honest baseline for that question
/// is the result you would have had if it still fit.
/// </para>
/// </remarks>
public static class RankingMetrics
{
    /// <summary>
    /// Normalized discounted cumulative gain against the oracle ordering.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Relevance grades are taken from the oracle's own ranking: a document the oracle placed at
    /// position <c>r</c> is worth <c>1/log2(r+1)</c>. Documents the oracle did not return at all
    /// score zero. The ideal list is therefore the oracle list itself, so a perfect reproduction
    /// scores 1.0 and any reordering is penalized in proportion to how far up the list it happened.
    /// </para>
    /// <para>
    /// The double logarithmic discount — once to derive the grade, once for position — is what
    /// makes this metric sensitive where it matters. Swapping ranks 1 and 2 costs real score;
    /// swapping 40 and 41 costs almost nothing, which matches what a user would notice.
    /// </para>
    /// </remarks>
    public static double NormalizedDiscountedCumulativeGain(
        IReadOnlyList<string> candidate,
        IReadOnlyList<string> oracle,
        int k)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(oracle);

        if (oracle.Count == 0 || k <= 0)
        {
            return 0d;
        }

        Dictionary<string, double> gains = new(StringComparer.Ordinal);
        for (int i = 0; i < oracle.Count; i++)
        {
            gains[oracle[i]] = 1d / Math.Log2(i + 2);
        }

        double dcg = 0;
        for (int i = 0; i < Math.Min(k, candidate.Count); i++)
        {
            if (gains.TryGetValue(candidate[i], out double gain))
            {
                dcg += gain / Math.Log2(i + 2);
            }
        }

        double idcg = 0;
        for (int i = 0; i < Math.Min(k, oracle.Count); i++)
        {
            idcg += gains[oracle[i]] / Math.Log2(i + 2);
        }

        return idcg > double.Epsilon ? dcg / idcg : 0d;
    }

    /// <summary>
    /// Normalized discounted cumulative gain against absolute relevance judgments.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the counterpart to <see cref="NormalizedDiscountedCumulativeGain"/> and answers the
    /// question that one cannot. Fidelity nDCG grades a document by where the oracle ranked it,
    /// which makes the oracle correct by definition; a striped result that surfaces a genuinely
    /// better document the oracle never retrieved is scored as an error. Here the grade comes from a
    /// judge with no stake in either retrieval path, so the oracle is just another system and can
    /// lose.
    /// </para>
    /// <para>
    /// Gain is <c>2^grade - 1</c> on the four-point scale, the standard exponential form: it makes
    /// one highly relevant document worth more than two marginal ones, which is the behaviour a
    /// ranking metric should have. The ideal list is the best ordering of the documents that were
    /// judged for this query, so a run cannot be penalised for failing to retrieve something nobody
    /// judged — but it also gets no credit for it.
    /// </para>
    /// <para>
    /// Unjudged documents score zero, the standard pooling convention. That biases against a
    /// strategy returning documents no other approach found, which is why coverage is reported
    /// alongside: a strategy with low coverage has an asterisk on its score, and the way to remove
    /// the asterisk is to widen the pool and judge again.
    /// </para>
    /// </remarks>
    public static double JudgedNdcg(
        IReadOnlyList<string> candidate,
        IReadOnlyDictionary<string, int> grades,
        int k)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(grades);

        if (grades.Count == 0 || k <= 0)
        {
            return 0d;
        }

        double dcg = 0;
        for (int i = 0; i < Math.Min(k, candidate.Count); i++)
        {
            if (grades.TryGetValue(candidate[i], out int grade) && grade > 0)
            {
                dcg += (Math.Pow(2, grade) - 1) / Math.Log2(i + 2);
            }
        }

        double idcg = 0;
        int position = 0;
        foreach (int grade in grades.Values.Where(g => g > 0).OrderByDescending(g => g).Take(k))
        {
            idcg += (Math.Pow(2, grade) - 1) / Math.Log2(position + 2);
            position++;
        }

        return idcg > double.Epsilon ? dcg / idcg : 0d;
    }

    /// <summary>
    /// Fraction of the returned documents that carry a relevance judgment.
    /// </summary>
    /// <remarks>
    /// Pooling only judges what some approach actually returned, so a strategy that surfaces
    /// documents nobody else did has them silently counted as irrelevant. Reporting coverage makes
    /// that visible instead of letting it masquerade as poor relevance.
    /// </remarks>
    public static double JudgedCoverage(
        IReadOnlyList<string> candidate,
        IReadOnlyDictionary<string, int> grades,
        int k)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(grades);

        int considered = Math.Min(k, candidate.Count);
        if (considered == 0)
        {
            return 0d;
        }

        int judged = 0;
        for (int i = 0; i < considered; i++)
        {
            if (grades.ContainsKey(candidate[i]))
            {
                judged++;
            }
        }

        return judged / (double)considered;
    }

    /// <summary>
    /// Fraction of the oracle's top <paramref name="k"/> that the candidate also surfaced in its
    /// top <paramref name="k"/>.
    /// </summary>
    /// <remarks>
    /// Order-insensitive, which makes it the right companion to nDCG rather than a substitute.
    /// A strategy can retrieve every correct document and still rank them badly; comparing the two
    /// metrics separates "found the wrong documents" from "found the right ones in the wrong order",
    /// and those have completely different fixes.
    /// </remarks>
    public static double RecallAtK(IReadOnlyList<string> candidate, IReadOnlyList<string> oracle, int k)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(oracle);

        if (oracle.Count == 0 || k <= 0)
        {
            return 0d;
        }

        HashSet<string> truth = new(oracle.Take(k), StringComparer.Ordinal);
        if (truth.Count == 0)
        {
            return 0d;
        }

        int hits = candidate.Take(k).Count(truth.Contains);
        return hits / (double)truth.Count;
    }

    /// <summary>Set overlap of the two top-<paramref name="k"/> lists, ignoring order entirely.</summary>
    public static double JaccardAtK(IReadOnlyList<string> candidate, IReadOnlyList<string> oracle, int k)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(oracle);

        HashSet<string> a = new(candidate.Take(k), StringComparer.Ordinal);
        HashSet<string> b = new(oracle.Take(k), StringComparer.Ordinal);

        if (a.Count == 0 && b.Count == 0)
        {
            return 1d;
        }

        int intersection = a.Count(b.Contains);
        int union = a.Count + b.Count - intersection;

        return union > 0 ? intersection / (double)union : 0d;
    }

    /// <summary>
    /// Kendall's tau-b rank correlation over the documents both lists contain.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Counts how often the two lists agree about which of a pair of documents should come first.
    /// Returns 1.0 for identical orderings, 0.0 for unrelated ones, and negative values when the
    /// candidate systematically inverts the oracle.
    /// </para>
    /// <para>
    /// Restricted to the intersection by necessity — a pair cannot be concordant or discordant if
    /// one list never mentions one of its members. That makes it a measure of ordering quality
    /// alone, cleanly separated from retrieval quality, which recall already covers.
    /// </para>
    /// </remarks>
    public static double KendallTau(IReadOnlyList<string> candidate, IReadOnlyList<string> oracle)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(oracle);

        Dictionary<string, int> oraclePositions = new(StringComparer.Ordinal);
        for (int i = 0; i < oracle.Count; i++)
        {
            oraclePositions.TryAdd(oracle[i], i);
        }

        List<int> shared = [];
        foreach (string id in candidate)
        {
            if (oraclePositions.TryGetValue(id, out int position))
            {
                shared.Add(position);
            }
        }

        if (shared.Count < 2)
        {
            return 0d;
        }

        int concordant = 0;
        int discordant = 0;

        for (int i = 0; i < shared.Count - 1; i++)
        {
            for (int j = i + 1; j < shared.Count; j++)
            {
                // i precedes j in the candidate list by construction, so agreement means the
                // oracle also placed i before j.
                if (shared[i] < shared[j])
                {
                    concordant++;
                }
                else if (shared[i] > shared[j])
                {
                    discordant++;
                }
            }
        }

        int total = concordant + discordant;
        return total > 0 ? (concordant - discordant) / (double)total : 0d;
    }

    /// <summary>
    /// Rank-biased overlap: top-weighted similarity between two ranked lists.
    /// </summary>
    /// <param name="p">
    /// Persistence, between 0 and 1. Lower values concentrate weight at the top of the list;
    /// 0.9 spreads roughly 86% of the weight over the first ten positions.
    /// </param>
    /// <remarks>
    /// <para>
    /// The most appropriate single number for this sample. Unlike Kendall's tau it does not require
    /// the lists to contain the same documents, and unlike recall it is order-sensitive — which
    /// matters because fused lists routinely contain documents the oracle's top ten omitted and
    /// vice versa.
    /// </para>
    /// <para>
    /// It also weights disagreement the way a user experiences it. A wrong result at position 1 is
    /// far more damaging than a wrong result at position 40, and rank-biased overlap is one of the
    /// few list-similarity measures that encodes that directly rather than treating all positions
    /// alike.
    /// </para>
    /// <para>
    /// The result is normalised by the maximum value attainable at this depth. Rank-biased overlap
    /// is defined over infinite lists, and evaluating the sum over a finite prefix of length
    /// <c>k</c> caps it at <c>1 - p^k</c> — so two <em>identical</em> ten-item lists would otherwise
    /// score 0.65 rather than 1. Dividing that out restores the property that identical lists score
    /// 1 and disjoint lists score 0, and makes values comparable across different result-set sizes.
    /// The alternative convention extrapolates the unseen tail instead, which assumes agreement
    /// beyond the prefix; that assumption is not safe here, because the sample is specifically
    /// measuring lists that diverge.
    /// </para>
    /// </remarks>
    public static double RankBiasedOverlap(
        IReadOnlyList<string> candidate,
        IReadOnlyList<string> oracle,
        double p = 0.9)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(oracle);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(p);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(p, 1d);

        int depth = Math.Max(candidate.Count, oracle.Count);
        if (depth == 0)
        {
            return 1d;
        }

        HashSet<string> seenCandidate = new(StringComparer.Ordinal);
        HashSet<string> seenOracle = new(StringComparer.Ordinal);

        double sum = 0;
        int overlap = 0;

        for (int d = 0; d < depth; d++)
        {
            if (d < candidate.Count && seenCandidate.Add(candidate[d]) && seenOracle.Contains(candidate[d]))
            {
                overlap++;
            }

            if (d < oracle.Count && seenOracle.Add(oracle[d]) && seenCandidate.Contains(oracle[d]))
            {
                overlap++;
            }

            sum += Math.Pow(p, d) * (overlap / (double)(d + 1));
        }

        double maximum = 1 - Math.Pow(p, depth);
        return maximum > 0 ? (1 - p) * sum / maximum : 0d;
    }
}
