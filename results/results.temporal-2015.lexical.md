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

Measured against a lexical run (100 queries, top-10, Equalized candidate budget: oracle 1x50 vs stripes 2x25) on 2026-09-02 23:23:37Z.

Every score compares a fused two-index result against the same query answered by a single
index holding the whole corpus. `1.000` means the split was invisible; lower means striping
cost relevance that the strategy did not recover. These are fidelity numbers, not absolute
relevance judgements.

## Keyword

| Strategy | nDCG@10 | Recall@10 | RBO | Kendall τ | Queries | Compute units | p50 ms | p95 ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| `single-index` | 1.000 | 1.000 | 1.000 | 1.000 | 1.0 | 0.0004 | 60 | 88 |
| `idf-correct-sidecar` | 0.990 | 0.962 | 0.828 | 0.939 | 2.0 | 0.0004 | 52 | 55 |
| `idf-correct-probe` | 0.990 | 0.960 | 0.826 | 0.936 | 8.5 | 0.0016 | 305 | 557 |
| `naive-score` | 0.989 | 0.956 | 0.825 | 0.940 | 2.0 | 0.0004 | 52 | 55 |
| `quota-merge` | 0.884 | 0.899 | 0.671 | 0.759 | 2.0 | 0.0004 | 52 | 55 |
| `interleave` | 0.764 | 0.531 | 0.526 | 0.620 | 2.0 | 0.0004 | 52 | 55 |
| `minmax-norm` | 0.748 | 0.633 | 0.518 | 0.643 | 2.0 | 0.0004 | 52 | 55 |
| `zscore-norm` | 0.733 | 0.610 | 0.506 | 0.607 | 2.0 | 0.0004 | 52 | 55 |
| `global-rrf` | 0.704 | 0.531 | 0.466 | 0.589 | 2.0 | 0.0004 | 52 | 55 |

### By query span

Stripe-local queries find their answers in one index; cross-stripe queries need both.
Fusion quality is decided by the second column.

| Strategy | nDCG stripe-local | nDCG cross-stripe | Δ |
| --- | ---: | ---: | ---: |
| `single-index` | 1.000 | 1.000 | 0.000 |
| `idf-correct-sidecar` | 0.996 | 0.987 | -0.009 |
| `idf-correct-probe` | 0.996 | 0.985 | -0.011 |
| `naive-score` | 0.996 | 0.985 | -0.011 |
| `quota-merge` | 0.880 | 0.886 | 0.006 |
| `interleave` | 0.750 | 0.774 | 0.023 |
| `minmax-norm` | 0.737 | 0.756 | 0.019 |
| `zscore-norm` | 0.722 | 0.740 | 0.017 |
| `global-rrf` | 0.695 | 0.710 | 0.015 |

## Vector

| Strategy | nDCG@10 | Recall@10 | RBO | Kendall τ | Queries | Compute units | p50 ms | p95 ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| `single-index` | 1.000 | 1.000 | 1.000 | 1.000 | 1.0 | 0.0006 | 74 | 106 |
| `naive-score` | 0.984 | 0.973 | 0.838 | 1.000 | 2.0 | 0.0008 | 61 | 94 |
| `vector-similarity` | 0.984 | 0.973 | 0.838 | 1.000 | 2.0 | 0.0008 | 61 | 94 |
| `quota-merge` | 0.883 | 0.895 | 0.674 | 0.829 | 2.0 | 0.0008 | 61 | 94 |
| `interleave` | 0.749 | 0.509 | 0.512 | 0.690 | 2.0 | 0.0008 | 61 | 94 |
| `global-rrf` | 0.701 | 0.509 | 0.460 | 0.658 | 2.0 | 0.0008 | 61 | 94 |
| `minmax-norm` | 0.695 | 0.523 | 0.464 | 0.709 | 2.0 | 0.0008 | 61 | 94 |
| `zscore-norm` | 0.677 | 0.527 | 0.440 | 0.647 | 2.0 | 0.0008 | 61 | 94 |

### By query span

Stripe-local queries find their answers in one index; cross-stripe queries need both.
Fusion quality is decided by the second column.

| Strategy | nDCG stripe-local | nDCG cross-stripe | Δ |
| --- | ---: | ---: | ---: |
| `single-index` | 1.000 | 1.000 | 0.000 |
| `naive-score` | 0.998 | 0.975 | -0.023 |
| `vector-similarity` | 0.998 | 0.975 | -0.023 |
| `quota-merge` | 0.885 | 0.881 | -0.005 |
| `interleave` | 0.744 | 0.753 | 0.009 |
| `global-rrf` | 0.697 | 0.703 | 0.006 |
| `minmax-norm` | 0.693 | 0.697 | 0.004 |
| `zscore-norm` | 0.665 | 0.685 | 0.020 |

