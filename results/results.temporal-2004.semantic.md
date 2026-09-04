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

Measured against a lexical run (100 queries, top-10, Equalized candidate budget: oracle 1x50 vs stripes 2x25) on 2026-09-03 08:44:30Z.

Every score compares a fused two-index result against the same query answered by a single
index holding the whole corpus. `1.000` means the split was invisible; lower means striping
cost relevance that the strategy did not recover. These are fidelity numbers, not absolute
relevance judgements.

## Keyword

| Strategy | nDCG@10 | Recall@10 | RBO | Kendall τ | Queries | Compute units | p50 ms | p95 ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| `single-index` | 1.000 | 1.000 | 1.000 | 1.000 | 1.0 | 0.0004 | 117 | 150 |
| `semantic-score` | 0.835 | 0.749 | 0.674 | 0.998 | 2.0 | 0.0008 | 133 | 160 |
| `semantic-rerank` | 0.835 | 0.749 | 0.674 | 0.998 | 4.0 | 0.0014 | 240 | 277 |
| `interleave` | 0.777 | 0.669 | 0.560 | 0.707 | 2.0 | 0.0008 | 133 | 160 |
| `quota-merge` | 0.771 | 0.669 | 0.548 | 0.686 | 2.0 | 0.0008 | 133 | 161 |
| `global-rrf` | 0.771 | 0.669 | 0.548 | 0.686 | 2.0 | 0.0008 | 133 | 160 |
| `idf-correct-probe` | 0.661 | 0.469 | 0.316 | 0.090 | 8.5 | 0.0022 | 407 | 664 |
| `naive-score` | 0.660 | 0.467 | 0.317 | 0.095 | 2.0 | 0.0008 | 133 | 162 |
| `idf-correct-sidecar` | 0.659 | 0.464 | 0.316 | 0.096 | 2.0 | 0.0008 | 133 | 160 |
| `zscore-norm` | 0.645 | 0.446 | 0.303 | 0.075 | 2.0 | 0.0008 | 133 | 161 |
| `minmax-norm` | 0.645 | 0.451 | 0.302 | 0.066 | 2.0 | 0.0008 | 133 | 161 |

### By query span

Stripe-local queries find their answers in one index; cross-stripe queries need both.
Fusion quality is decided by the second column.

| Strategy | nDCG stripe-local | nDCG cross-stripe | Δ |
| --- | ---: | ---: | ---: |
| `single-index` | 1.000 | 1.000 | 0.000 |
| `semantic-score` | 0.906 | 0.787 | -0.118 |
| `semantic-rerank` | 0.906 | 0.787 | -0.118 |
| `quota-merge` | 0.806 | 0.748 | -0.058 |
| `global-rrf` | 0.806 | 0.748 | -0.058 |
| `interleave` | 0.831 | 0.741 | -0.091 |
| `minmax-norm` | 0.668 | 0.629 | -0.039 |
| `naive-score` | 0.708 | 0.628 | -0.080 |
| `idf-correct-probe` | 0.712 | 0.628 | -0.084 |
| `idf-correct-sidecar` | 0.713 | 0.623 | -0.089 |
| `zscore-norm` | 0.685 | 0.619 | -0.066 |

