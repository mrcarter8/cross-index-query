# Cross-index fusion results

> **Provenance note.** This file was produced before the 2026-09-04 corrections and is retained
> because its judged-relevance numbers back section 6 of the report. Two caveats apply:
>
> - **The `RBO` column is wrong.** Rank-biased overlap was computed without a depth cutoff, so it
>   compared a 10-item candidate list against a 50-item reference and understated every strategy.
>   The other columns are unaffected. Current files carry the corrected values.
> - **No significance table, and no controls.** The `local-bm25` and `single-index-rescored` controls
>   did not exist yet. See the report's "The controls" section before reading any `global-bm25` row
>   in this file as a striping result.
>
> Regenerating this file requires rebuilding its indexes, which re-uploads the corpus. It has not
> been redone because its conclusions rest on judged nDCG, which the corrections did not touch.

Measured against a lexical run (100 queries, top-10, Equalized candidate budget: oracle 1x50 vs stripes 2x25) on 2026-09-03 08:42:32Z.

Every score compares a fused two-index result against the same query answered by a single
index holding the whole corpus. `1.000` means the split was invisible; lower means striping
cost relevance that the strategy did not recover. These are fidelity numbers, not absolute
relevance judgements.

## Keyword

| Strategy | nDCG@10 | Recall@10 | RBO | Kendall τ | Queries | Compute units | p50 ms | p95 ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| `single-index` | 1.000 | 1.000 | 1.000 | 1.000 | 1.0 | 0.0004 | 118 | 147 |
| `semantic-score` | 0.880 | 0.811 | 0.722 | 0.988 | 2.0 | 0.0007 | 128 | 146 |
| `semantic-rerank` | 0.880 | 0.811 | 0.722 | 0.988 | 4.0 | 0.0014 | 234 | 263 |
| `quota-merge` | 0.852 | 0.861 | 0.661 | 0.911 | 2.0 | 0.0007 | 128 | 146 |
| `interleave` | 0.772 | 0.568 | 0.548 | 0.751 | 2.0 | 0.0007 | 128 | 146 |
| `global-rrf` | 0.716 | 0.568 | 0.490 | 0.705 | 2.0 | 0.0007 | 128 | 146 |
| `naive-score` | 0.676 | 0.485 | 0.335 | 0.100 | 2.0 | 0.0007 | 128 | 146 |
| `idf-correct-sidecar` | 0.675 | 0.485 | 0.333 | 0.085 | 2.0 | 0.0007 | 128 | 146 |
| `idf-correct-probe` | 0.673 | 0.483 | 0.331 | 0.091 | 8.5 | 0.0020 | 396 | 657 |
| `minmax-norm` | 0.573 | 0.378 | 0.267 | 0.053 | 2.0 | 0.0007 | 128 | 146 |
| `zscore-norm` | 0.567 | 0.376 | 0.259 | 0.001 | 2.0 | 0.0007 | 128 | 146 |

### By query span

Stripe-local queries find their answers in one index; cross-stripe queries need both.
Fusion quality is decided by the second column.

| Strategy | nDCG stripe-local | nDCG cross-stripe | Δ |
| --- | ---: | ---: | ---: |
| `single-index` | 1.000 | 1.000 | 0.000 |
| `semantic-score` | 0.947 | 0.835 | -0.112 |
| `semantic-rerank` | 0.947 | 0.835 | -0.112 |
| `quota-merge` | 0.884 | 0.830 | -0.054 |
| `interleave` | 0.796 | 0.756 | -0.040 |
| `global-rrf` | 0.745 | 0.696 | -0.049 |
| `naive-score` | 0.725 | 0.642 | -0.083 |
| `idf-correct-sidecar` | 0.726 | 0.641 | -0.085 |
| `idf-correct-probe` | 0.726 | 0.638 | -0.088 |
| `minmax-norm` | 0.594 | 0.560 | -0.034 |
| `zscore-norm` | 0.586 | 0.555 | -0.031 |

