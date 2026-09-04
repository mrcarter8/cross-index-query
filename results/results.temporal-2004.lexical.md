# Cross-index fusion results

> **Provenance note.** This file was produced before the 2026-09-04 corrections and is retained
> because its judged-relevance numbers back section 6 of the report. Two caveats apply:
>
> - **The `RBO` column is wrong.** Rank-biased overlap was computed without a depth cutoff, so it
>   compared a 10-item candidate list against a 50-item reference and understated every strategy.
>   The other columns are unaffected. Current files carry the corrected values.
> - **No significance table, and no controls.** The `local-bm25` and `single-index-rescored` controls
>   did not exist yet. See the report's "The controls" section before reading any `global-bm25` row
>   in this file as a striping result.
>
> Regenerating this file requires rebuilding its indexes, which re-uploads the corpus. It has not
> been redone because its conclusions rest on judged nDCG, which the corrections did not touch.

Measured against a lexical run (100 queries, top-10, Equalized candidate budget: oracle 1x50 vs stripes 2x25) on 2026-09-03 08:49:48Z.

Every score compares a fused two-index result against the same query answered by a single
index holding the whole corpus. `1.000` means the split was invisible; lower means striping
cost relevance that the strategy did not recover. These are fidelity numbers, not absolute
relevance judgements.

## Keyword

| Strategy | nDCG@10 | Recall@10 | RBO | Kendall τ | Queries | Compute units | p50 ms | p95 ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| `single-index` | 1.000 | 1.000 | 1.000 | 1.000 | 1.0 | 0.0004 | 67 | 94 |
| `idf-correct-sidecar` | 0.975 | 0.883 | 0.775 | 0.824 | 2.0 | 0.0006 | 58 | 84 |
| `idf-correct-probe` | 0.974 | 0.896 | 0.774 | 0.823 | 8.5 | 0.0007 | 351 | 592 |
| `naive-score` | 0.969 | 0.873 | 0.760 | 0.787 | 2.0 | 0.0006 | 58 | 84 |
| `zscore-norm` | 0.940 | 0.818 | 0.685 | 0.662 | 2.0 | 0.0006 | 58 | 84 |
| `quota-merge` | 0.935 | 0.803 | 0.666 | 0.617 | 2.0 | 0.0006 | 58 | 84 |
| `global-rrf` | 0.935 | 0.803 | 0.666 | 0.617 | 2.0 | 0.0006 | 58 | 84 |
| `interleave` | 0.932 | 0.803 | 0.660 | 0.608 | 2.0 | 0.0006 | 58 | 84 |
| `minmax-norm` | 0.928 | 0.815 | 0.667 | 0.660 | 2.0 | 0.0006 | 58 | 84 |

### By query span

Stripe-local queries find their answers in one index; cross-stripe queries need both.
Fusion quality is decided by the second column.

| Strategy | nDCG stripe-local | nDCG cross-stripe | Δ |
| --- | ---: | ---: | ---: |
| `single-index` | 1.000 | 1.000 | 0.000 |
| `naive-score` | 0.967 | 0.970 | 0.003 |
| `idf-correct-sidecar` | 0.985 | 0.967 | -0.018 |
| `idf-correct-probe` | 0.988 | 0.964 | -0.024 |
| `zscore-norm` | 0.936 | 0.943 | 0.006 |
| `quota-merge` | 0.929 | 0.939 | 0.010 |
| `global-rrf` | 0.929 | 0.939 | 0.010 |
| `interleave` | 0.933 | 0.932 | -0.001 |
| `minmax-norm` | 0.925 | 0.930 | 0.005 |

