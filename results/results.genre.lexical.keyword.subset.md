# Cross-index fusion results

Measured against a lexical run (100 queries, top-10, Equalized candidate budget: oracle 1x50 vs stripes 2x25) on 2026-09-08 21:51:35Z.

Every score compares a fused two-index result against the same query answered by a single
index holding the whole corpus. `1.000` means the split was invisible; lower means striping
cost relevance that the strategy did not recover. These are fidelity numbers, not absolute
relevance judgements.

## Keyword

| Strategy | nDCG@10 | Recall@10 | RBO | Kendall τ | Queries | Compute units | Model tokens | p50 ms | p95 ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| `single-index` | 1.000 | 1.000 | 1.000 | 1.000 | 1.0 | 0.0004 | — | 65 | 92 |
| `idf-correct-sidecar` | 0.965 | 0.854 | 0.854 | 0.767 | 2.0 | 0.0006 | — | 60 | 82 |
| `naive-score` | 0.937 | 0.805 | 0.787 | 0.698 | 2.0 | 0.0006 | — | 60 | 82 |
| `selective-one` | 0.858 | 0.682 | 0.679 | 0.776 | 1.0 | 0.0004 | — | 65 | 92 |
| `selective-adaptive` | 0.692 | 0.477 | 0.366 | 0.356 | 1.9 | 0.0006 | — | 62 | 71 |
| `global-bm25` | 0.672 | 0.455 | 0.328 | 0.312 | 2.0 | 0.0006 | — | 62 | 83 |
| `single-index-rescored` | 0.668 | 0.426 | 0.318 | 0.336 | 1.0 | 0.0004 | — | 65 | 92 |

### By query span

Stripe-local queries find their answers in one index; cross-stripe queries need both.
Fusion quality is decided by the second column.

| Strategy | nDCG stripe-local | nDCG cross-stripe | Δ |
| --- | ---: | ---: | ---: |
| `single-index` | 1.000 | 1.000 | 0.000 |
| `idf-correct-sidecar` | 0.976 | 0.957 | -0.019 |
| `naive-score` | 0.947 | 0.930 | -0.017 |
| `selective-one` | 0.920 | 0.816 | -0.104 |
| `selective-adaptive` | 0.776 | 0.636 | -0.141 |
| `single-index-rescored` | 0.722 | 0.633 | -0.089 |
| `global-bm25` | 0.734 | 0.630 | -0.104 |

### Quality, cost and latency

Cost assumes $0.27 per compute-unit hour and $1.00 per 1K semantic ranker queries, both read from the Azure retail
prices API, plus $0.40 per million model tokens, which is an assumption rather than a
measurement — substitute your own rate and the token-bearing rows move with it.

| Strategy | Quality | vs single | $/1K queries | vs single | p50 ms | vs single | Tokens/query |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| `global-bm25` | 0.632 | 1.18x | $0.157 | 1.36x | 62 | 0.94x | — |
| `single-index-rescored` | 0.628 | 1.17x | $0.116 | 1.00x | 65 | 1.00x | — |
| `selective-adaptive` | 0.625 | 1.17x | $0.155 | 1.34x | 62 | 0.95x | — |
| `idf-correct-sidecar` | 0.549 | 1.02x | $0.157 | 1.36x | 60 | 0.92x | — |
| `single-index` | 0.537 | 1.00x | $0.116 | 1.00x | 65 | 1.00x | — |
| `selective-one` | 0.524 | 0.98x | $0.112 | 0.97x | 65 | 1.00x | — |
| `naive-score` | 0.522 | 0.97x | $0.157 | 1.36x | 60 | 0.92x | — |

A multiple below 1.00x in the quality column is relevance lost; above 1.00x in the cost
or latency columns is what recovering it charged you. Read the three together — a row
that wins on quality alone has not necessarily won.

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
| `idf-correct-sidecar` | +0.012 | [+0.003, +0.022] | 0.27 | 0.0253 | 0.0043 | 58/32/10 |
| `selective-one` | -0.013 | [-0.038, +0.011] | -0.10 | 0.3328 | 0.9408 | 48/47/5 |
| `naive-score` | -0.015 | [-0.029, -0.001] | -0.21 | 0.0837 | 0.0243 | 36/57/7 |

An interval that spans zero means the data are consistent with no difference, whatever
the point estimate suggests. Treat those rows as parity, not as small effects.

