# Cross-index fusion results

Measured against a semantic run (100 queries, top-10, Equalized candidate budget: oracle 1x50 vs stripes 2x25) on 2026-09-04 12:24:21Z.

Every score compares a fused two-index result against the same query answered by a single
index holding the whole corpus. `1.000` means the split was invisible; lower means striping
cost relevance that the strategy did not recover. These are fidelity numbers, not absolute
relevance judgements.

## Keyword

| Strategy | nDCG@10 | Recall@10 | RBO | Kendall τ | Queries | Compute units | p50 ms | p95 ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| `single-index` | 1.000 | 1.000 | 1.000 | 1.000 | 1.0 | 0.0004 | 155 | 177 |
| `semantic-score` | 0.849 | 0.770 | 0.817 | 0.988 | 2.0 | 0.0008 | 149 | 260 |
| `semantic-rerank` | 0.849 | 0.770 | 0.817 | 0.988 | 4.0 | 0.0014 | 254 | 401 |
| `agentic-retrieval` | 0.843 | 0.757 | 0.811 | 0.988 | 0.0 | 0.0000 | 331 | 565 |
| `quota-merge` | 0.722 | 0.583 | 0.553 | 0.635 | 2.0 | 0.0008 | 149 | 260 |
| `global-rrf` | 0.722 | 0.583 | 0.553 | 0.635 | 2.0 | 0.0008 | 149 | 276 |
| `interleave` | 0.708 | 0.583 | 0.537 | 0.627 | 2.0 | 0.0008 | 149 | 260 |
| `idf-correct-probe` | 0.680 | 0.486 | 0.309 | 0.116 | 8.5 | 0.0009 | 440 | 683 |
| `idf-correct-sidecar` | 0.679 | 0.485 | 0.307 | 0.120 | 2.0 | 0.0008 | 150 | 260 |
| `external-rerank` | 0.665 | 0.478 | 0.334 | 0.147 | 2.0 | 0.0008 | 23117 | 29720 |
| `single-index-rescored` | 0.660 | 0.433 | 0.303 | 0.166 | 1.0 | 0.0004 | 155 | 177 |
| `naive-score` | 0.653 | 0.449 | 0.291 | 0.115 | 2.0 | 0.0008 | 149 | 260 |
| `global-bm25` | 0.647 | 0.463 | 0.321 | 0.192 | 2.0 | 0.0008 | 151 | 261 |
| `minmax-norm` | 0.609 | 0.397 | 0.249 | 0.087 | 2.0 | 0.0008 | 150 | 260 |
| `zscore-norm` | 0.608 | 0.387 | 0.245 | 0.094 | 2.0 | 0.0008 | 150 | 260 |
| `local-bm25` | 0.595 | 0.421 | 0.295 | 0.141 | 2.0 | 0.0008 | 151 | 261 |

### By query span

Stripe-local queries find their answers in one index; cross-stripe queries need both.
Fusion quality is decided by the second column.

| Strategy | nDCG stripe-local | nDCG cross-stripe | Δ |
| --- | ---: | ---: | ---: |
| `single-index` | 1.000 | 1.000 | 0.000 |
| `semantic-score` | 0.939 | 0.790 | -0.149 |
| `semantic-rerank` | 0.939 | 0.790 | -0.149 |
| `agentic-retrieval` | 0.934 | 0.782 | -0.152 |
| `quota-merge` | 0.720 | 0.724 | 0.004 |
| `global-rrf` | 0.720 | 0.724 | 0.004 |
| `interleave` | 0.706 | 0.710 | 0.004 |
| `idf-correct-probe` | 0.728 | 0.648 | -0.081 |
| `idf-correct-sidecar` | 0.726 | 0.647 | -0.080 |
| `naive-score` | 0.685 | 0.632 | -0.053 |
| `minmax-norm` | 0.578 | 0.629 | 0.051 |
| `zscore-norm` | 0.584 | 0.624 | 0.040 |
| `single-index-rescored` | 0.721 | 0.619 | -0.103 |
| `external-rerank` | 0.740 | 0.615 | -0.125 |
| `global-bm25` | 0.729 | 0.592 | -0.137 |
| `local-bm25` | 0.668 | 0.546 | -0.123 |

### Significance against the single index

Paired over the same queries. `Δ judged` is mean judged nDCG@10 minus the single index;
the interval is a 95% paired bootstrap over 10,000 resamples. `p (Holm)` is the paired
t-test corrected across every strategy in this mode; `p (W)` is the uncorrected Wilcoxon
signed-rank, shown because it is the conservative check. W/L/T counts queries where the
strategy beat, lost to, or tied the single index.

| Strategy | Δ judged | 95% interval | d | p (Holm) | p (W) | W/L/T |
| --- | ---: | :---: | ---: | ---: | ---: | :---: |
| `external-rerank` | +0.046 | [+0.020, +0.074] | 0.32 | 0.0065 | 0.0063 | 60/35/5 |
| `semantic-score` | +0.001 | [-0.015, +0.016] | 0.01 | 1.0000 | 0.8140 | 37/31/32 |
| `semantic-rerank` | +0.001 | [-0.015, +0.016] | 0.01 | 1.0000 | 0.8140 | 37/31/32 |
| `agentic-retrieval` | -0.004 | [-0.021, +0.013] | -0.05 | 1.0000 | 0.6538 | 37/32/31 |
| `global-bm25` | -0.062 | [-0.089, -0.035] | -0.45 | &lt;0.0001 | &lt;0.0001 | 34/63/3 |
| `idf-correct-probe` | -0.088 | [-0.117, -0.060] | -0.60 | &lt;0.0001 | &lt;0.0001 | 26/72/2 |
| `idf-correct-sidecar` | -0.090 | [-0.118, -0.061] | -0.61 | &lt;0.0001 | &lt;0.0001 | 24/73/3 |
| `single-index-rescored` | -0.094 | [-0.122, -0.067] | -0.67 | &lt;0.0001 | &lt;0.0001 | 24/74/2 |
| `quota-merge` | -0.103 | [-0.133, -0.074] | -0.68 | &lt;0.0001 | &lt;0.0001 | 25/73/2 |
| `global-rrf` | -0.103 | [-0.133, -0.074] | -0.68 | &lt;0.0001 | &lt;0.0001 | 25/73/2 |
| `local-bm25` | -0.107 | [-0.135, -0.079] | -0.74 | &lt;0.0001 | &lt;0.0001 | 20/77/3 |
| `interleave` | -0.113 | [-0.145, -0.082] | -0.71 | &lt;0.0001 | &lt;0.0001 | 23/75/2 |
| `naive-score` | -0.119 | [-0.150, -0.088] | -0.75 | &lt;0.0001 | &lt;0.0001 | 21/76/3 |
| `minmax-norm` | -0.172 | [-0.212, -0.133] | -0.87 | &lt;0.0001 | &lt;0.0001 | 20/78/2 |
| `zscore-norm` | -0.184 | [-0.222, -0.147] | -0.96 | &lt;0.0001 | &lt;0.0001 | 19/79/2 |

An interval that spans zero means the data are consistent with no difference, whatever
the point estimate suggests. Treat those rows as parity, not as small effects.

