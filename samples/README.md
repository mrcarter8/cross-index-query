# Merge patterns

Four self-contained samples, one per pattern, showing exactly how results from two indexes are
combined into one ranked list. Each is deliberately small enough to read in a minute and paste into
your own code.

They are ordered by what they cost you at query time:

| # | pattern | who ranks | extra queries | extra bill | file |
| --- | --- | --- | --- | --- | --- |
| 1 | Query only | your code, arithmetic | none | none | [`Pattern1_QueryOnly.cs`](Pattern1_QueryOnly.cs) |
| 2 | Self-rerank, external | a model you run | none | your model | [`Pattern2_ExternalRerank.cs`](Pattern2_ExternalRerank.cs) |
| 3 | Built-in semantic ranker | the service's cross-encoder | none | semantic meter | [`Pattern3_SemanticRanker.cs`](Pattern3_SemanticRanker.cs) |
| 4 | Agentic retrieval | the service, end to end | replaces yours | agentic meter | [`Pattern4_AgenticRetrieval.cs`](Pattern4_AgenticRetrieval.cs) |

The measured relevance and cost of each is in [`../docs/report.md`](../docs/report.md). The short
version: pattern 1 makes the split disappear for nothing — merged results measured at +0.005 judged
nDCG against the same corpus in one index (p = 0.28, indistinguishable from zero) — and patterns 2-4
are worth paying for because reranking beats BM25, not because striping made them necessary.

## These are the code paths that produced the numbers

Not illustrations of them. `SampleEquivalenceTests` asserts that the merge functions in
[`Pattern1_QueryOnly.cs`](Pattern1_QueryOnly.cs) rank identically to the strategies the evaluation
harness runs, on a fixture built to separate them — so a sample that drifted from the benchmarked
implementation fails the build rather than quietly misleading you. The project compiles as part of
the solution for the same reason.

## The one thing to take away

Do not sort a merged result list by raw `@search.score` from a keyword query, and do not merge on
ranks. The first compares numbers computed against different corpora; the second throws away the
only evidence of how strong each match was. Pattern 1 shows both the wrong way and the two right
ways in about forty lines.

## A number in the results table that is not what it looks like

`global-bm25` scores +0.096 judged nDCG against the service's own BM25, which reads as striping
*improving* relevance. It is not. Recomputing scores client-side is a scoring change that helps a
single index just as much: a control that applies the identical rescorer to an unsplit corpus scores
+0.092 of that on its own, leaving +0.005 attributable to the split.

Adopt these patterns to make striping free, which they demonstrably do. Do not adopt them expecting
striping to make search better.
