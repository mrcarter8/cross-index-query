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
version: pattern 1 recovers essentially all of the striping loss for nothing, and patterns 2-4 are
worth paying for because reranking beats BM25 — not because striping made them necessary.

## The one thing to take away

Do not sort a merged result list by raw `@search.score` from a keyword query, and do not merge on
ranks. The first compares numbers computed against different corpora; the second throws away the
only evidence of how strong each match was. Pattern 1 shows both the wrong way and the two right
ways in about forty lines.
