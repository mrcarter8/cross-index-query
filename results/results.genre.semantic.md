# Cross-index fusion results

Measured against a lexical run (100 queries, top-10, Equalized candidate budget: oracle 1x50 vs stripes 2x25) on 2026-09-03 09:53:57Z.

Every score compares a fused two-index result against the same query answered by a single
index holding the whole corpus. `1.000` means the split was invisible; lower means striping
cost relevance that the strategy did not recover. These are fidelity numbers, not absolute
relevance judgements.

## Keyword

| Strategy | nDCG@10 | Recall@10 | RBO | Kendall τ | Queries | Compute units | p50 ms | p95 ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| `single-index` | 1.000 | 1.000 | 1.000 | 1.000 | 1.0 | 0.0004 | 159 | 185 |
| `semantic-score` | 0.849 | 0.770 | 0.693 | 0.988 | 2.0 | 0.0007 | 157 | 272 |
| `semantic-rerank` | 0.849 | 0.770 | 0.693 | 0.988 | 4.0 | 0.0013 | 274 | 414 |
| `agentic-retrieval` | 0.843 | 0.757 | 0.686 | 0.988 | 0.0 | 0.0000 | 327 | 527 |
| `quota-merge` | 0.722 | 0.583 | 0.489 | 0.635 | 2.0 | 0.0007 | 157 | 272 |
| `global-rrf` | 0.722 | 0.583 | 0.489 | 0.635 | 2.0 | 0.0007 | 157 | 284 |
| `interleave` | 0.708 | 0.583 | 0.478 | 0.627 | 2.0 | 0.0007 | 157 | 272 |
| `idf-correct-probe` | 0.680 | 0.486 | 0.342 | 0.116 | 8.5 | 0.0008 | 457 | 685 |
| `idf-correct-sidecar` | 0.679 | 0.485 | 0.341 | 0.120 | 2.0 | 0.0007 | 157 | 272 |
| `external-rerank` | 0.670 | 0.489 | 0.351 | 0.121 | 2.0 | 0.0007 | 21792 | 32512 |
| `naive-score` | 0.653 | 0.449 | 0.320 | 0.115 | 2.0 | 0.0007 | 157 | 272 |
| `global-bm25` | 0.647 | 0.463 | 0.344 | 0.192 | 2.0 | 0.0007 | 159 | 273 |
| `minmax-norm` | 0.609 | 0.397 | 0.281 | 0.087 | 2.0 | 0.0007 | 157 | 272 |
| `zscore-norm` | 0.608 | 0.387 | 0.277 | 0.094 | 2.0 | 0.0007 | 157 | 272 |

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
| `external-rerank` | 0.744 | 0.621 | -0.124 |
| `global-bm25` | 0.729 | 0.592 | -0.137 |

