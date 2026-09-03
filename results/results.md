# Cross-index fusion results

Measured against a lexical run (100 queries, top-10, Equalized candidate budget: oracle 1x50 vs stripes 2x25) on 2026-09-02 06:52:53Z.

Every score compares a fused two-index result against the same query answered by a single
index holding the whole corpus. `1.000` means the split was invisible; lower means striping
cost relevance that the strategy did not recover. These are fidelity numbers, not absolute
relevance judgements.

## Keyword

| Strategy | nDCG@10 | Recall@10 | RBO | Kendall τ | Queries | Compute units | p50 ms | p95 ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| `single-index` | 1.000 | 1.000 | 1.000 | 1.000 | 1.0 | 0.0004 | 66 | 90 |
| `idf-correct-sidecar` | 0.965 | 0.860 | 0.756 | 0.762 | 2.0 | 0.0006 | 56 | 59 |
| `idf-correct-probe` | 0.963 | 0.853 | 0.751 | 0.771 | 8.5 | 0.0007 | 312 | 564 |
| `naive-score` | 0.938 | 0.814 | 0.703 | 0.698 | 2.0 | 0.0006 | 56 | 59 |
| `minmax-norm` | 0.886 | 0.712 | 0.592 | 0.557 | 2.0 | 0.0006 | 56 | 59 |
| `quota-merge` | 0.885 | 0.705 | 0.579 | 0.479 | 2.0 | 0.0006 | 56 | 59 |
| `global-rrf` | 0.885 | 0.705 | 0.579 | 0.479 | 2.0 | 0.0006 | 56 | 59 |
| `zscore-norm` | 0.880 | 0.718 | 0.589 | 0.519 | 2.0 | 0.0006 | 56 | 59 |
| `interleave` | 0.870 | 0.705 | 0.564 | 0.454 | 2.0 | 0.0006 | 56 | 59 |

### By query span

Stripe-local queries find their answers in one index; cross-stripe queries need both.
Fusion quality is decided by the second column.

| Strategy | nDCG stripe-local | nDCG cross-stripe | Δ |
| --- | ---: | ---: | ---: |
| `single-index` | 1.000 | 1.000 | 0.000 |
| `idf-correct-sidecar` | 0.975 | 0.958 | -0.017 |
| `idf-correct-probe` | 0.974 | 0.956 | -0.018 |
| `naive-score` | 0.949 | 0.931 | -0.017 |
| `quota-merge` | 0.848 | 0.909 | 0.061 |
| `global-rrf` | 0.848 | 0.909 | 0.061 |
| `zscore-norm` | 0.837 | 0.908 | 0.071 |
| `minmax-norm` | 0.853 | 0.908 | 0.055 |
| `interleave` | 0.838 | 0.891 | 0.053 |

## Hybrid

| Strategy | nDCG@10 | Recall@10 | RBO | Kendall τ | Queries | Compute units | p50 ms | p95 ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| `single-index` | 1.000 | 1.000 | 1.000 | 1.000 | 1.0 | 0.0048 | 227 | 242 |
| `hybrid-legs` | 0.955 | 0.787 | 0.731 | 0.731 | 2.0 | 0.0075 | 202 | 225 |
| `naive-score` | 0.867 | 0.745 | 0.616 | 0.551 | 2.0 | 0.0075 | 202 | 225 |
| `minmax-norm` | 0.840 | 0.727 | 0.573 | 0.530 | 2.0 | 0.0075 | 202 | 225 |
| `quota-merge` | 0.815 | 0.653 | 0.542 | 0.504 | 2.0 | 0.0075 | 202 | 225 |
| `global-rrf` | 0.815 | 0.653 | 0.542 | 0.504 | 2.0 | 0.0075 | 202 | 225 |
| `vector-similarity` | 0.802 | 0.552 | 0.461 | 0.349 | 2.0 | 0.0075 | 202 | 225 |
| `interleave` | 0.794 | 0.653 | 0.517 | 0.474 | 2.0 | 0.0075 | 202 | 225 |
| `idf-correct-sidecar` | 0.767 | 0.504 | 0.425 | 0.427 | 2.0 | 0.0075 | 202 | 225 |
| `idf-correct-probe` | 0.766 | 0.504 | 0.423 | 0.427 | 2.0 | 0.0075 | 202 | 225 |
| `zscore-norm` | 0.758 | 0.678 | 0.479 | 0.421 | 2.0 | 0.0075 | 202 | 225 |

### By query span

Stripe-local queries find their answers in one index; cross-stripe queries need both.
Fusion quality is decided by the second column.

| Strategy | nDCG stripe-local | nDCG cross-stripe | Δ |
| --- | ---: | ---: | ---: |
| `single-index` | 1.000 | 1.000 | 0.000 |
| `hybrid-legs` | 0.967 | 0.947 | -0.020 |
| `naive-score` | 0.826 | 0.895 | 0.069 |
| `minmax-norm` | 0.783 | 0.878 | 0.095 |
| `quota-merge` | 0.740 | 0.865 | 0.125 |
| `global-rrf` | 0.740 | 0.865 | 0.125 |
| `interleave` | 0.730 | 0.837 | 0.106 |
| `zscore-norm` | 0.649 | 0.830 | 0.181 |
| `vector-similarity` | 0.817 | 0.792 | -0.025 |
| `idf-correct-sidecar` | 0.809 | 0.739 | -0.070 |
| `idf-correct-probe` | 0.809 | 0.737 | -0.072 |

