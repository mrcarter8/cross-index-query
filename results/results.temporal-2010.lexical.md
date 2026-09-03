# Cross-index fusion results

Measured against a lexical run (100 queries, top-10, Equalized candidate budget: oracle 1x50 vs stripes 2x25) on 2026-09-03 00:41:16Z.

Every score compares a fused two-index result against the same query answered by a single
index holding the whole corpus. `1.000` means the split was invisible; lower means striping
cost relevance that the strategy did not recover. These are fidelity numbers, not absolute
relevance judgements.

## Keyword

| Strategy | nDCG@10 | Recall@10 | RBO | Kendall τ | Queries | Compute units | p50 ms | p95 ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| `single-index` | 1.000 | 1.000 | 1.000 | 1.000 | 1.0 | 0.0004 | 61 | 89 |
| `idf-correct-probe` | 0.978 | 0.914 | 0.787 | 0.842 | 8.5 | 0.0019 | 315 | 575 |
| `idf-correct-sidecar` | 0.977 | 0.904 | 0.782 | 0.840 | 2.0 | 0.0005 | 54 | 57 |
| `naive-score` | 0.974 | 0.905 | 0.772 | 0.806 | 2.0 | 0.0005 | 54 | 57 |
| `interleave` | 0.938 | 0.749 | 0.662 | 0.643 | 2.0 | 0.0005 | 54 | 57 |
| `quota-merge` | 0.933 | 0.850 | 0.663 | 0.580 | 2.0 | 0.0005 | 54 | 57 |
| `zscore-norm` | 0.927 | 0.754 | 0.649 | 0.604 | 2.0 | 0.0005 | 54 | 57 |
| `global-rrf` | 0.920 | 0.749 | 0.633 | 0.579 | 2.0 | 0.0005 | 54 | 57 |
| `minmax-norm` | 0.909 | 0.737 | 0.626 | 0.628 | 2.0 | 0.0005 | 54 | 57 |

### By query span

Stripe-local queries find their answers in one index; cross-stripe queries need both.
Fusion quality is decided by the second column.

| Strategy | nDCG stripe-local | nDCG cross-stripe | Δ |
| --- | ---: | ---: | ---: |
| `single-index` | 1.000 | 1.000 | 0.000 |
| `idf-correct-probe` | 0.988 | 0.972 | -0.017 |
| `idf-correct-sidecar` | 0.985 | 0.971 | -0.013 |
| `naive-score` | 0.978 | 0.971 | -0.007 |
| `quota-merge` | 0.915 | 0.944 | 0.029 |
| `zscore-norm` | 0.907 | 0.941 | 0.034 |
| `interleave` | 0.935 | 0.940 | 0.006 |
| `global-rrf` | 0.899 | 0.934 | 0.036 |
| `minmax-norm` | 0.892 | 0.920 | 0.028 |

## Vector

| Strategy | nDCG@10 | Recall@10 | RBO | Kendall τ | Queries | Compute units | p50 ms | p95 ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| `single-index` | 1.000 | 1.000 | 1.000 | 1.000 | 1.0 | 0.0006 | 74 | 101 |
| `naive-score` | 0.970 | 0.953 | 0.823 | 1.000 | 2.0 | 0.0009 | 63 | 96 |
| `vector-similarity` | 0.970 | 0.953 | 0.823 | 1.000 | 2.0 | 0.0009 | 63 | 96 |
| `interleave` | 0.912 | 0.707 | 0.639 | 0.672 | 2.0 | 0.0009 | 63 | 96 |
| `quota-merge` | 0.907 | 0.813 | 0.637 | 0.598 | 2.0 | 0.0009 | 63 | 96 |
| `minmax-norm` | 0.890 | 0.727 | 0.622 | 0.685 | 2.0 | 0.0009 | 63 | 96 |
| `global-rrf` | 0.889 | 0.707 | 0.605 | 0.611 | 2.0 | 0.0009 | 63 | 96 |
| `zscore-norm` | 0.882 | 0.724 | 0.606 | 0.628 | 2.0 | 0.0009 | 63 | 96 |

### By query span

Stripe-local queries find their answers in one index; cross-stripe queries need both.
Fusion quality is decided by the second column.

| Strategy | nDCG stripe-local | nDCG cross-stripe | Δ |
| --- | ---: | ---: | ---: |
| `single-index` | 1.000 | 1.000 | 0.000 |
| `naive-score` | 0.994 | 0.954 | -0.040 |
| `vector-similarity` | 0.994 | 0.954 | -0.040 |
| `interleave` | 0.922 | 0.905 | -0.017 |
| `quota-merge` | 0.925 | 0.895 | -0.030 |
| `minmax-norm` | 0.900 | 0.884 | -0.016 |
| `global-rrf` | 0.902 | 0.881 | -0.021 |
| `zscore-norm` | 0.887 | 0.879 | -0.007 |

