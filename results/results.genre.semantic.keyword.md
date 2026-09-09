# Cross-index fusion results

Measured against a semantic run (100 queries, top-10, Equalized candidate budget: oracle 1x50 vs stripes 2x25) on 2026-09-09 09:10:13Z.

Every score compares a fused two-index result against the same query answered by a single
index holding the whole corpus. `1.000` means the split was invisible; lower means striping
cost relevance that the strategy did not recover. These are fidelity numbers, not absolute
relevance judgements.

## Keyword

| Strategy | nDCG@10 | Recall@10 | RBO | Kendall τ | Queries | Compute units | Agentic tokens | Model tokens | p50 ms | p95 ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| `single-index` | 1.000 | 1.000 | 1.000 | 1.000 | 1.0 | 0.0005 | — | — | 167 | 359 |
| `semantic-score` | 0.849 | 0.770 | 0.817 | 0.988 | 2.0 | 0.0008 | — | — | 149 | 283 |
| `semantic-rerank` | 0.849 | 0.770 | 0.817 | 0.988 | 4.0 | 0.0015 | — | — | 266 | 405 |
| `quota-merge` | 0.722 | 0.583 | 0.553 | 0.635 | 2.0 | 0.0008 | — | — | 149 | 283 |
| `global-rrf` | 0.722 | 0.583 | 0.553 | 0.635 | 2.0 | 0.0008 | — | — | 149 | 283 |
| `interleave` | 0.708 | 0.583 | 0.537 | 0.627 | 2.0 | 0.0008 | — | — | 149 | 283 |
| `idf-correct-probe` | 0.680 | 0.486 | 0.309 | 0.116 | 8.5 | 0.0021 | — | — | 492 | 769 |
| `idf-correct-sidecar` | 0.679 | 0.485 | 0.307 | 0.120 | 2.0 | 0.0008 | — | — | 149 | 283 |
| `external-rerank` | 0.666 | 0.491 | 0.329 | 0.136 | 2.0 | 0.0008 | — | 30,958 | 24985 | 36477 |
| `single-index-rescored` | 0.660 | 0.433 | 0.303 | 0.166 | 1.0 | 0.0005 | — | — | 167 | 359 |
| `agentic-rerank` | 0.657 | 0.502 | 0.414 | 0.324 | 2.0 | 0.0000 | 18,500 | — | 2097 | 2394 |
| `naive-score` | 0.653 | 0.449 | 0.291 | 0.115 | 2.0 | 0.0008 | — | — | 149 | 283 |
| `global-bm25` | 0.647 | 0.463 | 0.321 | 0.192 | 2.0 | 0.0008 | — | — | 151 | 284 |
| `selective-adaptive` | 0.645 | 0.414 | 0.299 | 0.166 | 1.9 | 0.0006 | — | — | 62 | 79 |
| `minmax-norm` | 0.609 | 0.397 | 0.249 | 0.087 | 2.0 | 0.0008 | — | — | 149 | 283 |
| `zscore-norm` | 0.608 | 0.387 | 0.245 | 0.094 | 2.0 | 0.0008 | — | — | 149 | 283 |
| `local-bm25` | 0.595 | 0.421 | 0.295 | 0.141 | 2.0 | 0.0008 | — | — | 151 | 284 |
| `selective-one` | 0.583 | 0.331 | 0.220 | 0.106 | 1.0 | 0.0004 | — | — | 67 | 94 |
| `agentic-planned` | 0.548 | 0.417 | 0.344 | 0.245 | 4.7 | 0.0000 | 39,100 | 1,144 | 4082 | 5230 |
| `agentic-cheap` | 0.539 | 0.283 | 0.179 | 0.059 | 2.0 | 0.0000 | 0 | — | 1851 | 2116 |

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
| `naive-score` | 0.685 | 0.632 | -0.053 |
| `minmax-norm` | 0.578 | 0.629 | 0.051 |
| `zscore-norm` | 0.584 | 0.624 | 0.040 |
| `external-rerank` | 0.731 | 0.623 | -0.109 |
| `single-index-rescored` | 0.721 | 0.619 | -0.103 |
| `selective-adaptive` | 0.715 | 0.598 | -0.117 |
| `global-bm25` | 0.729 | 0.592 | -0.137 |
| `agentic-rerank` | 0.760 | 0.587 | -0.173 |
| `local-bm25` | 0.668 | 0.546 | -0.123 |
| `selective-one` | 0.652 | 0.537 | -0.114 |
| `agentic-cheap` | 0.549 | 0.532 | -0.017 |
| `agentic-planned` | 0.670 | 0.466 | -0.204 |

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
| `agentic-rerank` | 0.783 | 1.09x | $2.41 | $2.41 | — | — | 2097 |
| `external-rerank` | 0.777 | 1.08x | $14.38 | $14.59 | 1.66x | 60 % | 24985 |
| `agentic-planned` | 0.724 | 1.00x | $6.32 | $6.32 | — | — | 4082 |
| `semantic-score` | 0.722 | 1.00x | $2.00 | $2.21 | 1.66x | 60 % | 149 |
| `semantic-rerank` | 0.722 | 1.00x | $4.00 | $4.40 | 3.15x | 32 % | 266 |
| `single-index` | 0.721 | 1.00x | $1.00 | $1.13 | 1.00x | 100 % | 167 |
| `global-bm25` | 0.660 | 0.92x | $2.00 | $2.21 | 1.66x | 60 % | 151 |
| `idf-correct-probe` | 0.633 | 0.88x | $8.00 | $8.57 | 4.51x | 22 % | 492 |
| `local-bm25` | 0.632 | 0.88x | $2.00 | $2.21 | 1.66x | 60 % | 151 |
| `idf-correct-sidecar` | 0.632 | 0.88x | $2.00 | $2.21 | 1.66x | 60 % | 149 |
| `single-index-rescored` | 0.628 | 0.87x | $1.00 | $1.13 | 1.00x | 100 % | 167 |
| `selective-adaptive` | 0.625 | 0.87x | $2.00 | $2.16 | 1.27x | 79 % | 62 |
| `quota-merge` | 0.618 | 0.86x | $2.00 | $2.21 | 1.66x | 60 % | 149 |
| `global-rrf` | 0.618 | 0.86x | $2.00 | $2.21 | 1.66x | 60 % | 149 |
| `interleave` | 0.608 | 0.84x | $2.00 | $2.21 | 1.66x | 60 % | 149 |
| `naive-score` | 0.602 | 0.84x | $2.00 | $2.21 | 1.66x | 60 % | 149 |
| `minmax-norm` | 0.550 | 0.76x | $2.00 | $2.21 | 1.66x | 60 % | 149 |
| `zscore-norm` | 0.537 | 0.75x | $2.00 | $2.21 | 1.66x | 60 % | 149 |
| `selective-one` | 0.524 | 0.73x | $1.00 | $1.10 | 0.82x | 122 % | 67 |
| `agentic-cheap` | 0.456 | 0.63x | $2.00 | $2.00 | — | — | 1851 |

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
| `agentic-rerank` | +0.062 | [+0.039, +0.086] | 0.51 | &lt;0.0001 | &lt;0.0001 | 65/31/4 |
| `external-rerank` | +0.056 | [+0.029, +0.085] | 0.39 | 0.0008 | 0.0012 | 57/40/3 |
| `agentic-planned` | +0.003 | [-0.031, +0.034] | 0.02 | 1.0000 | 0.1831 | 52/45/3 |
| `semantic-score` | +0.001 | [-0.015, +0.016] | 0.01 | 1.0000 | 0.8093 | 37/31/32 |
| `semantic-rerank` | +0.001 | [-0.015, +0.016] | 0.01 | 1.0000 | 0.8093 | 37/31/32 |
| `global-bm25` | -0.061 | [-0.088, -0.035] | -0.45 | &lt;0.0001 | &lt;0.0001 | 34/63/3 |
| `idf-correct-probe` | -0.088 | [-0.116, -0.059] | -0.60 | &lt;0.0001 | &lt;0.0001 | 26/72/2 |
| `local-bm25` | -0.089 | [-0.117, -0.062] | -0.62 | &lt;0.0001 | &lt;0.0001 | 22/75/3 |
| `idf-correct-sidecar` | -0.089 | [-0.118, -0.061] | -0.61 | &lt;0.0001 | &lt;0.0001 | 24/73/3 |
| `single-index-rescored` | -0.093 | [-0.121, -0.066] | -0.67 | &lt;0.0001 | &lt;0.0001 | 24/74/2 |
| `selective-adaptive` | -0.096 | [-0.124, -0.069] | -0.67 | &lt;0.0001 | &lt;0.0001 | 26/74/0 |
| `quota-merge` | -0.103 | [-0.132, -0.074] | -0.69 | &lt;0.0001 | &lt;0.0001 | 25/73/2 |
| `global-rrf` | -0.103 | [-0.132, -0.074] | -0.69 | &lt;0.0001 | &lt;0.0001 | 25/73/2 |
| `interleave` | -0.113 | [-0.145, -0.082] | -0.71 | &lt;0.0001 | &lt;0.0001 | 23/75/2 |
| `naive-score` | -0.119 | [-0.150, -0.088] | -0.75 | &lt;0.0001 | &lt;0.0001 | 21/76/3 |
| `minmax-norm` | -0.171 | [-0.211, -0.132] | -0.87 | &lt;0.0001 | &lt;0.0001 | 20/78/2 |
| `zscore-norm` | -0.184 | [-0.222, -0.147] | -0.96 | &lt;0.0001 | &lt;0.0001 | 19/79/2 |
| `selective-one` | -0.197 | [-0.235, -0.160] | -1.03 | &lt;0.0001 | &lt;0.0001 | 12/87/1 |
| `agentic-cheap` | -0.265 | [-0.304, -0.225] | -1.31 | &lt;0.0001 | &lt;0.0001 | 6/93/1 |

An interval that spans zero means the data are consistent with no difference, whatever
the point estimate suggests. Treat those rows as parity, not as small effects.

