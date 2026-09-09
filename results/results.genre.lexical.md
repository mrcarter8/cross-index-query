# Cross-index fusion results

Measured against a lexical run (100 queries, top-10, Equalized candidate budget: oracle 1x50 vs stripes 2x25) on 2026-09-09 09:13:23Z.

Every score compares a fused two-index result against the same query answered by a single
index holding the whole corpus. `1.000` means the split was invisible; lower means striping
cost relevance that the strategy did not recover. These are fidelity numbers, not absolute
relevance judgements.

## Keyword

| Strategy | nDCG@10 | Recall@10 | RBO | Kendall τ | Queries | Compute units | Agentic tokens | Model tokens | p50 ms | p95 ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| `single-index` | 1.000 | 1.000 | 1.000 | 1.000 | 1.0 | 0.0005 | — | — | 67 | 96 |
| `idf-correct-sidecar` | 0.965 | 0.854 | 0.854 | 0.767 | 2.0 | 0.0006 | — | — | 60 | 85 |
| `idf-correct-probe` | 0.963 | 0.848 | 0.849 | 0.775 | 8.5 | 0.0007 | — | — | 348 | 586 |
| `naive-score` | 0.937 | 0.805 | 0.787 | 0.698 | 2.0 | 0.0006 | — | — | 60 | 85 |
| `minmax-norm` | 0.886 | 0.706 | 0.647 | 0.557 | 2.0 | 0.0006 | — | — | 60 | 85 |
| `quota-merge` | 0.883 | 0.698 | 0.626 | 0.479 | 2.0 | 0.0006 | — | — | 60 | 85 |
| `global-rrf` | 0.883 | 0.698 | 0.626 | 0.479 | 2.0 | 0.0006 | — | — | 60 | 85 |
| `zscore-norm` | 0.880 | 0.714 | 0.642 | 0.518 | 2.0 | 0.0006 | — | — | 60 | 85 |
| `interleave` | 0.867 | 0.698 | 0.601 | 0.452 | 2.0 | 0.0006 | — | — | 60 | 85 |
| `selective-one` | 0.858 | 0.682 | 0.679 | 0.776 | 1.0 | 0.0004 | — | — | 64 | 70 |
| `selective-adaptive` | 0.692 | 0.477 | 0.366 | 0.356 | 1.9 | 0.0006 | — | — | 62 | 74 |
| `global-bm25` | 0.672 | 0.455 | 0.328 | 0.312 | 2.0 | 0.0006 | — | — | 62 | 86 |
| `single-index-rescored` | 0.668 | 0.426 | 0.318 | 0.336 | 1.0 | 0.0005 | — | — | 67 | 96 |
| `local-bm25` | 0.609 | 0.396 | 0.282 | 0.277 | 2.0 | 0.0006 | — | — | 61 | 86 |

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
| `selective-one` | 0.920 | 0.816 | -0.104 |
| `selective-adaptive` | 0.776 | 0.636 | -0.141 |
| `single-index-rescored` | 0.722 | 0.633 | -0.089 |
| `global-bm25` | 0.734 | 0.630 | -0.104 |
| `local-bm25` | 0.684 | 0.559 | -0.125 |

### Quality, cost and capacity

Cost appears twice because the two pricing models charge differently for the same work.
**Metered** is consumption billing that applies on every tier — the semantic ranker at $
1.00 per 1K queries, the agentic
retrieval meter at $0.022 per million tokens, both read from the Azure retail prices
API. **Serverless** adds compute at $
0.27 per CU-hour. On a provisioned tier that compute
is already bought by the hour, so the marginal query costs nothing extra and the
consumption shows up in the capacity column instead.

| Strategy | Quality | vs single | Metered $/1K | Serverless $/1K | Capacity | Peak QPS | p50 ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| `global-bm25` | 0.632 | 1.18x | — | $0.160 | 1.28x | 78 % | 62 |
| `single-index-rescored` | 0.628 | 1.17x | — | $0.125 | 1.00x | 100 % | 67 |
| `selective-adaptive` | 0.625 | 1.17x | — | $0.165 | 1.33x | 75 % | 62 |
| `local-bm25` | 0.605 | 1.13x | — | $0.160 | 1.28x | 78 % | 61 |
| `idf-correct-probe` | 0.550 | 1.03x | — | $0.189 | 1.52x | 66 % | 348 |
| `idf-correct-sidecar` | 0.549 | 1.02x | — | $0.160 | 1.28x | 78 % | 60 |
| `single-index` | 0.537 | 1.00x | — | $0.125 | 1.00x | 100 % | 67 |
| `selective-one` | 0.524 | 0.98x | — | $0.100 | 0.81x | 124 % | 64 |
| `naive-score` | 0.522 | 0.97x | — | $0.160 | 1.28x | 78 % | 60 |
| `minmax-norm` | 0.473 | 0.88x | — | $0.160 | 1.28x | 78 % | 60 |
| `zscore-norm` | 0.470 | 0.88x | — | $0.160 | 1.28x | 78 % | 60 |
| `quota-merge` | 0.464 | 0.86x | — | $0.160 | 1.28x | 78 % | 60 |
| `global-rrf` | 0.464 | 0.86x | — | $0.160 | 1.28x | 78 % | 60 |
| `interleave` | 0.456 | 0.85x | — | $0.160 | 1.28x | 78 % | 60 |

`Capacity` is compute consumed per query relative to the single index; `Peak QPS` is the
share of the baseline's peak throughput the same hardware retains as a result. On
serverless, read the serverless column and ignore capacity. On a provisioned tier, read
the metered column and treat capacity as the scaling question — a dash means the work
happens server-side where the client cannot measure it.

### Significance against the single index

Paired over the same queries. `Δ judged` is mean judged nDCG@10 minus the single index;
the interval is a 95% paired bootstrap over 10,000 resamples. `p (Holm)` is the paired
t-test corrected across every strategy in this mode; `p (W)` is the uncorrected Wilcoxon
signed-rank, shown because it is the conservative check. W/L/T counts queries where the
strategy beat, lost to, or tied the single index.

| Strategy | Δ judged | 95% interval | d | p (Holm) | p (W) | W/L/T |
| --- | ---: | :---: | ---: | ---: | ---: | :---: |
| `global-bm25` | +0.096 | [+0.064, +0.127] | 0.59 | &lt;0.0001 | &lt;0.0001 | 74/23/3 |
| `single-index-rescored` | +0.091 | [+0.058, +0.124] | 0.54 | &lt;0.0001 | &lt;0.0001 | 75/22/3 |
| `selective-adaptive` | +0.089 | [+0.057, +0.121] | 0.54 | &lt;0.0001 | &lt;0.0001 | 70/27/3 |
| `local-bm25` | +0.069 | [+0.036, +0.101] | 0.41 | 0.0005 | 0.0002 | 65/34/1 |
| `idf-correct-probe` | +0.014 | [+0.005, +0.023] | 0.30 | 0.0143 | 0.0017 | 61/30/9 |
| `idf-correct-sidecar` | +0.012 | [+0.003, +0.022] | 0.27 | 0.0253 | 0.0043 | 58/32/10 |
| `selective-one` | -0.013 | [-0.038, +0.011] | -0.10 | 0.3328 | 0.9408 | 48/47/5 |
| `naive-score` | -0.015 | [-0.029, -0.001] | -0.21 | 0.0837 | 0.0243 | 36/57/7 |
| `minmax-norm` | -0.064 | [-0.090, -0.038] | -0.48 | &lt;0.0001 | &lt;0.0001 | 31/67/2 |
| `zscore-norm` | -0.066 | [-0.092, -0.041] | -0.51 | &lt;0.0001 | &lt;0.0001 | 27/71/2 |
| `quota-merge` | -0.073 | [-0.098, -0.048] | -0.55 | &lt;0.0001 | &lt;0.0001 | 29/70/1 |
| `global-rrf` | -0.073 | [-0.098, -0.048] | -0.55 | &lt;0.0001 | &lt;0.0001 | 29/70/1 |
| `interleave` | -0.080 | [-0.107, -0.055] | -0.59 | &lt;0.0001 | &lt;0.0001 | 25/74/1 |

An interval that spans zero means the data are consistent with no difference, whatever
the point estimate suggests. Treat those rows as parity, not as small effects.

## Hybrid

| Strategy | nDCG@10 | Recall@10 | RBO | Kendall τ | Queries | Compute units | Agentic tokens | Model tokens | p50 ms | p95 ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| `single-index` | 1.000 | 1.000 | 1.000 | 1.000 | 1.0 | 0.0057 | — | — | 230 | 267 |
| `hybrid-legs` | 0.930 | 0.777 | 0.793 | 0.682 | 2.0 | 0.0079 | — | — | 227 | 242 |
| `naive-score` | 0.832 | 0.710 | 0.649 | 0.526 | 2.0 | 0.0079 | — | — | 227 | 242 |
| `selective-one` | 0.812 | 0.697 | 0.668 | 0.762 | 1.0 | 0.0047 | — | — | 207 | 238 |
| `minmax-norm` | 0.811 | 0.700 | 0.600 | 0.500 | 2.0 | 0.0079 | — | — | 227 | 242 |
| `vector-similarity` | 0.789 | 0.557 | 0.469 | 0.338 | 2.0 | 0.0079 | — | — | 227 | 242 |
| `quota-merge` | 0.786 | 0.629 | 0.565 | 0.461 | 2.0 | 0.0079 | — | — | 227 | 242 |
| `global-rrf` | 0.786 | 0.629 | 0.565 | 0.461 | 2.0 | 0.0079 | — | — | 227 | 242 |
| `interleave` | 0.767 | 0.629 | 0.536 | 0.441 | 2.0 | 0.0079 | — | — | 227 | 242 |
| `idf-correct-sidecar` | 0.755 | 0.484 | 0.447 | 0.419 | 2.0 | 0.0079 | — | — | 227 | 242 |
| `idf-correct-probe` | 0.755 | 0.486 | 0.445 | 0.422 | 2.0 | 0.0079 | — | — | 227 | 242 |
| `zscore-norm` | 0.734 | 0.644 | 0.477 | 0.402 | 2.0 | 0.0079 | — | — | 227 | 242 |
| `selective-adaptive` | 0.678 | 0.497 | 0.382 | 0.191 | 1.9 | 0.0078 | — | — | 202 | 236 |
| `global-bm25` | 0.655 | 0.460 | 0.340 | 0.126 | 2.0 | 0.0079 | — | — | 228 | 243 |
| `single-index-rescored` | 0.648 | 0.423 | 0.308 | 0.121 | 1.0 | 0.0057 | — | — | 230 | 267 |
| `local-bm25` | 0.587 | 0.396 | 0.290 | 0.083 | 2.0 | 0.0079 | — | — | 229 | 243 |

### By query span

Stripe-local queries find their answers in one index; cross-stripe queries need both.
Fusion quality is decided by the second column.

| Strategy | nDCG stripe-local | nDCG cross-stripe | Δ |
| --- | ---: | ---: | ---: |
| `single-index` | 1.000 | 1.000 | 0.000 |
| `hybrid-legs` | 0.957 | 0.912 | -0.045 |
| `naive-score` | 0.811 | 0.846 | 0.035 |
| `minmax-norm` | 0.772 | 0.837 | 0.065 |
| `quota-merge` | 0.728 | 0.825 | 0.096 |
| `global-rrf` | 0.728 | 0.825 | 0.096 |
| `interleave` | 0.721 | 0.798 | 0.078 |
| `zscore-norm` | 0.652 | 0.789 | 0.137 |
| `vector-similarity` | 0.811 | 0.774 | -0.038 |
| `selective-one` | 0.917 | 0.741 | -0.176 |
| `idf-correct-sidecar` | 0.810 | 0.718 | -0.092 |
| `idf-correct-probe` | 0.810 | 0.717 | -0.093 |
| `selective-adaptive` | 0.781 | 0.610 | -0.170 |
| `single-index-rescored` | 0.728 | 0.595 | -0.133 |
| `global-bm25` | 0.747 | 0.594 | -0.153 |
| `local-bm25` | 0.664 | 0.535 | -0.129 |

### Quality, cost and capacity

Cost appears twice because the two pricing models charge differently for the same work.
**Metered** is consumption billing that applies on every tier — the semantic ranker at $
1.00 per 1K queries, the agentic
retrieval meter at $0.022 per million tokens, both read from the Azure retail prices
API. **Serverless** adds compute at $
0.27 per CU-hour. On a provisioned tier that compute
is already bought by the hour, so the marginal query costs nothing extra and the
consumption shows up in the capacity column instead.

| Strategy | Quality | vs single | Metered $/1K | Serverless $/1K | Capacity | Peak QPS | p50 ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| `hybrid-legs` | 0.685 | 1.01x | — | $2.14 | 1.40x | 71 % | 227 |
| `vector-similarity` | 0.680 | 1.00x | — | $2.14 | 1.40x | 71 % | 227 |
| `single-index` | 0.678 | 1.00x | — | $1.53 | 1.00x | 100 % | 230 |
| `selective-adaptive` | 0.654 | 0.96x | — | $2.11 | 1.38x | 72 % | 202 |
| `global-bm25` | 0.654 | 0.96x | — | $2.14 | 1.40x | 71 % | 228 |
| `single-index-rescored` | 0.641 | 0.95x | — | $1.53 | 1.00x | 100 % | 230 |
| `selective-one` | 0.630 | 0.93x | — | $1.26 | 0.83x | 121 % | 207 |
| `local-bm25` | 0.622 | 0.92x | — | $2.14 | 1.40x | 71 % | 229 |
| `naive-score` | 0.617 | 0.91x | — | $2.14 | 1.40x | 71 % | 227 |
| `minmax-norm` | 0.604 | 0.89x | — | $2.14 | 1.40x | 71 % | 227 |
| `quota-merge` | 0.584 | 0.86x | — | $2.14 | 1.40x | 71 % | 227 |
| `global-rrf` | 0.584 | 0.86x | — | $2.14 | 1.40x | 71 % | 227 |
| `interleave` | 0.576 | 0.85x | — | $2.14 | 1.40x | 71 % | 227 |
| `zscore-norm` | 0.566 | 0.83x | — | $2.14 | 1.40x | 71 % | 227 |
| `idf-correct-probe` | 0.555 | 0.82x | — | $2.14 | 1.40x | 71 % | 227 |
| `idf-correct-sidecar` | 0.554 | 0.82x | — | $2.14 | 1.40x | 71 % | 227 |

`Capacity` is compute consumed per query relative to the single index; `Peak QPS` is the
share of the baseline's peak throughput the same hardware retains as a result. On
serverless, read the serverless column and ignore capacity. On a provisioned tier, read
the metered column and treat capacity as the scaling question — a dash means the work
happens server-side where the client cannot measure it.

### Significance against the single index

Paired over the same queries. `Δ judged` is mean judged nDCG@10 minus the single index;
the interval is a 95% paired bootstrap over 10,000 resamples. `p (Holm)` is the paired
t-test corrected across every strategy in this mode; `p (W)` is the uncorrected Wilcoxon
signed-rank, shown because it is the conservative check. W/L/T counts queries where the
strategy beat, lost to, or tied the single index.

| Strategy | Δ judged | 95% interval | d | p (Holm) | p (W) | W/L/T |
| --- | ---: | :---: | ---: | ---: | ---: | :---: |
| `hybrid-legs` | +0.006 | [-0.009, +0.022] | 0.08 | 0.8363 | 0.1507 | 48/39/13 |
| `vector-similarity` | +0.001 | [-0.027, +0.030] | 0.01 | 0.9370 | 0.9885 | 49/48/3 |
| `selective-adaptive` | -0.024 | [-0.052, +0.003] | -0.17 | 0.3762 | 0.1341 | 39/54/7 |
| `global-bm25` | -0.024 | [-0.053, +0.004] | -0.17 | 0.3762 | 0.1525 | 40/56/4 |
| `single-index-rescored` | -0.037 | [-0.066, -0.008] | -0.25 | 0.0743 | 0.0162 | 38/59/3 |
| `selective-one` | -0.048 | [-0.074, -0.025] | -0.38 | 0.0019 | 0.0010 | 30/51/19 |
| `local-bm25` | -0.057 | [-0.086, -0.027] | -0.37 | 0.0021 | &lt;0.0001 | 28/71/1 |
| `naive-score` | -0.062 | [-0.084, -0.039] | -0.53 | &lt;0.0001 | &lt;0.0001 | 26/67/7 |
| `minmax-norm` | -0.075 | [-0.100, -0.049] | -0.58 | &lt;0.0001 | &lt;0.0001 | 29/65/6 |
| `quota-merge` | -0.095 | [-0.124, -0.066] | -0.63 | &lt;0.0001 | &lt;0.0001 | 26/69/5 |
| `global-rrf` | -0.095 | [-0.124, -0.066] | -0.63 | &lt;0.0001 | &lt;0.0001 | 26/69/5 |
| `interleave` | -0.102 | [-0.133, -0.072] | -0.65 | &lt;0.0001 | &lt;0.0001 | 26/69/5 |
| `zscore-norm` | -0.113 | [-0.149, -0.078] | -0.61 | &lt;0.0001 | &lt;0.0001 | 22/74/4 |
| `idf-correct-probe` | -0.124 | [-0.156, -0.092] | -0.74 | &lt;0.0001 | &lt;0.0001 | 20/77/3 |
| `idf-correct-sidecar` | -0.124 | [-0.157, -0.092] | -0.75 | &lt;0.0001 | &lt;0.0001 | 20/77/3 |

An interval that spans zero means the data are consistent with no difference, whatever
the point estimate suggests. Treat those rows as parity, not as small effects.

## Vector

| Strategy | nDCG@10 | Recall@10 | RBO | Kendall τ | Queries | Compute units | Agentic tokens | Model tokens | p50 ms | p95 ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| `single-index` | 1.000 | 1.000 | 1.000 | 1.000 | 1.0 | 0.0006 | — | — | 77 | 92 |
| `naive-score` | 0.974 | 0.960 | 0.962 | 1.000 | 2.0 | 0.0009 | — | — | 65 | 92 |
| `vector-similarity` | 0.974 | 0.960 | 0.962 | 1.000 | 2.0 | 0.0009 | — | — | 65 | 92 |
| `minmax-norm` | 0.811 | 0.659 | 0.615 | 0.620 | 2.0 | 0.0009 | — | — | 65 | 92 |
| `quota-merge` | 0.808 | 0.627 | 0.599 | 0.587 | 2.0 | 0.0009 | — | — | 65 | 92 |
| `global-rrf` | 0.808 | 0.627 | 0.599 | 0.587 | 2.0 | 0.0009 | — | — | 65 | 92 |
| `zscore-norm` | 0.791 | 0.641 | 0.580 | 0.583 | 2.0 | 0.0009 | — | — | 65 | 92 |
| `interleave` | 0.789 | 0.627 | 0.565 | 0.555 | 2.0 | 0.0009 | — | — | 65 | 92 |

### By query span

Stripe-local queries find their answers in one index; cross-stripe queries need both.
Fusion quality is decided by the second column.

| Strategy | nDCG stripe-local | nDCG cross-stripe | Δ |
| --- | ---: | ---: | ---: |
| `single-index` | 1.000 | 1.000 | 0.000 |
| `naive-score` | 0.994 | 0.961 | -0.033 |
| `vector-similarity` | 0.994 | 0.961 | -0.033 |
| `minmax-norm` | 0.756 | 0.848 | 0.093 |
| `quota-merge` | 0.752 | 0.845 | 0.093 |
| `global-rrf` | 0.752 | 0.845 | 0.093 |
| `zscore-norm` | 0.739 | 0.826 | 0.086 |
| `interleave` | 0.742 | 0.820 | 0.078 |

### Quality, cost and capacity

Cost appears twice because the two pricing models charge differently for the same work.
**Metered** is consumption billing that applies on every tier — the semantic ranker at $
1.00 per 1K queries, the agentic
retrieval meter at $0.022 per million tokens, both read from the Azure retail prices
API. **Serverless** adds compute at $
0.27 per CU-hour. On a provisioned tier that compute
is already bought by the hour, so the marginal query costs nothing extra and the
consumption shows up in the capacity column instead.

| Strategy | Quality | vs single | Metered $/1K | Serverless $/1K | Capacity | Peak QPS | p50 ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| `single-index` | 0.685 | 1.00x | — | $0.167 | 1.00x | 100 % | 77 |
| `naive-score` | 0.681 | 0.99x | — | $0.239 | 1.43x | 70 % | 65 |
| `vector-similarity` | 0.681 | 0.99x | — | $0.239 | 1.43x | 70 % | 65 |
| `minmax-norm` | 0.596 | 0.87x | — | $0.239 | 1.43x | 70 % | 65 |
| `quota-merge` | 0.591 | 0.86x | — | $0.239 | 1.43x | 70 % | 65 |
| `global-rrf` | 0.591 | 0.86x | — | $0.239 | 1.43x | 70 % | 65 |
| `zscore-norm` | 0.590 | 0.86x | — | $0.239 | 1.43x | 70 % | 65 |
| `interleave` | 0.584 | 0.85x | — | $0.239 | 1.43x | 70 % | 65 |

`Capacity` is compute consumed per query relative to the single index; `Peak QPS` is the
share of the baseline's peak throughput the same hardware retains as a result. On
serverless, read the serverless column and ignore capacity. On a provisioned tier, read
the metered column and treat capacity as the scaling question — a dash means the work
happens server-side where the client cannot measure it.

### Significance against the single index

Paired over the same queries. `Δ judged` is mean judged nDCG@10 minus the single index;
the interval is a 95% paired bootstrap over 10,000 resamples. `p (Holm)` is the paired
t-test corrected across every strategy in this mode; `p (W)` is the uncorrected Wilcoxon
signed-rank, shown because it is the conservative check. W/L/T counts queries where the
strategy beat, lost to, or tied the single index.

| Strategy | Δ judged | 95% interval | d | p (Holm) | p (W) | W/L/T |
| --- | ---: | :---: | ---: | ---: | ---: | :---: |
| `naive-score` | -0.004 | [-0.011, +0.002] | -0.13 | 0.3766 | 0.1402 | 8/15/77 |
| `vector-similarity` | -0.004 | [-0.011, +0.002] | -0.13 | 0.3766 | 0.1402 | 8/15/77 |
| `minmax-norm` | -0.089 | [-0.120, -0.061] | -0.58 | &lt;0.0001 | &lt;0.0001 | 22/72/6 |
| `quota-merge` | -0.094 | [-0.123, -0.066] | -0.64 | &lt;0.0001 | &lt;0.0001 | 22/73/5 |
| `global-rrf` | -0.094 | [-0.123, -0.066] | -0.64 | &lt;0.0001 | &lt;0.0001 | 22/73/5 |
| `zscore-norm` | -0.095 | [-0.126, -0.067] | -0.64 | &lt;0.0001 | &lt;0.0001 | 21/73/6 |
| `interleave` | -0.101 | [-0.133, -0.072] | -0.65 | &lt;0.0001 | &lt;0.0001 | 21/74/5 |

An interval that spans zero means the data are consistent with no difference, whatever
the point estimate suggests. Treat those rows as parity, not as small effects.

