# Cross-index fusion results

Measured against a lexical run (100 queries, top-10, Equalized candidate budget: oracle 1x50 vs stripes 2x25) on 2026-09-04 11:37:41Z.

Every score compares a fused two-index result against the same query answered by a single
index holding the whole corpus. `1.000` means the split was invisible; lower means striping
cost relevance that the strategy did not recover. These are fidelity numbers, not absolute
relevance judgements.

## Keyword

| Strategy | nDCG@10 | Recall@10 | RBO | Kendall τ | Queries | Compute units | p50 ms | p95 ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| `single-index` | 1.000 | 1.000 | 1.000 | 1.000 | 1.0 | 0.0004 | 65 | 93 |
| `idf-correct-sidecar` | 0.965 | 0.854 | 0.854 | 0.767 | 2.0 | 0.0006 | 55 | 59 |
| `idf-correct-probe` | 0.963 | 0.848 | 0.849 | 0.775 | 8.5 | 0.0007 | 309 | 559 |
| `naive-score` | 0.937 | 0.805 | 0.787 | 0.698 | 2.0 | 0.0006 | 55 | 59 |
| `minmax-norm` | 0.886 | 0.706 | 0.647 | 0.557 | 2.0 | 0.0006 | 55 | 59 |
| `quota-merge` | 0.883 | 0.698 | 0.626 | 0.479 | 2.0 | 0.0006 | 55 | 59 |
| `global-rrf` | 0.883 | 0.698 | 0.626 | 0.479 | 2.0 | 0.0006 | 55 | 59 |
| `zscore-norm` | 0.880 | 0.714 | 0.642 | 0.518 | 2.0 | 0.0006 | 55 | 59 |
| `interleave` | 0.867 | 0.698 | 0.601 | 0.452 | 2.0 | 0.0006 | 55 | 59 |
| `global-bm25` | 0.672 | 0.455 | 0.328 | 0.312 | 2.0 | 0.0006 | 57 | 60 |
| `single-index-rescored` | 0.668 | 0.426 | 0.318 | 0.336 | 1.0 | 0.0004 | 65 | 93 |
| `local-bm25` | 0.609 | 0.396 | 0.282 | 0.277 | 2.0 | 0.0006 | 57 | 61 |

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
| `single-index-rescored` | 0.722 | 0.633 | -0.089 |
| `global-bm25` | 0.734 | 0.630 | -0.104 |
| `local-bm25` | 0.684 | 0.559 | -0.125 |

### Significance against the single index

Paired over the same queries. `Δ judged` is mean judged nDCG@10 minus the single index;
the interval is a 95% paired bootstrap over 10,000 resamples. `p (Holm)` is the paired
t-test corrected across every strategy in this mode; `p (W)` is the uncorrected Wilcoxon
signed-rank, shown because it is the conservative check. W/L/T counts queries where the
strategy beat, lost to, or tied the single index.

| Strategy | Δ judged | 95% interval | d | p (Holm) | p (W) | W/L/T |
| --- | ---: | :---: | ---: | ---: | ---: | :---: |
| `global-bm25` | +0.096 | [+0.064, +0.128] | 0.59 | &lt;0.0001 | &lt;0.0001 | 74/23/3 |
| `single-index-rescored` | +0.092 | [+0.058, +0.124] | 0.54 | &lt;0.0001 | &lt;0.0001 | 75/22/3 |
| `local-bm25` | +0.069 | [+0.036, +0.102] | 0.41 | 0.0003 | 0.0001 | 65/34/1 |
| `idf-correct-probe` | +0.014 | [+0.005, +0.023] | 0.30 | 0.0109 | 0.0018 | 61/30/9 |
| `idf-correct-sidecar` | +0.013 | [+0.003, +0.022] | 0.27 | 0.0172 | 0.0043 | 58/32/10 |
| `naive-score` | -0.015 | [-0.029, -0.001] | -0.21 | 0.0411 | 0.0245 | 36/57/7 |
| `minmax-norm` | -0.064 | [-0.091, -0.039] | -0.48 | &lt;0.0001 | &lt;0.0001 | 31/67/2 |
| `zscore-norm` | -0.067 | [-0.092, -0.042] | -0.51 | &lt;0.0001 | &lt;0.0001 | 27/71/2 |
| `quota-merge` | -0.073 | [-0.099, -0.048] | -0.56 | &lt;0.0001 | &lt;0.0001 | 29/70/1 |
| `global-rrf` | -0.073 | [-0.099, -0.048] | -0.56 | &lt;0.0001 | &lt;0.0001 | 29/70/1 |
| `interleave` | -0.081 | [-0.107, -0.055] | -0.59 | &lt;0.0001 | &lt;0.0001 | 25/74/1 |

An interval that spans zero means the data are consistent with no difference, whatever
the point estimate suggests. Treat those rows as parity, not as small effects.

## Hybrid

| Strategy | nDCG@10 | Recall@10 | RBO | Kendall τ | Queries | Compute units | p50 ms | p95 ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| `single-index` | 1.000 | 1.000 | 1.000 | 1.000 | 1.0 | 0.0049 | 229 | 245 |
| `hybrid-legs` | 0.930 | 0.777 | 0.792 | 0.681 | 2.0 | 0.0079 | 215 | 231 |
| `naive-score` | 0.832 | 0.710 | 0.649 | 0.526 | 2.0 | 0.0079 | 214 | 231 |
| `minmax-norm` | 0.811 | 0.699 | 0.600 | 0.499 | 2.0 | 0.0079 | 214 | 231 |
| `vector-similarity` | 0.789 | 0.556 | 0.469 | 0.338 | 2.0 | 0.0079 | 215 | 231 |
| `quota-merge` | 0.786 | 0.629 | 0.565 | 0.462 | 2.0 | 0.0079 | 214 | 231 |
| `global-rrf` | 0.786 | 0.629 | 0.565 | 0.462 | 2.0 | 0.0079 | 214 | 231 |
| `interleave` | 0.767 | 0.629 | 0.536 | 0.442 | 2.0 | 0.0079 | 214 | 231 |
| `idf-correct-sidecar` | 0.755 | 0.484 | 0.447 | 0.419 | 2.0 | 0.0079 | 214 | 231 |
| `idf-correct-probe` | 0.755 | 0.486 | 0.445 | 0.422 | 2.0 | 0.0079 | 214 | 231 |
| `zscore-norm` | 0.734 | 0.644 | 0.477 | 0.402 | 2.0 | 0.0079 | 214 | 231 |
| `global-bm25` | 0.655 | 0.460 | 0.340 | 0.126 | 2.0 | 0.0079 | 216 | 233 |
| `single-index-rescored` | 0.648 | 0.423 | 0.308 | 0.121 | 1.0 | 0.0049 | 229 | 245 |
| `local-bm25` | 0.587 | 0.396 | 0.290 | 0.083 | 2.0 | 0.0079 | 215 | 232 |

### By query span

Stripe-local queries find their answers in one index; cross-stripe queries need both.
Fusion quality is decided by the second column.

| Strategy | nDCG stripe-local | nDCG cross-stripe | Δ |
| --- | ---: | ---: | ---: |
| `single-index` | 1.000 | 1.000 | 0.000 |
| `hybrid-legs` | 0.957 | 0.912 | -0.045 |
| `naive-score` | 0.811 | 0.846 | 0.035 |
| `minmax-norm` | 0.771 | 0.837 | 0.066 |
| `quota-merge` | 0.728 | 0.825 | 0.096 |
| `global-rrf` | 0.728 | 0.825 | 0.096 |
| `interleave` | 0.721 | 0.798 | 0.078 |
| `zscore-norm` | 0.652 | 0.789 | 0.137 |
| `vector-similarity` | 0.811 | 0.774 | -0.038 |
| `idf-correct-sidecar` | 0.810 | 0.718 | -0.092 |
| `idf-correct-probe` | 0.810 | 0.717 | -0.093 |
| `single-index-rescored` | 0.728 | 0.595 | -0.133 |
| `global-bm25` | 0.747 | 0.594 | -0.153 |
| `local-bm25` | 0.664 | 0.535 | -0.129 |

### Significance against the single index

Paired over the same queries. `Δ judged` is mean judged nDCG@10 minus the single index;
the interval is a 95% paired bootstrap over 10,000 resamples. `p (Holm)` is the paired
t-test corrected across every strategy in this mode; `p (W)` is the uncorrected Wilcoxon
signed-rank, shown because it is the conservative check. W/L/T counts queries where the
strategy beat, lost to, or tied the single index.

| Strategy | Δ judged | 95% interval | d | p (Holm) | p (W) | W/L/T |
| --- | ---: | :---: | ---: | ---: | ---: | :---: |
| `hybrid-legs` | +0.007 | [-0.009, +0.022] | 0.08 | 0.8210 | 0.1544 | 48/39/13 |
| `vector-similarity` | +0.002 | [-0.027, +0.031] | 0.01 | 0.9050 | 0.9799 | 49/48/3 |
| `global-bm25` | -0.025 | [-0.053, +0.004] | -0.17 | 0.2997 | 0.1546 | 40/56/4 |
| `single-index-rescored` | -0.039 | [-0.068, -0.009] | -0.25 | 0.0505 | 0.0147 | 38/59/3 |
| `naive-score` | -0.062 | [-0.084, -0.039] | -0.53 | &lt;0.0001 | &lt;0.0001 | 26/67/7 |
| `local-bm25` | -0.064 | [-0.094, -0.034] | -0.41 | 0.0005 | &lt;0.0001 | 28/71/1 |
| `minmax-norm` | -0.075 | [-0.100, -0.049] | -0.58 | &lt;0.0001 | &lt;0.0001 | 29/65/6 |
| `quota-merge` | -0.095 | [-0.124, -0.066] | -0.63 | &lt;0.0001 | &lt;0.0001 | 26/69/5 |
| `global-rrf` | -0.095 | [-0.124, -0.066] | -0.63 | &lt;0.0001 | &lt;0.0001 | 26/69/5 |
| `interleave` | -0.103 | [-0.133, -0.072] | -0.65 | &lt;0.0001 | &lt;0.0001 | 26/69/5 |
| `zscore-norm` | -0.113 | [-0.150, -0.078] | -0.61 | &lt;0.0001 | &lt;0.0001 | 22/74/4 |
| `idf-correct-probe` | -0.124 | [-0.157, -0.092] | -0.74 | &lt;0.0001 | &lt;0.0001 | 20/77/3 |
| `idf-correct-sidecar` | -0.125 | [-0.157, -0.093] | -0.75 | &lt;0.0001 | &lt;0.0001 | 20/77/3 |

An interval that spans zero means the data are consistent with no difference, whatever
the point estimate suggests. Treat those rows as parity, not as small effects.

## Vector

| Strategy | nDCG@10 | Recall@10 | RBO | Kendall τ | Queries | Compute units | p50 ms | p95 ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| `single-index` | 1.000 | 1.000 | 1.000 | 1.000 | 1.0 | 0.0006 | 75 | 78 |
| `naive-score` | 0.974 | 0.960 | 0.962 | 1.000 | 2.0 | 0.0009 | 62 | 64 |
| `vector-similarity` | 0.974 | 0.960 | 0.962 | 1.000 | 2.0 | 0.0009 | 62 | 64 |
| `minmax-norm` | 0.811 | 0.659 | 0.614 | 0.619 | 2.0 | 0.0009 | 62 | 65 |
| `quota-merge` | 0.808 | 0.627 | 0.599 | 0.587 | 2.0 | 0.0009 | 62 | 64 |
| `global-rrf` | 0.808 | 0.627 | 0.599 | 0.587 | 2.0 | 0.0009 | 62 | 64 |
| `zscore-norm` | 0.791 | 0.641 | 0.580 | 0.582 | 2.0 | 0.0009 | 62 | 65 |
| `interleave` | 0.789 | 0.627 | 0.565 | 0.555 | 2.0 | 0.0009 | 62 | 64 |

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

### Significance against the single index

Paired over the same queries. `Δ judged` is mean judged nDCG@10 minus the single index;
the interval is a 95% paired bootstrap over 10,000 resamples. `p (Holm)` is the paired
t-test corrected across every strategy in this mode; `p (W)` is the uncorrected Wilcoxon
signed-rank, shown because it is the conservative check. W/L/T counts queries where the
strategy beat, lost to, or tied the single index.

| Strategy | Δ judged | 95% interval | d | p (Holm) | p (W) | W/L/T |
| --- | ---: | :---: | ---: | ---: | ---: | :---: |
| `naive-score` | -0.004 | [-0.011, +0.002] | -0.13 | 0.3854 | 0.1402 | 8/15/77 |
| `vector-similarity` | -0.004 | [-0.011, +0.002] | -0.13 | 0.3854 | 0.1402 | 8/15/77 |
| `minmax-norm` | -0.089 | [-0.121, -0.061] | -0.58 | &lt;0.0001 | &lt;0.0001 | 22/72/6 |
| `quota-merge` | -0.094 | [-0.123, -0.066] | -0.64 | &lt;0.0001 | &lt;0.0001 | 22/73/5 |
| `global-rrf` | -0.094 | [-0.123, -0.066] | -0.64 | &lt;0.0001 | &lt;0.0001 | 22/73/5 |
| `zscore-norm` | -0.096 | [-0.126, -0.067] | -0.64 | &lt;0.0001 | &lt;0.0001 | 21/73/6 |
| `interleave` | -0.102 | [-0.133, -0.072] | -0.65 | &lt;0.0001 | &lt;0.0001 | 21/74/5 |

An interval that spans zero means the data are consistent with no difference, whatever
the point estimate suggests. Treat those rows as parity, not as small effects.

