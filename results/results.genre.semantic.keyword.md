# Cross-index fusion results

Measured against a semantic run (100 queries, top-10, Equalized candidate budget: oracle 1x50 vs stripes 2x25) on 2026-09-07 02:01:31Z.

Every score compares a fused two-index result against the same query answered by a single
index holding the whole corpus. `1.000` means the split was invisible; lower means striping
cost relevance that the strategy did not recover. These are fidelity numbers, not absolute
relevance judgements.

## Keyword

| Strategy | nDCG@10 | Recall@10 | RBO | Kendall τ | Queries | Compute units | Model tokens | p50 ms | p95 ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| `single-index` | 1.000 | 1.000 | 1.000 | 1.000 | 1.0 | 0.0004 | — | 159 | 338 |
| `semantic-score` | 0.849 | 0.770 | 0.817 | 0.988 | 2.0 | 0.0008 | — | 158 | 280 |
| `semantic-rerank` | 0.849 | 0.770 | 0.817 | 0.988 | 4.0 | 0.0014 | — | 274 | 405 |
| `quota-merge` | 0.722 | 0.583 | 0.553 | 0.635 | 2.0 | 0.0008 | — | 158 | 280 |
| `global-rrf` | 0.722 | 0.583 | 0.553 | 0.635 | 2.0 | 0.0008 | — | 158 | 280 |
| `interleave` | 0.708 | 0.583 | 0.537 | 0.627 | 2.0 | 0.0008 | — | 158 | 280 |
| `idf-correct-probe` | 0.680 | 0.486 | 0.309 | 0.116 | 8.5 | 0.0009 | — | 458 | 720 |
| `idf-correct-sidecar` | 0.679 | 0.485 | 0.307 | 0.120 | 2.0 | 0.0008 | — | 158 | 280 |
| `external-rerank` | 0.674 | 0.494 | 0.338 | 0.141 | 2.0 | 0.0008 | 30,906 | 19392 | 27142 |
| `single-index-rescored` | 0.660 | 0.433 | 0.303 | 0.166 | 1.0 | 0.0004 | — | 159 | 338 |
| `agentic-rerank` | 0.658 | 0.503 | 0.414 | 0.306 | 2.0 | 0.0000 | 18,500 | 1973 | 2376 |
| `naive-score` | 0.653 | 0.449 | 0.291 | 0.115 | 2.0 | 0.0008 | — | 158 | 280 |
| `global-bm25` | 0.647 | 0.463 | 0.321 | 0.192 | 2.0 | 0.0008 | — | 160 | 281 |
| `minmax-norm` | 0.609 | 0.397 | 0.249 | 0.087 | 2.0 | 0.0008 | — | 158 | 280 |
| `zscore-norm` | 0.608 | 0.387 | 0.245 | 0.094 | 2.0 | 0.0008 | — | 158 | 280 |
| `local-bm25` | 0.595 | 0.421 | 0.295 | 0.141 | 2.0 | 0.0008 | — | 160 | 281 |
| `agentic-planned` | 0.540 | 0.405 | 0.331 | 0.273 | 5.0 | 0.0000 | 42,857 | 3838 | 4698 |
| `agentic-cheap` | 0.539 | 0.283 | 0.179 | 0.059 | 2.0 | 0.0000 | 0 | 1845 | 1927 |

### By query span

Stripe-local queries find their answers in one index; cross-stripe queries need both.
Fusion quality is decided by the second column.

| Strategy | nDCG stripe-local | nDCG cross-stripe | Δ |
| --- | ---: | ---: | ---: |
| `single-index` | 1.000 | 1.000 | 0.000 |
| `semantic-score` | 0.939 | 0.790 | -0.149 |
| `semantic-rerank` | 0.939 | 0.790 | -0.149 |
| `quota-merge` | 0.720 | 0.724 | 0.004 |
| `global-rrf` | 0.720 | 0.724 | 0.004 |
| `interleave` | 0.706 | 0.710 | 0.004 |
| `idf-correct-probe` | 0.728 | 0.648 | -0.081 |
| `idf-correct-sidecar` | 0.726 | 0.647 | -0.080 |
| `external-rerank` | 0.732 | 0.636 | -0.096 |
| `naive-score` | 0.685 | 0.632 | -0.053 |
| `minmax-norm` | 0.578 | 0.629 | 0.051 |
| `zscore-norm` | 0.584 | 0.624 | 0.040 |
| `single-index-rescored` | 0.721 | 0.619 | -0.103 |
| `global-bm25` | 0.729 | 0.592 | -0.137 |
| `agentic-rerank` | 0.760 | 0.589 | -0.171 |
| `local-bm25` | 0.668 | 0.546 | -0.123 |
| `agentic-cheap` | 0.549 | 0.532 | -0.017 |
| `agentic-planned` | 0.668 | 0.454 | -0.214 |

### Quality, cost and latency

Cost assumes $0.27 per compute-unit hour and $1.00 per 1K semantic ranker queries, both read from the Azure retail
prices API, plus $0.40 per million model tokens, which is an assumption rather than a
measurement — substitute your own rate and the token-bearing rows move with it.

| Strategy | Quality | vs single | $/1K queries | vs single | p50 ms | vs single | Tokens/query |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| `agentic-rerank` | 0.782 | 1.08x | $9.40 | 8.51x | 1973 | 12.40x | 18,500 |
| `external-rerank` | 0.769 | 1.07x | $14.57 | 13.19x | 19392 | 122x | 30,906 |
| `semantic-score` | 0.722 | 1.00x | $2.21 | 2.00x | 158 | 0.99x | — |
| `semantic-rerank` | 0.722 | 1.00x | $4.39 | 3.97x | 274 | 1.72x | — |
| `single-index` | 0.721 | 1.00x | $1.10 | 1.00x | 159 | 1.00x | — |
| `agentic-planned` | 0.711 | 0.99x | $22.14 | 20.04x | 3838 | 24.12x | 42,857 |
| `global-bm25` | 0.660 | 0.92x | $2.21 | 2.00x | 160 | 1.00x | — |
| `idf-correct-probe` | 0.633 | 0.88x | $8.24 | 7.46x | 458 | 2.88x | — |
| `local-bm25` | 0.632 | 0.88x | $2.21 | 2.00x | 160 | 1.00x | — |
| `idf-correct-sidecar` | 0.632 | 0.88x | $2.21 | 2.00x | 158 | 0.99x | — |
| `single-index-rescored` | 0.628 | 0.87x | $1.10 | 1.00x | 159 | 1.00x | — |
| `quota-merge` | 0.619 | 0.86x | $2.21 | 2.00x | 158 | 0.99x | — |
| `global-rrf` | 0.619 | 0.86x | $2.21 | 2.00x | 158 | 0.99x | — |
| `interleave` | 0.608 | 0.84x | $2.21 | 2.00x | 158 | 0.99x | — |
| `naive-score` | 0.602 | 0.84x | $2.21 | 2.00x | 158 | 0.99x | — |
| `minmax-norm` | 0.550 | 0.76x | $2.21 | 2.00x | 158 | 0.99x | — |
| `zscore-norm` | 0.538 | 0.75x | $2.21 | 2.00x | 158 | 0.99x | — |
| `agentic-cheap` | 0.457 | 0.63x | $2.00 | 1.81x | 1845 | 11.59x | 0 |

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
| `agentic-rerank` | +0.061 | [+0.037, +0.085] | 0.50 | &lt;0.0001 | &lt;0.0001 | 64/32/4 |
| `external-rerank` | +0.047 | [+0.023, +0.073] | 0.37 | 0.0015 | 0.0023 | 60/37/3 |
| `semantic-score` | +0.001 | [-0.015, +0.016] | 0.01 | 1.0000 | 0.8093 | 37/31/32 |
| `semantic-rerank` | +0.001 | [-0.015, +0.016] | 0.01 | 1.0000 | 0.8093 | 37/31/32 |
| `agentic-planned` | -0.010 | [-0.046, +0.025] | -0.06 | 1.0000 | 0.9435 | 49/49/2 |
| `global-bm25` | -0.061 | [-0.088, -0.035] | -0.45 | &lt;0.0001 | &lt;0.0001 | 34/63/3 |
| `idf-correct-probe` | -0.088 | [-0.116, -0.059] | -0.60 | &lt;0.0001 | &lt;0.0001 | 26/72/2 |
| `local-bm25` | -0.089 | [-0.117, -0.062] | -0.62 | &lt;0.0001 | &lt;0.0001 | 22/75/3 |
| `idf-correct-sidecar` | -0.089 | [-0.118, -0.061] | -0.61 | &lt;0.0001 | &lt;0.0001 | 24/73/3 |
| `single-index-rescored` | -0.093 | [-0.121, -0.066] | -0.67 | &lt;0.0001 | &lt;0.0001 | 24/74/2 |
| `quota-merge` | -0.103 | [-0.132, -0.074] | -0.69 | &lt;0.0001 | &lt;0.0001 | 25/73/2 |
| `global-rrf` | -0.103 | [-0.132, -0.074] | -0.69 | &lt;0.0001 | &lt;0.0001 | 25/73/2 |
| `interleave` | -0.113 | [-0.145, -0.082] | -0.71 | &lt;0.0001 | &lt;0.0001 | 23/75/2 |
| `naive-score` | -0.119 | [-0.150, -0.088] | -0.75 | &lt;0.0001 | &lt;0.0001 | 21/76/3 |
| `minmax-norm` | -0.172 | [-0.211, -0.132] | -0.87 | &lt;0.0001 | &lt;0.0001 | 20/78/2 |
| `zscore-norm` | -0.184 | [-0.222, -0.147] | -0.96 | &lt;0.0001 | &lt;0.0001 | 19/79/2 |
| `agentic-cheap` | -0.265 | [-0.304, -0.225] | -1.31 | &lt;0.0001 | &lt;0.0001 | 6/93/1 |

An interval that spans zero means the data are consistent with no difference, whatever
the point estimate suggests. Treat those rows as parity, not as small effects.

