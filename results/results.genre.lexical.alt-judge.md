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

Measured against a lexical run (100 queries, top-10, Equalized candidate budget: oracle 1x50 vs stripes 2x25) on 2026-09-03 14:46:11Z.

Every score compares a fused two-index result against the same query answered by a single
index holding the whole corpus. `1.000` means the split was invisible; lower means striping
cost relevance that the strategy did not recover. These are fidelity numbers, not absolute
relevance judgements.

## Keyword

| Strategy | nDCG@10 | Recall@10 | RBO | Kendall τ | Queries | Compute units | p50 ms | p95 ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| `single-index` | 1.000 | 1.000 | 1.000 | 1.000 | 1.0 | 0.0004 | 66 | 93 |
| `idf-correct-sidecar` | 0.965 | 0.854 | 0.755 | 0.767 | 2.0 | 0.0006 | 61 | 85 |
| `idf-correct-probe` | 0.963 | 0.848 | 0.751 | 0.775 | 8.5 | 0.0020 | 329 | 615 |
| `naive-score` | 0.937 | 0.805 | 0.700 | 0.698 | 2.0 | 0.0006 | 61 | 85 |
| `minmax-norm` | 0.886 | 0.706 | 0.592 | 0.557 | 2.0 | 0.0006 | 61 | 85 |
| `quota-merge` | 0.883 | 0.698 | 0.576 | 0.479 | 2.0 | 0.0006 | 61 | 85 |
| `global-rrf` | 0.883 | 0.698 | 0.576 | 0.479 | 2.0 | 0.0006 | 61 | 85 |
| `zscore-norm` | 0.880 | 0.714 | 0.590 | 0.518 | 2.0 | 0.0006 | 61 | 85 |
| `interleave` | 0.867 | 0.698 | 0.559 | 0.452 | 2.0 | 0.0006 | 61 | 85 |
| `global-bm25` | 0.672 | 0.455 | 0.344 | 0.312 | 2.0 | 0.0006 | 63 | 86 |

### By query span

Stripe-local queries find their answers in one index; cross-stripe queries need both.
Fusion quality is decided by the second column.

| Strategy | nDCG stripe-local | nDCG cross-stripe | Δ |
| --- | ---: | ---: | ---: |
| `single-index` | 1.000 | 1.000 | 0.000 |
| `idf-correct-sidecar` | 0.976 | 0.957 | -0.019 |
| `idf-correct-probe` | 0.975 | 0.955 | -0.020 |
| `naive-score` | 0.947 | 0.930 | -0.017 |
| `quota-merge` | 0.845 | 0.908 | 0.063 |
| `global-rrf` | 0.845 | 0.908 | 0.063 |
| `zscore-norm` | 0.839 | 0.907 | 0.068 |
| `minmax-norm` | 0.854 | 0.907 | 0.053 |
| `interleave` | 0.835 | 0.888 | 0.054 |
| `global-bm25` | 0.734 | 0.630 | -0.104 |

## Vector

| Strategy | nDCG@10 | Recall@10 | RBO | Kendall τ | Queries | Compute units | p50 ms | p95 ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| `single-index` | 1.000 | 1.000 | 1.000 | 1.000 | 1.0 | 0.0006 | 76 | 105 |
| `naive-score` | 0.974 | 0.960 | 0.827 | 1.000 | 2.0 | 0.0009 | 66 | 97 |
| `vector-similarity` | 0.974 | 0.960 | 0.827 | 1.000 | 2.0 | 0.0009 | 66 | 97 |
| `minmax-norm` | 0.811 | 0.659 | 0.549 | 0.620 | 2.0 | 0.0009 | 66 | 97 |
| `quota-merge` | 0.808 | 0.627 | 0.534 | 0.587 | 2.0 | 0.0009 | 66 | 97 |
| `global-rrf` | 0.808 | 0.627 | 0.534 | 0.587 | 2.0 | 0.0009 | 66 | 97 |
| `zscore-norm` | 0.791 | 0.641 | 0.523 | 0.582 | 2.0 | 0.0009 | 66 | 97 |
| `interleave` | 0.789 | 0.627 | 0.512 | 0.555 | 2.0 | 0.0009 | 66 | 97 |

### By query span

Stripe-local queries find their answers in one index; cross-stripe queries need both.
Fusion quality is decided by the second column.

| Strategy | nDCG stripe-local | nDCG cross-stripe | Δ |
| --- | ---: | ---: | ---: |
| `single-index` | 1.000 | 1.000 | 0.000 |
| `naive-score` | 0.994 | 0.961 | -0.033 |
| `vector-similarity` | 0.994 | 0.961 | -0.033 |
| `minmax-norm` | 0.755 | 0.848 | 0.093 |
| `quota-merge` | 0.752 | 0.845 | 0.093 |
| `global-rrf` | 0.752 | 0.845 | 0.093 |
| `zscore-norm` | 0.739 | 0.826 | 0.086 |
| `interleave` | 0.742 | 0.820 | 0.078 |

## Hybrid

| Strategy | nDCG@10 | Recall@10 | RBO | Kendall τ | Queries | Compute units | p50 ms | p95 ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| `single-index` | 1.000 | 1.000 | 1.000 | 1.000 | 1.0 | 0.0049 | 201 | 235 |
| `hybrid-legs` | 0.930 | 0.776 | 0.701 | 0.681 | 2.0 | 0.0073 | 188 | 216 |
| `naive-score` | 0.832 | 0.710 | 0.581 | 0.526 | 2.0 | 0.0073 | 188 | 216 |
| `minmax-norm` | 0.811 | 0.699 | 0.547 | 0.500 | 2.0 | 0.0073 | 188 | 216 |
| `vector-similarity` | 0.789 | 0.556 | 0.454 | 0.338 | 2.0 | 0.0073 | 188 | 216 |
| `quota-merge` | 0.786 | 0.629 | 0.512 | 0.461 | 2.0 | 0.0073 | 188 | 216 |
| `global-rrf` | 0.786 | 0.629 | 0.512 | 0.461 | 2.0 | 0.0073 | 188 | 216 |
| `interleave` | 0.767 | 0.629 | 0.493 | 0.441 | 2.0 | 0.0073 | 188 | 216 |
| `idf-correct-sidecar` | 0.755 | 0.484 | 0.413 | 0.419 | 2.0 | 0.0073 | 188 | 216 |
| `idf-correct-probe` | 0.755 | 0.486 | 0.412 | 0.422 | 2.0 | 0.0073 | 188 | 216 |
| `zscore-norm` | 0.734 | 0.644 | 0.457 | 0.402 | 2.0 | 0.0073 | 188 | 216 |
| `global-bm25` | 0.655 | 0.460 | 0.342 | 0.126 | 2.0 | 0.0073 | 190 | 218 |

### By query span

Stripe-local queries find their answers in one index; cross-stripe queries need both.
Fusion quality is decided by the second column.

| Strategy | nDCG stripe-local | nDCG cross-stripe | Δ |
| --- | ---: | ---: | ---: |
| `single-index` | 1.000 | 1.000 | 0.000 |
| `hybrid-legs` | 0.957 | 0.912 | -0.045 |
| `naive-score` | 0.811 | 0.846 | 0.035 |
| `minmax-norm` | 0.772 | 0.837 | 0.066 |
| `quota-merge` | 0.728 | 0.825 | 0.096 |
| `global-rrf` | 0.728 | 0.825 | 0.096 |
| `interleave` | 0.721 | 0.798 | 0.078 |
| `zscore-norm` | 0.652 | 0.789 | 0.137 |
| `vector-similarity` | 0.811 | 0.774 | -0.038 |
| `idf-correct-sidecar` | 0.810 | 0.718 | -0.092 |
| `idf-correct-probe` | 0.810 | 0.717 | -0.093 |
| `global-bm25` | 0.747 | 0.594 | -0.153 |

