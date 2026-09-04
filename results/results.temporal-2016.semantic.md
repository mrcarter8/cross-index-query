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

Measured against a lexical run (100 queries, top-10, Equalized candidate budget: oracle 1x50 vs stripes 2x25) on 2026-09-03 08:40:40Z.

Every score compares a fused two-index result against the same query answered by a single
index holding the whole corpus. `1.000` means the split was invisible; lower means striping
cost relevance that the strategy did not recover. These are fidelity numbers, not absolute
relevance judgements.

## Keyword

| Strategy | nDCG@10 | Recall@10 | RBO | Kendall τ | Queries | Compute units | p50 ms | p95 ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| `single-index` | 1.000 | 1.000 | 1.000 | 1.000 | 1.0 | 0.0005 | 119 | 148 |
| `semantic-score` | 0.979 | 0.970 | 0.833 | 0.999 | 2.0 | 0.0006 | 118 | 144 |
| `semantic-rerank` | 0.979 | 0.970 | 0.833 | 0.999 | 3.9 | 0.0010 | 218 | 251 |
| `quota-merge` | 0.856 | 0.898 | 0.677 | 0.994 | 2.0 | 0.0006 | 118 | 144 |
| `interleave` | 0.772 | 0.637 | 0.570 | 0.988 | 2.0 | 0.0006 | 118 | 144 |
| `global-rrf` | 0.724 | 0.637 | 0.530 | 0.986 | 2.0 | 0.0006 | 118 | 144 |
| `naive-score` | 0.686 | 0.499 | 0.345 | 0.102 | 2.0 | 0.0006 | 119 | 144 |
| `idf-correct-probe` | 0.686 | 0.499 | 0.345 | 0.102 | 8.5 | 0.0018 | 386 | 647 |
| `idf-correct-sidecar` | 0.686 | 0.499 | 0.345 | 0.102 | 2.0 | 0.0006 | 119 | 144 |
| `zscore-norm` | 0.577 | 0.416 | 0.292 | 0.078 | 2.0 | 0.0006 | 119 | 144 |
| `minmax-norm` | 0.524 | 0.400 | 0.265 | 0.091 | 2.0 | 0.0006 | 119 | 144 |

### By query span

Stripe-local queries find their answers in one index; cross-stripe queries need both.
Fusion quality is decided by the second column.

| Strategy | nDCG stripe-local | nDCG cross-stripe | Δ |
| --- | ---: | ---: | ---: |
| `single-index` | 1.000 | 1.000 | 0.000 |
| `semantic-score` | 0.995 | 0.969 | -0.025 |
| `semantic-rerank` | 0.995 | 0.969 | -0.025 |
| `quota-merge` | 0.861 | 0.852 | -0.009 |
| `interleave` | 0.783 | 0.765 | -0.019 |
| `global-rrf` | 0.730 | 0.721 | -0.009 |
| `naive-score` | 0.737 | 0.652 | -0.085 |
| `idf-correct-probe` | 0.737 | 0.652 | -0.085 |
| `idf-correct-sidecar` | 0.737 | 0.652 | -0.085 |
| `zscore-norm` | 0.628 | 0.543 | -0.085 |
| `minmax-norm` | 0.574 | 0.490 | -0.083 |

