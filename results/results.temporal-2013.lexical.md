# Cross-index fusion results

Measured against a lexical run (100 queries, top-10, Equalized candidate budget: oracle 1x50 vs stripes 2x25) on 2026-09-03 08:48:36Z.

Every score compares a fused two-index result against the same query answered by a single
index holding the whole corpus. `1.000` means the split was invisible; lower means striping
cost relevance that the strategy did not recover. These are fidelity numbers, not absolute
relevance judgements.

## Keyword

| Strategy | nDCG@10 | Recall@10 | RBO | Kendall τ | Queries | Compute units | p50 ms | p95 ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| `single-index` | 1.000 | 1.000 | 1.000 | 1.000 | 1.0 | 0.0004 | 67 | 94 |
| `idf-correct-sidecar` | 0.982 | 0.934 | 0.800 | 0.872 | 2.0 | 0.0006 | 56 | 59 |
| `idf-correct-probe` | 0.979 | 0.937 | 0.797 | 0.866 | 8.5 | 0.0007 | 319 | 573 |
| `naive-score` | 0.978 | 0.923 | 0.797 | 0.871 | 2.0 | 0.0006 | 56 | 59 |
| `quota-merge` | 0.926 | 0.881 | 0.695 | 0.736 | 2.0 | 0.0006 | 56 | 59 |
| `interleave` | 0.880 | 0.627 | 0.591 | 0.533 | 2.0 | 0.0006 | 56 | 59 |
| `zscore-norm` | 0.854 | 0.649 | 0.555 | 0.469 | 2.0 | 0.0006 | 56 | 59 |
| `minmax-norm` | 0.850 | 0.665 | 0.557 | 0.482 | 2.0 | 0.0006 | 56 | 59 |
| `global-rrf` | 0.845 | 0.627 | 0.544 | 0.454 | 2.0 | 0.0006 | 56 | 59 |

### By query span

Stripe-local queries find their answers in one index; cross-stripe queries need both.
Fusion quality is decided by the second column.

| Strategy | nDCG stripe-local | nDCG cross-stripe | Δ |
| --- | ---: | ---: | ---: |
| `single-index` | 1.000 | 1.000 | 0.000 |
| `naive-score` | 0.986 | 0.973 | -0.013 |
| `idf-correct-sidecar` | 0.995 | 0.973 | -0.023 |
| `idf-correct-probe` | 0.995 | 0.968 | -0.027 |
| `quota-merge` | 0.921 | 0.929 | 0.008 |
| `interleave` | 0.863 | 0.891 | 0.029 |
| `zscore-norm` | 0.829 | 0.871 | 0.042 |
| `minmax-norm` | 0.826 | 0.866 | 0.040 |
| `global-rrf` | 0.821 | 0.862 | 0.041 |

