# Cross-index fusion results

Measured against a lexical run (100 queries, top-10, Equalized candidate budget: oracle 1x50 vs stripes 2x25) on 2026-09-03 08:47:25Z.

Every score compares a fused two-index result against the same query answered by a single
index holding the whole corpus. `1.000` means the split was invisible; lower means striping
cost relevance that the strategy did not recover. These are fidelity numbers, not absolute
relevance judgements.

## Keyword

| Strategy | nDCG@10 | Recall@10 | RBO | Kendall τ | Queries | Compute units | p50 ms | p95 ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| `single-index` | 1.000 | 1.000 | 1.000 | 1.000 | 1.0 | 0.0004 | 65 | 93 |
| `naive-score` | 0.995 | 0.973 | 0.845 | 0.966 | 2.0 | 0.0004 | 58 | 75 |
| `idf-correct-probe` | 0.995 | 0.973 | 0.845 | 0.966 | 8.5 | 0.0005 | 317 | 582 |
| `idf-correct-sidecar` | 0.995 | 0.973 | 0.845 | 0.966 | 2.0 | 0.0004 | 58 | 75 |
| `zscore-norm` | 0.880 | 0.801 | 0.702 | 0.957 | 2.0 | 0.0004 | 58 | 75 |
| `quota-merge` | 0.869 | 0.894 | 0.683 | 0.952 | 2.0 | 0.0004 | 58 | 75 |
| `minmax-norm` | 0.807 | 0.776 | 0.610 | 0.952 | 2.0 | 0.0004 | 58 | 75 |
| `interleave` | 0.777 | 0.634 | 0.570 | 0.944 | 2.0 | 0.0004 | 58 | 75 |
| `global-rrf` | 0.736 | 0.634 | 0.535 | 0.943 | 2.0 | 0.0004 | 58 | 75 |

### By query span

Stripe-local queries find their answers in one index; cross-stripe queries need both.
Fusion quality is decided by the second column.

| Strategy | nDCG stripe-local | nDCG cross-stripe | Δ |
| --- | ---: | ---: | ---: |
| `single-index` | 1.000 | 1.000 | 0.000 |
| `naive-score` | 0.998 | 0.993 | -0.005 |
| `idf-correct-probe` | 0.998 | 0.993 | -0.005 |
| `idf-correct-sidecar` | 0.998 | 0.993 | -0.005 |
| `zscore-norm` | 0.881 | 0.879 | -0.002 |
| `quota-merge` | 0.876 | 0.865 | -0.012 |
| `minmax-norm` | 0.817 | 0.800 | -0.017 |
| `interleave` | 0.783 | 0.773 | -0.010 |
| `global-rrf` | 0.743 | 0.731 | -0.011 |

