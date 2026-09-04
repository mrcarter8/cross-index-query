# Decisions

**Working document. Delete before publishing to Azure-Samples.**

Settled decisions and the reasoning behind them. If you find yourself about to change one of these,
read the reasoning first — most were arrived at after getting them wrong once.

---

## The core asymmetry that drives the whole design

The three query modes fail differently across indexes, and that is *why* the sample needs three
separate treatments rather than one merge function.

| Mode | Score | Cross-index comparable? |
| --- | --- | --- |
| **Keyword (BM25)** | IDF and `avgdl` computed **per index** | **No.** The same document scores differently depending only on which index it landed in. |
| **Vector** | cosine similarity, ~0.333–1.00 | **Yes**, provided both indexes use an identical embedding space. Corpus-independent by construction. |
| **Hybrid** | `@search.score` is an **RRF** score with fixed `k=60`, derived from *ranks* | **Worse than keyword.** Already a rank-fusion output, so it has thrown away the magnitudes you would need to re-fuse correctly. |
| **Semantic rerank** | `rerankerScore`, absolute 0–4 from a cross-encoder | **Yes.** Uses no corpus statistics, so it is inherently stripe-safe. |

The practical consequences:

- Vector-only striping is close to *free* — this is the good news case, and the sample should say so
  plainly rather than manufacturing drama.
- Keyword striping is where the real damage is, and it's recoverable with IDF correction.
- Hybrid is the worst case for naive merging, which is counterintuitive and worth leading with,
  because hybrid is what most people are actually running.

---

## IDF correction — the central technique

**Single term:** `globalScore = score × IDF_global(t) / IDF_local(t)`

**Multi-term:** the **IDF-weighted mean** of per-term ratios:

```
weightedSum += (globalIdf / localIdf) * globalIdf
weightTotal += globalIdf
factor       = weightedSum / weightTotal
```

Weighting by global IDF means informative terms dominate the correction. That is correct rather than
arbitrary: the distortion lives precisely in the terms the two stripes disagree about, and those are
the rare ones.

**Terms with `localDf == 0` are skipped.** Their local IDF would be maximal, which produces an
enormous deflation factor derived from a term that never influenced the score in the first place.
This was a real bug risk and is now pinned by a test.

**Fallback:** when the query tokenizes to nothing, or there are no stripes, it degrades to
`NaiveScoreFusion` rather than throwing.

This is the sample's thesis, and `IdfCorrectionFusionTests` encodes it as an executable claim: with
`dragon` at df 2000/5000 in stripe A versus 10/5000 in stripe B, naive merging picks B and the
corrected merge picks A. If that test ever goes red, the sample's argument is broken.

---

## RBO: depth normalization, not tail extrapolation

`RankBiasedOverlap` originally computed `(1-p)·Σ` with no depth normalization, so two **identical**
length-k lists scored `1 − p^k` — 0.41 at p=0.9, k=5; 0.65 at k=10. Identical lists must score 1.0.
Fixed by dividing by `1 − p^depth`.

The alternative fix is tail extrapolation, and it was rejected deliberately: extrapolation *assumes
agreement continues past the observed prefix*, and this sample exists specifically to measure lists
that diverge. Assuming the thing you are trying to measure would be circular.

Caught by `RankingMetricsTests`, which is the strongest argument for having written those tests first.

---

## Strategies report honestly rather than silently degrading`SemanticScoreFusion` **throws** when its precondition isn't met instead of falling back. The
harness catches that and records **no row** for that cell:

```csharp
catch (InvalidOperationException)
{
    // A strategy that declares its precondition unmet is reporting honestly, not failing.
    // Recording a zero would misrepresent it as having produced a bad result.
    return null;
}
```

An absent row means "not applicable here." A zero would mean "tried and scored nothing." Those are
different claims and the results table must not conflate them.

---

## A run is only valid when the oracle and the strategy rank by the same function

Measured 2026-09-02. This is the rule the first full evaluation established, and it cost a
plausible-looking wrong number to learn.

Ground truth is the oracle's ordering for the same query, and `useSemanticRanker` is one flag
applied to **both** the oracle and the stripes. Ten of the eleven strategies only reshuffle the
fan-out they were handed, so they rank by whatever function the oracle used. `SemanticRerankFusion`
issues its **own** reranker queries unconditionally, so with `--semantic` off it produced a
cross-encoder ordering scored against a BM25 baseline and landed at nDCG 0.484 against
`naive-score`'s 0.938 — apparently refuting the design's claim that semantic rerank is the best
cross-index option. It was being penalised for not being BM25.

`IFusionStrategy.RequiresSemanticRanker` (default `false`) now marks the two semantic strategies and
the harness skips them unless `--semantic` is set. The guard lives in the harness rather than the
strategy, so `cli query -s semantic-rerank` still demonstrates second-pass reranking — there is no
baseline in the `query` path to be inconsistent with.

**Consequence for reading results: rows are comparable within a run, never across runs.** The
semantic and non-semantic runs have different ground truths. `naive-score` scores 0.938 in the
non-semantic keyword run and 0.584 in the semantic one; it did not get worse, the truth changed
underneath it. The semantic run's finding is a real one — with the reranker on, merge on the
reranker score, because sorting by `@search.score` throws away the ranking you just paid for — but
it is a statement about that run only.

---

## The 2x50 hypothesis is not testable with this metric

The harness measures **fidelity to the oracle**: `1.000` means the fused list matched the single
index exactly, and any deviation counts against the strategy. The metric is signed the same way
whether the striped result was worse than the oracle or better.

The 2x50 hypothesis claims striping may **beat** the oracle, because the reranker caps input at 50
documents per query, so two stripes push 100 documents through the cross-encoder against the
oracle's 50. Under an oracle-as-truth metric that shows up as a *lower* score, indistinguishable
from damage. The hypothesis therefore cannot be confirmed or refuted by the current results table,
and the semantic run's numbers must not be read as evidence either way.

Testing it needs a measure of relevance that does not define the oracle as correct. The cheap option
uses data already retrieved: `rerankerScore` is an absolute 0-4 cross-encoder score that consults no
corpus statistics, so **mean `rerankerScore@10` for the striped result versus the oracle's own
top-10** is directly comparable. If the striped top-10 scores higher, striping genuinely surfaced
better documents rather than merely different ones. This is unbuilt and unasked — see `state.md`.

---

## The semantic funnel cannot be equalized from the client

Measured 2026-09-02 against the live service, replacing what the documentation asserts.

The reranker window is **exactly 50**, confirmed directly: a `top=70` semantic query returns 70
documents of which precisely 50 carry an `@search.rerankerScore`. Position 51 onward have none and
revert to BM25 order — visible as a run of tied 8.587 scores. The window is also **not** controlled
by `$top`: `top=25` and `top=50` return an identical first 25.

The consequence matters more than the number. A semantic query reranks its top-50 L1 candidates
*inside each index* before returning anything, so a striped deployment pushes N x 50 documents
through the cross-encoder while the single index pushes 50. Asking each stripe for fewer results
does not shrink its window; it only discards documents the reranker already scored.

So **striping silently multiplies your semantic funnel, and the client cannot opt out.** That is a
structural property of splitting an index, not a tuning choice, and it belongs in the README.

`EvaluationOptions.CandidateBudget` still exists and still matters, because it does control:

- what the fusion strategies see, which is why the BM25-based strategies improve under `Equalized`
  in a semantic run (`naive-score` 0.584 to 0.653) — they receive 25 documents that the reranker
  already selected rather than 50 chosen by BM25;
- how many documents `SemanticRerankFusion` sends to the cross-encoder, because that strategy names
  its candidates explicitly through `search.in` rather than relying on the service's own window.

What it cannot do is equalize the in-place semantic path, and no client-side setting can.

---

## Second-pass reranking buys nothing over the scores you already have

`semantic-score` and `semantic-rerank` returned **identical** results to three decimal places across
100 queries, in both candidate-budget conditions (nDCG 0.846, recall 0.770, RBO 0.689). The second
pass cost 0.0014 CU and 249 ms against 0.0008 CU and 136 ms.

Same cross-encoder, same query, overlapping documents, therefore the same ordering. When the fan-out
already ran with the semantic ranker, re-scoring is pure overhead. `SemanticScoreFusion`'s
documentation always claimed this — "cheaper than reranking as a second pass" — and it is now
measured rather than asserted.

`SemanticRerankFusion` keeps its place in the catalog because it is the strategy that works when the
fan-out was **not** semantic, which is the situation most people are actually in.

---

## Measured: IDF-corrected striping beats the single index on keyword

Run 2026-09-02, 100 queries, genre striping, **equalized candidate budget** (oracle 1x50, stripes
2x25), scored against 4,235 independent relevance judgments. Paired per-query two-sided t-tests.

| comparison | mean ΔnDCG | t | p | W/L/T |
| --- | ---: | ---: | ---: | --- |
| keyword `idf-correct-sidecar` vs single index | **+0.0127** | 2.71 | **0.005** | 58/33/9 |
| keyword `idf-correct-probe` vs single index | **+0.0142** | 3.00 | **0.002** | 61/31/8 |
| keyword `naive-score` vs single index | −0.0155 | −2.12 | 0.029 | 36/56/8 |
| keyword `idf-correct-sidecar` vs `naive-score` | **+0.0282** | 4.26 | **<0.0001** | 58/19/23 |
| hybrid `hybrid-legs` vs single index | +0.0088 | 1.31 | 0.18 | 47/40/13 |
| hybrid `naive-score` vs single index | −0.0581 | −5.35 | **<0.0001** | 30/66/4 |
| hybrid `hybrid-legs` vs `naive-score` | **+0.0669** | 6.28 | **<0.0001** | 73/21/6 |

Three claims are supported, and the third is the surprising one:

1. **Naive merging really is worse than not striping.** Significantly so in hybrid
   (−0.058, p<0.0001) and directionally in keyword (−0.016, p=0.029).
2. **Leg decomposition restores hybrid to parity.** It beats naive fusion decisively
   (+0.067, p<0.0001) and is statistically **indistinguishable** from the single index
   (p=0.18). Claim parity here, not superiority.
3. **IDF-corrected keyword striping significantly exceeds the single index** (+0.013, p=0.005;
   +0.014, p=0.002 for the probe variant).

Claim 3 needs its mechanism stated, because "splitting your index improves relevance" is otherwise
an absurd sentence. It is **not** the 2x50 reranker effect: this is the lexical run, with no semantic
ranking anywhere in it, and the candidate budget was equalized. What genre striping does is
**guarantee candidate slots to each thematic half of the corpus**. A single index spends its whole
top-50 on whatever globally scores highest, which for a cross-cutting query can be one genre;
striping forces 25 from each side, and IDF correction then puts the two halves on a common scale so
the diversity is usable rather than merely present.

So the effect is real, and it is a property of **thematic** striping specifically. Random striping
has nothing to diversify, and the same experiment against a random split is the obvious next
measurement — it is what would separate "striping helps" from "topical partitioning helps".

Caveats that must travel with these numbers:

- Seven comparisons were made. Under Bonferroni (α=0.05/7≈0.007) claims 2 and 3 survive; keyword
  `naive-score` vs single index (p=0.029) does not, so state it as directional.
- The margins are small in absolute terms. The defensible headline is "IDF-corrected striping is at
  least as good as a single index, and naive merging is measurably worse", not "striping is better".
- Judged coverage was 99-100%, so pooling bias is not carrying these results.

---

## Scenario B: striping to scale — measured

Run 2026-09-02. Temporal split by publication year, modelling the migration customers actually
perform: freeze the full index, send new documents to a new one. Keyword and vector, no semantic
ranker, scored against independent judgments.

Judged nDCG, keyword. Single index baseline is 0.542–0.547 throughout.

| imbalance | single | `idf-correct` | `naive-score` | `quota-merge` | `global-rrf` | `interleave` |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 525:1 | 0.547 | 0.546 | 0.546 | 0.468 | **0.381** | 0.398 |
| 45:1 | 0.546 | 0.546 | 0.545 | 0.484 | **0.353** | 0.372 |
| 9.4:1 | 0.542 | 0.541 | 0.537 | 0.521 | **0.437** | 0.447 |
| 2.8:1 | 0.542 | 0.535 | 0.535 | 0.512 | **0.487** | 0.497 |
| 1.0:1 | 0.542 | 0.540 | 0.532 | 0.516 | **0.516** | 0.520 |

### The predicted failure did not happen

The prediction was that IDF deflation in the small index would bury new data. The arithmetic is
sound — a singleton term's IDF gap is exactly `ln(N_large/N_small)`, reaching 6.26 nats at 525:1 —
but conditioning on the queries where the *new* index holds relevant documents shows no significant
harm from score-based merging:

| split | strategy | subset | n | mean Δ | t |
| --- | --- | --- | ---: | ---: | ---: |
| 9.4:1 | `naive-score` | new-data-relevant | 73 | −0.0050 | −0.83 |
| 9.4:1 | `idf-correct-sidecar` | new-data-relevant | 73 | −0.0029 | −0.60 |
| 9.4:1 | **`global-rrf`** | new-data-relevant | 73 | **−0.0833** | **−5.72** |
| 9.4:1 | **`global-rrf`** | other | 27 | **−0.1632** | **−5.77** |

Directionally negative, statistically indistinguishable from zero. The deflation is close to a
uniform scale factor across the small index's terms, and ranking is largely invariant to that.
**Record this as a refuted prediction, not a small effect.**

### What actually breaks is rank-based fusion

`global-rrf` loses 0.166 nDCG at 525:1 and degrades monotonically with imbalance. RRF scores a
document by `1/(k + rank)` and has no idea how many documents that rank was drawn from, so rank 1
of 19 documents is treated as the equal of rank 1 of 9,981. Under imbalance it promotes the small
index's best non-answer into the merged head on every query.

The signature confirms the mechanism: RRF's damage is **worse on queries where the new index holds
nothing relevant** (−0.163) than where it holds something (−0.083). That is junk promotion by
definition. Judged coverage falls the same way — 93% at 1:1 down to 58–74% under imbalance — meaning
its top-10 fills with documents no other approach surfaced.

**Vector mode isolates the cause.** Cosine consults no corpus statistics, so vector retrieval is
immune to IDF divergence by construction — yet `global-rrf` collapses there too (0.402 against
`naive-score`'s 0.693 at 525:1). The failure is rank fusion's blindness to index size, and it has
nothing to do with BM25.

### The guidance this inverts

RRF is the conventional recommendation for merging across indexes, precisely because it is immune to
incomparable score scales. Under size imbalance that immunity is the defect: discarding scores
discards the only signal that distinguishes a good match in a large index from the best of a tiny
one. **Merge unbalanced stripes on scores, not on ranks.** If ranks must be used, allocate slots
proportionally — `quota-merge` recovers most of the gap.

Two diseases, two cures, and they are independent:

| condition | symptom | cure |
| --- | --- | --- |
| vocabulary divergence (Scenario A) | incomparable IDF | global IDF correction |
| size imbalance (Scenario B) | rank fusion promotes junk | merge on scores, or allocate proportionally |

---

## The judge was checked against a second judge

Measured 2026-09-03. All 6,805 pooled pairs were re-graded by a different model
(`gpt-5-nano` against the primary `gpt-5.4-batch`), and every strategy comparison was recomputed
against the second judge's grades.

**Agreement:** exact 53.8%, within one grade 90.5%, quadratically weighted Cohen's kappa **0.735**,
correlation **0.815**. The second judge is systematically more generous — mean grade 1.954 against
1.422, a shift of **+0.532**.

The confusion matrix shows where the agreement is:

| primary \ second | 0 | 1 | 2 | 3 |
| --- | ---: | ---: | ---: | ---: |
| **0** | 1,043 | 581 | 118 | 19 |
| **1** | 66 | 699 | 892 | 510 |
| **2** | 1 | 14 | 177 | 927 |
| **3** | 0 | 1 | 11 | **1,743** |

**Agreement on the top grade is 99.3%** (1,743 of 1,755). The disagreement is concentrated in the
0/1/2 band, and it is almost entirely one-directional inflation rather than reordering. That is the
benign pattern for this study: nDCG with exponential gain is dominated by grade-3 documents
(`2^3-1 = 7` against `2^1-1 = 1`), and a uniform upward shift applied to every strategy's results
cancels in the comparison between them.

**Robustness of the conclusions: 26 of 27 comparisons unchanged.**

| conclusion | judge 1 | judge 2 | holds? |
| --- | ---: | ---: | --- |
| keyword `global-bm25` better than single | +0.096 *(t=5.88)* | +0.102 *(t=6.20)* | **yes** |
| keyword `idf-correct-sidecar` better | +0.013 *(t=2.67)* | +0.016 *(t=3.01)* | **yes** |
| keyword `global-rrf` worse | −0.073 *(t=−5.54)* | −0.061 *(t=−4.88)* | **yes** |
| hybrid `hybrid-legs` parity | +0.007 *(t=0.87)* | −0.002 *(t=−0.32)* | **yes** |
| hybrid `naive-score` worse | −0.062 *(t=−5.26)* | −0.060 *(t=−5.79)* | **yes** |
| hybrid `idf-correct` worse | −0.125 *(t=−7.51)* | −0.121 *(t=−7.89)* | **yes** |
| vector `naive-score` parity | −0.004 *(t=−1.31)* | +0.000 *(t=0.13)* | **yes** |
| vector `global-rrf` worse | −0.094 *(t=−6.36)* | −0.080 *(t=−5.23)* | **yes** |
| **keyword `naive-score` worse** | **−0.015 *(t=−2.06)*** | **−0.002 *(t=−0.36)*** | **no — parity** |

**One conclusion is judge-dependent, and it is the weakest one in the study.** Keyword naive merging
measured as significantly worse than a single index under the primary judge (p=0.034, already
failing Bonferroni at 7 comparisons) and as indistinguishable under the second.

This changes what can be claimed. The defensible statement about naive keyword merging is
**"directionally worse, small enough that a change of judge moves it to parity"** — not "measurably
worse". Every other finding, including all the large ones, is judge-robust.

The check does not eliminate the self-preference concern: both judges come from the same model
family, so a bias shared by that family would be invisible here. What it does establish is that the
conclusions are not artefacts of one particular model's idiosyncrasies, and that the study's headline
results survive a judge that grades half a point more generously on average.

---

## Cost accounting

Every strategy shares the same fan-out, so what distinguishes them on cost is the *extra* requests
they issue on their own account:

```csharp
QueryCount   = stripes.QueryCount   + scope.RequestCount;
ComputeUnits = stripes.ComputeUnits + (scope.TotalComputeUnits ?? 0d);
```

This is correct for every strategy that consumes a `FanOutResult`. It is **wrong for agentic
retrieval**, which does its own retrieval and is charged only for what it spends — see
`IFusionStrategy.PerformsOwnRetrieval`.

Cost is **measured** via the `x-ms-azs-compute-units-consumed` response header, not estimated. This
is the single most valuable property of choosing serverless for the sample, and it is confirmed
working (probe #1: `5E-05` CU for a 3-result keyword query).

---

## Hybrid leg decomposition

A single hybrid query returns only the fused RRF score — the component ranks are gone. To fuse
hybrid results correctly across stripes you need the legs separately, so `StripeRetriever` sets
`QueryDebugMode.Vector` and `ReadSubscores` pulls out BM25 and raw cosine independently.

Probe #3 confirmed both subscores are returned. Probe #3 also showed a document arriving with a
vector subscore and **no** text subscore — leg attribution works exactly as `HybridLegFusion`
assumes, and a missing text subscore correctly means "this document did not come from the text leg."

---

## Warmup is mandatory, not hygiene

Serverless compute scales to zero after roughly ten minutes idle. Without a warmup phase the first
measured queries carry cold-start latency and the entire latency column is fiction.
`WarmUpAsync` draws its discarded queries from the **real** query set so the warmup exercises the
same code path, index, and vector dimensionality the measured run will use.

---

## The query set is committed, not generated

100 queries with Shape/Span/Intent labels live in `data/queries.json`. Generating them per-run would
mean two people comparing results were comparing their query sets rather than their services.
Committing them makes the benchmark reproducible and comparable across services and dates.

---

## Genre as the split axis

Striping by genre makes the two stripes **thematically divergent**, which maximizes IDF disagreement
and therefore makes the effect measurable. Random striping would produce two statistically similar
indexes where naive merging looks nearly fine — which would be a much less honest demonstration of
what happens to real customers, whose data is virtually never randomly distributed.

The router stays pluggable (`random | genre`) precisely so the sample can *show* that contrast: the
same code, two split strategies, visibly different damage. **How you stripe is an independent
variable**, and it is one most people don't expect.

---

## Corpus scope: 10k, not 1k

Originally ~1,000 documents were floated. Raised to the full 10k (≈5k/5k) because document-frequency
effects are what the sample measures, and they need enough documents to be stable and believable.
The cost was one batch job, paid once, with the output committed so consumers pay nothing.

---

## Vectors are committed, encoded as int8 base64

**Decision:** commit `data/books.enriched.json` with `contentVector` stored as base64 int8 rather
than as JSON decimal text.

The alternative — ship blurbs only and make everyone run `dataprep embed` — was rejected. It moves a
cost that has already been paid onto every single consumer, and it means no two people evaluating
the sample are measuring quite the same corpus.

The obstacle was size, not principle: at float32 the file is 192 MB, past what belongs in a git
repository. So the question was whether the vectors could be made to fit without meaningfully
damaging the results. They can. Measured over the real corpus, using the 100 committed evaluation
queries embedded with the same model, against exact float32 search as ground truth:

| encoding | recall@10 | nDCG@10 | raw MB | gzip MB |
| --- | --- | --- | --- | --- |
| float32 (decimal text) | 100.00% | 100.00% | 191.8 | 66.7 |
| float32, 4 significant digits | 100.00% | 100.00% | 136.4 | 45.7 |
| fp16 base64 | 100.00% | 100.00% | 47.5 | 33.4 |
| **int8 base64 (chosen)** | **99.90%** | **99.89%** | **28.1** | **18.8** |
| MRL 768 + int8 | 98.90% | 98.95% | 18.3 | 11.3 |
| MRL 384 + int8 | 98.90% | 98.99% | 13.4 | 7.4 |
| MRL 256 + int8 | 98.00% | 98.03% | 11.8 | 6.1 |

int8 at the full 1536 dimensions was chosen because it is the first row that is comfortably small,
and everything below it costs an order of magnitude more accuracy to save space that isn't needed:

- **28 MB clones normally.** Under GitHub's 50 MB warning threshold *uncompressed*, so there is no
  LFS pointer, no `.gz`, and no decompression step in the read path. This is worth more than it
  sounds — a compressed artifact would have required gzip support at three separate read sites.
- **0.10% recall@10.** One document in a thousand result slots shifts position.
- **Dimensionality is unchanged**, so the index schema stays at 1536 and nothing about the
  stripe-versus-oracle comparison moves. Matryoshka truncation would have changed the schema, adding
  a variable to an experiment whose entire purpose is isolating one.
- It is the **same scheme the service applies natively** when scalar compression is enabled on a
  vector field, so the sample is not doing anything exotic to its own data.

It works this well because `text-embedding-3-*` returns L2-normalised vectors — measured norms
0.99942–1.00060, max component 0.201 — so components occupy a narrow, predictable band and one
scale factor per vector wastes almost none of the available range.

Documents are dequantised to float32 before upload, so indexes receive ordinary
`Collection(Edm.Single)` values. The rounding applies identically to both stripes *and* the oracle,
which is what keeps the comparison honest: the sample measures fusion error, and a uniform
perturbation across all three indexes cannot masquerade as one.

The encoding lives in `QuantizedVectorConverter`, registered through `CorpusFile`'s serializer
options rather than as an attribute on `BookDocument.ContentVector`. That is deliberate — the same
type is handed to the Search SDK for upload, and that path must emit numbers. A test
(`DefaultSerialisationOfADocumentStillEmitsANumericArray`) pins it, because the failure mode is
uploading base64 strings into a numeric field.

Reading also accepts a plain JSON array, so a corpus regenerated at full precision still loads.

---

## Committed vectors are a cache, not a permanent artifact

The fair objection to committing embeddings: `text-embedding-3-small` will eventually be retired.
When it is, the committed document vectors remain internally consistent with each other, but a
*query* can no longer be embedded into the same space — and a sample whose queries can't reach its
documents is worse than useless, because it fails in a way that still returns results.

This does not argue against committing vectors. It argues for being explicit about what they are.

They are treated as a **cache of an expensive, reproducible computation**, not as source data:

- `corpus-manifest.json` records `embeddingModel`, `embeddingDimensions` and `vectorEncoding` as
  provenance, so what produced the file is discoverable from the file.
- The preflight validator hard-fails on a model or dimension mismatch with a message naming both
  sides. It does not warn — mismatched embedding spaces produce confident nonsense, which is the
  worst available failure mode.
- `dataprep embed` regenerates everything in one command in about eight minutes.

So the sample costs nothing to run today, and after the model is retired it costs one command rather
than being broken. The blurbs — which are genuinely non-reproducible, since regenerating them yields
different text — remain the artifact that guarantees everyone measures the same corpus.

The general principle, worth stating in the README: **commit derived data when it is expensive and
deterministic, and record enough provenance that it can be rebuilt.** Do not commit it as though it
were source.

---

## Both infrastructure models

`azd up` for people who want a working environment in one command, and bring-your-own-service for
people who already have one. Neither alone covers the audience: the first is the demo path, the
second is the "does this help me with *my* problem" path.

---

## Packaging

Standalone build in `C:\dev\cross-index-query`, **no git**, not checked in, by explicit instruction.
Destined to become a subfolder of an Azure-Samples repo later, so Azure-Samples hygiene
(`page_type: sample` front matter, `.editorconfig`, central package management) is maintained from
the start rather than retrofitted.

## The headline gain was mostly a confound, and a control found it

**Measured 2026-09-04.** The study's largest claim was that recomputing BM25 client-side over the
merged pool (`global-bm25`) beats a single index by **+0.096 judged nDCG** (p<0.0001, winning 74 of
100 queries). Read as a statement about striping, that claim was wrong.

`global-bm25` changes two things at once relative to the single-index baseline. It substitutes
corpus-wide document frequencies for per-index ones, which is the cross-index repair being
advertised. It also replaces the service's scoring with a client-side BM25 over text the caller
already has, which has nothing to do with striping and is available to anybody.

Two controls separate them.

`local-bm25` holds the tokenizer, the constants, the field set and the arithmetic fixed and varies
only whose statistics are consulted. It scores **0.607** against `global-bm25`'s 0.634, so global
statistics are worth **+0.027** (p=0.0003, interval [+0.013, +0.042]). Real, and much smaller than
the headline.

`single-index-rescored` applies the exact same `global-bm25` instance to the single index's own
results. Because one index holding the whole corpus *is* the corpus, its statistics are the global
statistics, so this is the striped strategy with the split removed and nothing else changed. It
scores **0.629**.

The decomposition:

| step | judged nDCG@10 | Δ | p |
| --- | ---: | ---: | ---: |
| single index, service BM25 | 0.538 | — | — |
| single index, client-side rescore | 0.629 | **+0.092** | <0.0001 |
| two stripes, same client-side rescore | 0.634 | **+0.005** | 0.28 *(n.s.)* |

**95% of the effect was the rescorer. Striping contributed +0.0045, interval [-0.003, +0.013],
59 of 100 queries returning an identical top-10.**

What this changes:

- The claim "striping can beat a single index" is **withdrawn**. It was an artifact of comparing a
  rescored striped arm against an unrescored single index.
- The claim it is replaced by is stronger and simpler: **striping is free when you merge on
  recomputed scores.** Not "small loss" — statistically indistinguishable from zero, measured
  against an arm that differs only in the split.
- The section that attributed the gain to candidate diversity from striping was reasoning from the
  confound. Striping does enforce candidate diversity, but that mechanism is not what the numbers
  were showing, and the honest reading is that its effect is too small for this corpus to resolve.
- The rescorer effect is real but out of scope, and possibly not a search finding at all: it may be
  field handling (the client-side scorer treats title, authors and blurb as one bag, so length
  normalization differs from the service's per-field scoring) or judge affinity for blurb-matched
  text. It is reported, not recommended.

The general lesson, and the reason both controls are committed and run by default: a comparison
that cannot come out against you is not evidence. `global-bm25` looked like the study's best result
for as long as nothing was positioned to falsify it.

## Content filtering makes a small slice of the corpus unjudgeable

**Measured 2026-09-04.** Of 67 pairs submitted in the final judging pass, 48 were rejected by Azure
OpenAI's content filter — `ResponsibleAIPolicyViolation`, predominantly `hate` at medium severity.
The corpus is real published books, and books about war, atrocity and abuse trip a classifier tuned
for generated content.

These pairs stay `null` rather than becoming 0. A filtered pair means "not judged", a claim about
the judging pipeline; scoring it 0 would assert "not relevant", a claim about the document, and
would penalise whichever strategy surfaced it. Coverage is reported per strategy in every results
table so the reader can see the effect directly, and every arm in the final keyword run sits at 99%.

The residual risk is that filtering is not uniform across strategies. It is not measurably so here,
but a corpus with heavier concentration of such material in one stripe would need this checked
before any cross-arm comparison could be trusted.

## Vector striping is exactly free, and HNSW was hiding it

**Measured 2026-09-04.** The vector-mode results carried an inconsistency nobody had explained:
Kendall tau was exactly 1.000 against the single index, yet nDCG was 0.974 and recall@10 was 0.959.

Those two facts cannot both be about ranking. A tau of 1.000 means there is not a single rank
inversion anywhere — every pair of documents appearing in both lists is ordered identically. That is
what theory predicts, because cosine similarity between a query vector and a document vector
consults no corpus statistics at all, so it cannot change when the corpus is split. The shortfall
was therefore not misordering; it was documents that never appeared as candidates.

The cause is that HNSW is an approximate nearest-neighbour algorithm. Traversing two proximity
graphs of 5,292 and 4,708 documents does not visit the same neighbours as traversing one graph of
10,000. The missing 4% is search approximation, and it has nothing to do with score comparability.

Confirmed by re-running with exact search (`Evaluation.ExhaustiveVectorSearch`, which sets
`VectorizedQuery.Exhaustive`):

| vector search | fidelity nDCG@10 | recall@10 | Kendall tau | judged nDCG@10 |
| --- | ---: | ---: | ---: | ---: |
| HNSW (default) | 0.974 | 0.960 | 1.000 | 0.683 |
| Exhaustive | **1.000** | **1.000** | **1.000** | **0.684** — identical to single index |

Under exact search the striped arm reproduces the single index **exactly**: same documents, same
order, same judged score to three decimals.

So the claim sharpens from "vector striping is free" to something stronger and more precise:

- **Splitting a corpus has zero effect on vector ranking.** Provable from first principles and now
  measured at exactly 1.000 on every fidelity metric.
- **Any shortfall you observe in practice is ANN recall**, an artefact of the index algorithm that
  would also appear between two runs against the same index with different graph parameters. It is
  ~2.6% nDCG here and is not a cost of striping.

The flag defaults to false, because HNSW is what production runs and the headline numbers should
describe what people will actually see. It exists so the two effects can be told apart rather than
asserted apart.

Note also that `global-rrf` still loses 0.092 judged nDCG under exhaustive search. Rank fusion's
failure in vector mode is not an ANN artefact either; it is rank fusion discarding scores that were
already perfectly comparable.

## Results filenames must encode the mode selection

**Fixed 2026-09-04.** Output files were named `results.{split}.{tier}.{csv,md}`, where tier is only
lexical or semantic. Two runs differing in `--modes` therefore wrote to the same path, and the
second silently destroyed the first — with nothing in the surviving file to indicate anything had
been lost. A keyword run was overwritten by a hybrid run during this session and the loss was caught
only because the section headings changed.

Filenames now carry a mode suffix unless the run covers every mode, in which case the short name is
kept. The set of "every mode" is derived from the enum rather than listed, so adding a retrieval mode
cannot leave the check stale and quietly make every partial run look like a full sweep.

The judgment pool is deliberately *not* qualified by mode, because a judgment is a property of a
(query, document) pair regardless of which mode surfaced it. Instead the pool file now unions with
whatever is already on disk. Replacing it had the same shape of bug: a keyword-only run would
discard documents a previous hybrid run had pooled, lowering coverage and biasing comparisons toward
whichever arm ran last.

## Agentic retrieval is two strategies, and the free one is the worst option measured

**Measured 2026-09-04.** The catalog previously carried one `agentic-retrieval` row reporting
parity with a single index, zero queries and zero compute units. Every part of that was misleading.

**It is not an LLM rerank.** With the minimal reasoning effort this sample is forced into — forced
because the knowledge base has no model attached — the documentation is explicit that "there's no
LLM for intelligent query planning or answer synthesis". Ordering comes from the semantic ranker
named by the index's own semantic configuration. Observable in the response: source activity
reports `semanticConfigurationName`, references carry `rerankerScore`, and no `modelQueryPlanning`
or `modelAnswerSynthesis` activity is ever emitted.

**There is a cost dial, and it is enormous.** `resultsProcessing` selects how the service orders
what it gathered:

| | ordering | model tokens | judged nDCG@10 |
| --- | --- | ---: | ---: |
| `rerank` (default) | semantic cross-encoder score | 18,500 | **0.783** |
| `none` | **round-robin across sources** | **0** | **0.457** |
| *single index + semantic* | — | — | *0.723* |

**+0.326 nDCG** separates them (d=1.52, p<0.0001, winning 94 of 100 queries). `agentic-rerank` is
the best row in the study; `agentic-cheap` is the worst, below every hand-written merge including
`interleave`.

The mechanism is the one this report keeps returning to. The cross-encoder score is a property of
the (query, document) pair and consults no corpus statistics, so it is directly comparable across
indexes — the same reason client-side merging on `rerankerScore` works. Decline to pay for it and
there is no comparable score left, so the service falls back to distributing results round-robin
across sources. Which is interleaving. Which this study already measured as the worst merge
available.

So "use agentic retrieval as a cheap cross-index merge engine" is possible and genuinely free of
model cost, and it buys the one merge strategy the rest of this report argues hardest against.

**Cost reporting was wrong.** The row showed 0 queries and 0 compute units because the work happens
server-side where the client's request counter cannot see it. Real figures now come from the
activity array: 2 searches per query, and reasoning tokens in a column of their own rather than
folded into compute units, which is a different meter at a different price.

**This arm cannot be budget-equalized.** Every other striped arm is capped at 25 candidates per
stripe so its total matches the single index's 50. The service rejects any `maxOutputDocuments`
below 50, so agentic retrieval necessarily sees 2x50 against the oracle's 1x50. Its +0.060 over the
single index therefore includes a depth advantage the other striped arms were denied, and should
not be read as a like-for-like win. Structural, like the semantic reranker's fixed 50-document
window.

**Pooling bias struck again.** `agentic-rerank` first measured 0.715 at 87% judgment coverage;
after extending the pool to 99% it measured 0.783. The third time in this study that a strategy
surfacing documents no other approach found was penalised for it. No comparison here is drawn
between arms at different coverage.

### API facts worth knowing

- `maxOutputDocuments` controls how many merged documents come back. **Range 50 to 200**, default
  25 on GA. Verified: requesting 5 or 10 is rejected, 200 returns 194 given enough candidates.
- **`maxOutputSize` silently truncates.** Requesting 200 documents without also raising the size
  budget returns about 49, with no error and no warning — indistinguishable from there being no
  more matching documents. Both caps must be raised together.
- `resultsProcessing` and `maxOutputDocuments` are **preview-only**. GA `2026-04-01` rejects both
  and caps the response at 25 references.
- References carry `docKey`, `title`, `sourceData` (full text), `rerankerScore` and
  `activitySource`, so the merged list is fully usable client-side. You are not limited to a
  synthesized answer.

### Not measured

The genuinely agentic capability — an LLM decomposing one query into several subqueries — needs a
model attached to the knowledge base and `low` or `medium` reasoning effort. That is the one
countermeasure in this study's four-option framing that remains unexercised, and it is the option
most likely to help the short, low-context queries that motivate the whole question.

## A skip handler must not swallow real failures

**Fixed 2026-09-04.** The harness treats `InvalidOperationException` from a strategy as "this
strategy declares its precondition unmet" and drops the row silently, which is correct for a
strategy that cannot run. `AgenticRetrievalFusion` then threw that same type on HTTP failure, so a
genuine 400 from the service produced a missing row with no error printed anywhere — a run that
looked complete and was not.

It now throws `HttpRequestException`, which propagates. The bug hid a real constraint
(`maxOutputDocuments` has a floor of 50) for an entire evaluation run. Exception types that a
caller uses for control flow must not be reused for failures.
