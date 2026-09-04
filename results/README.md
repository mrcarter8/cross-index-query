# Results

Every number in [`../docs/report.md`](../docs/report.md) comes from a file in this directory. The
CSVs carry one row per (query, strategy, mode), so any claim in the report can be recomputed
without an Azure subscription:

```
dotnet run --project ../src/CrossIndexQuery.Cli -- compare \
  --results results.genre.lexical.csv --candidate global-bm25
```

`compare` runs a paired bootstrap, a paired t-test and a Wilcoxon signed-rank test over the
committed per-query scores. It needs no credentials and costs nothing.

## Naming

```
results.{split}.{tier}[.{modes}][.alt-judge].{csv,md}
```

- **split** — `genre` for the intentional split, `temporal-{year}` for the split-to-scale cuts.
- **tier** — `lexical` or `semantic`, meaning whether the semantic ranker was on.
- **modes** — present only when the run covered some retrieval modes rather than all of them.
  A full sweep keeps the short name. Without this, two runs differing only in `--modes` would
  overwrite each other, which is a bug this study actually hit.
- **alt-judge** — scored against the second, independent judge rather than the primary one.

`judgment-pool.{split}.{tier}.json` is the union of every document any strategy returned, which is
what gets submitted for grading. It is not qualified by mode, because a judgment belongs to a
(query, document) pair regardless of which mode surfaced it.

## Current files

| file | what it is |
| --- | --- |
| `results.genre.lexical.*` | The main run. Keyword, hybrid and vector, no reranking. Carries the controls. |
| `results.genre.lexical.vector.*` | Vector mode under **exhaustive** nearest-neighbour search. Shows striping is exactly free once ANN approximation is removed. |
| `results.genre.semantic.keyword.*` | The reranked tier: patterns 2, 3 and 4. |

## Files retained for provenance

The `temporal-*` files and `results.genre.lexical.alt-judge.*` predate the 2026-09-04 corrections.
Each carries a note at the top saying so. Their **`RBO` column is wrong** — rank-biased overlap was
computed without a depth cutoff — and they have no significance table and no controls. Their judged
nDCG figures are unaffected, which is what section 6 of the report cites them for.

They have not been regenerated because doing so means rebuilding their indexes, and bulk upload
throttles the service for several minutes afterwards. Re-running them is a legitimate exercise if
you have a service to spare:

```
$env:CIQ_Corpus__StripeMode = 'Temporal'
$env:CIQ_Corpus__TemporalCutoffYear = '2004'
dotnet run --project ../src/CrossIndexQuery.Cli -- init --recreate
dotnet run --project ../src/CrossIndexQuery.Cli -- evaluate --modes Keyword
```

## Reading the columns

| column | means |
| --- | --- |
| `judgedNdcg` | **Absolute relevance**, from an independent judge. This is the column that decides anything. |
| `judgedCoverage` | Fraction of returned documents that carry a judgment. Compare strategies only at similar coverage — unjudged documents count as irrelevant, which penalises whichever strategy found things nobody else did. |
| `ndcg`, `recall`, `rbo`, `kendallTau` | **Fidelity** to the single index, not quality. A strategy that reorders results into something *better* scores low here. Useful for showing what striping changed; useless for showing whether the change was good. |
| `computeUnits` | Read from the service's own response header, not estimated. |
| `stripeMix` | How many of the returned documents came from each index. The fastest way to see a merge going wrong. |
