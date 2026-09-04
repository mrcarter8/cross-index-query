# Cross-index fusion results

Measured against a lexical run (100 queries, top-10, Equalized candidate budget: oracle 1x50 vs stripes 2x25) on 2026-09-04 11:34:34Z.

Every score compares a fused two-index result against the same query answered by a single
index holding the whole corpus. `1.000` means the split was invisible; lower means striping
cost relevance that the strategy did not recover. These are fidelity numbers, not absolute
relevance judgements.

## Vector

| Strategy | nDCG@10 | Recall@10 | RBO | Kendall τ | Queries | Compute units | p50 ms | p95 ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| `single-index` | 1.000 | 1.000 | 1.000 | 1.000 | 1.0 | 0.0007 | 79 | 108 |
| `naive-score` | 1.000 | 1.000 | 1.000 | 1.000 | 2.0 | 0.0009 | 64 | 99 |
| `vector-similarity` | 1.000 | 1.000 | 1.000 | 1.000 | 2.0 | 0.0009 | 64 | 99 |
| `minmax-norm` | 0.840 | 0.677 | 0.631 | 0.619 | 2.0 | 0.0009 | 64 | 99 |
| `quota-merge` | 0.838 | 0.643 | 0.616 | 0.578 | 2.0 | 0.0009 | 64 | 99 |
| `global-rrf` | 0.838 | 0.643 | 0.616 | 0.578 | 2.0 | 0.0009 | 64 | 99 |
| `zscore-norm` | 0.829 | 0.664 | 0.604 | 0.580 | 2.0 | 0.0009 | 64 | 99 |
| `interleave` | 0.820 | 0.643 | 0.584 | 0.541 | 2.0 | 0.0009 | 64 | 99 |

### By query span

Stripe-local queries find their answers in one index; cross-stripe queries need both.
Fusion quality is decided by the second column.

| Strategy | nDCG stripe-local | nDCG cross-stripe | Δ |
| --- | ---: | ---: | ---: |
| `single-index` | 1.000 | 1.000 | 0.000 |
| `naive-score` | 1.000 | 1.000 | 0.000 |
| `vector-similarity` | 1.000 | 1.000 | 0.000 |
| `minmax-norm` | 0.759 | 0.894 | 0.135 |
| `quota-merge` | 0.757 | 0.891 | 0.134 |
| `global-rrf` | 0.757 | 0.891 | 0.134 |
| `zscore-norm` | 0.750 | 0.881 | 0.131 |
| `interleave` | 0.747 | 0.869 | 0.121 |

### Significance against the single index

Paired over the same queries. `Δ judged` is mean judged nDCG@10 minus the single index;
the interval is a 95% paired bootstrap over 10,000 resamples. `p (Holm)` is the paired
t-test corrected across every strategy in this mode; `p (W)` is the uncorrected Wilcoxon
signed-rank, shown because it is the conservative check. W/L/T counts queries where the
strategy beat, lost to, or tied the single index.

| Strategy | Δ judged | 95% interval | d | p (Holm) | p (W) | W/L/T |
| --- | ---: | :---: | ---: | ---: | ---: | :---: |
| `naive-score` | 0.000 | [0.000, 0.000] | 0.00 | 1.0000 | 1.0000 | 0/0/100 |
| `vector-similarity` | 0.000 | [0.000, 0.000] | 0.00 | 1.0000 | 1.0000 | 0/0/100 |
| `minmax-norm` | -0.088 | [-0.120, -0.059] | -0.57 | &lt;0.0001 | &lt;0.0001 | 23/69/8 |
| `zscore-norm` | -0.090 | [-0.120, -0.062] | -0.62 | &lt;0.0001 | &lt;0.0001 | 25/68/7 |
| `quota-merge` | -0.092 | [-0.121, -0.064] | -0.63 | &lt;0.0001 | &lt;0.0001 | 25/70/5 |
| `global-rrf` | -0.092 | [-0.121, -0.064] | -0.63 | &lt;0.0001 | &lt;0.0001 | 25/70/5 |
| `interleave` | -0.098 | [-0.130, -0.069] | -0.63 | &lt;0.0001 | &lt;0.0001 | 22/73/5 |

An interval that spans zero means the data are consistent with no difference, whatever
the point estimate suggests. Treat those rows as parity, not as small effects.

