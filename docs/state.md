# State — live checklist

**Working document. Delete before publishing to Azure-Samples.**

Last updated 2026-09-04, after the control experiments that reframed the headline claim.
This file is the build checklist; `docs/report.md` is the deliverable.

---

## Status at a glance

| Area | State | Notes |
| --- | --- | --- |
| Solution scaffold | **done** | net10.0, 4 projects, central package management |
| Configuration + auth | **done** | `DefaultAzureCredential`, options binding |
| Corpus join from CSVs | **done** | `data/books.base.json`, 9,980/10,000 genre-classified |
| Blurb generation | **done** | **10,000** blurbs; the one content-filtered title was replaced |
| Embedding | **done** | **10,000** docs, 1536-dim, int8 base64, 30 MB, verified |
| Corpus statistics | **done** | per split: genre + 5 temporal cuts; carries per-stripe avgdl |
| Index schemas + router | **done** | Random, Genre, **Temporal** routing, all run live |
| Retrieval | **done** | run live across 6 index configurations |
| Fusion catalog | **done** | 15 strategies + 2 falsifying controls |
| Evaluation harness | **done** | judged + fidelity metrics, pooling, cost accounting |
| Query set | **done** | 100 committed queries, 1-7 content terms |
| Relevance judgments | **done** | 7,016 graded 0-3, committed; 2nd judge set committed too |
| CLI | **done** | 5 commands; `compare` re-runs the statistics offline |
| Offline tests | **done** | 79/79 passing, incl. sample- and control-equivalence |
| Infrastructure | **done** | azd + Bicep, server-side validated `Succeeded` |
| Pattern 1 query-only | **measured** | both scenarios, all three modes |
| Pattern 2 external rerank | **built**, measuring | `ExternalRerankFusion`, ~24 s/query |
| Pattern 3 semantic ranker | **measured** | Scenario A + imbalance sweep |
| Pattern 4 agentic retrieval | **built**, measuring | knowledge base over both stripes |
| Report | **drafted** | `docs/report.md` |
| Samples | **done** | `samples/`, one per pattern |
| README | **done** | problem framing, worked example, diagram |
| Significance testing | **done** | bootstrap CI + t + Wilcoxon + Holm, in-tool |
| Controls | **done** | `local-bm25`, `single-index-rescored`, exhaustive KNN |

---

## Step 0 — confirm the corpus survived the move

The offline data pipeline is **finished**: 10,000 documents, blurbs and vectors, nothing left to
regenerate. Just confirm the files copied intact, because a truncated corpus fails in a confusing
way three steps later.

```powershell
cd C:\dev\cross-index-query
Get-ChildItem data\books.enriched.json | Select-Object Name, Length   # expect ~32,000,000 bytes
Get-Content data\corpus-manifest.json -Raw                            # expect documentCount 10000
dotnet build                                                          # expect 0 warnings
dotnet test                                                           # expect 40/40
```

Vectors are stored as **base64 int8**, so `contentVector` reads as a string rather than an array of
numbers — that is correct, not corruption. Always load the corpus through `CorpusFile`; see
`docs/decisions.md` for why and what it costs (0.10% recall@10).

If the corpus is missing or truncated, regenerate it — the stage is idempotent, takes about eight
minutes, and rewrites `corpus-manifest.json` too:

```powershell
dotnet run --project src\CrossIndexQuery.DataPrep -- embed
```

---

## Step 1 — corpus statistics — **done, regenerated under genre striping**

Already run; `data/corpus-statistics.json` exists. Re-run only if the corpus **or the stripe mode**
changes.

```powershell
dotnet run --project src\CrossIndexQuery.DataPrep -- stats
```

Measured output worth keeping: 39,121 distinct terms, 91.6 average tokens, stripe A 5,292 documents
/ 26,350 terms, stripe B 4,708 / 28,006. Largest per-stripe IDF disagreements among terms in ≥40
documents — `koontz` 4.407 vs 9.150 (Δ4.74), `principles` 9.267 vs 4.555 (Δ4.71), `organizational`
9.267 vs 4.756 (Δ4.51), `agatha` 4.735 vs 9.150, `christie` 4.779 vs 9.150, `gaiman` 4.848 vs 9.150.
**This is the evidence for the sample's premise and belongs in the README.**

Produces `data/corpus-statistics.json`: global N, per-term document frequency, and `avgdl`, both
globally and per stripe. `IdfCorrectionFusion` depends on this.

### The sidecar is striping-mode-specific — this bit us once

The file was originally generated under `StripeMode.Random`, because that was the unflipped default
in both `appsettings.json` and `CorpusOptions.StripeMode`. Nothing recorded a decision to use
random; genre was always the intent, and `data/genre-map.json` says so in its own comment. The stage
simply used whatever the config said and printed output that looked entirely plausible.

The numbers previously recorded here (29,219 / 28,824 terms; `retired` 6.002 vs 5.025; `alabama`
6.086 vs 5.122) were that random split. They were **not** evidence for the sample's premise:

| global df | random Δ | genre Δ |
| --- | --- | --- |
| 40–59 | 0.23 | 0.82 |
| 60–99 | 0.19 | 0.73 |
| 100–199 | 0.14 | 0.69 |
| 200–499 | 0.09 | 0.61 |
| 500–1499 | 0.06 | 0.55 |
| 1500+ | **0.03** | **0.41** |
| overall | 0.162 | 0.711 |

Under random, disagreement decays to nothing as `df` rises — the 1/√df signature of sampling noise.
The three terms quoted as headline evidence all sat at df 40–57, right against the `df >= 40`
reporting cutoff, which is exactly where that noise peaks. Under genre it stays large at df ≥ 1500,
where noise cannot explain it, and the top terms are thematically interpretable (`koontz`, `agatha`,
`gaiman` common in A; `principles`, `organizational` common in B).

Two guards were added so this cannot recur silently:

- `dataprep stats` now prints `stripe mode` in its report.
- `CorpusOptions.StripeMode` now defaults to `Genre`, and `appsettings.json` says `"Genre"`.

**Still open:** `CorpusStatistics.FileName` is a `const`, so there is exactly one sidecar filename
and `SaveAsync` overwrites it. Measuring both striping modes needs a mode-qualified filename plus
provenance recorded *inside* the sidecar, so `IdfCorrectionFusion` can refuse a sidecar that does
not match the indexes. Today a mismatch is silent and produces confidently wrong IDF corrections.
The random-split sidecar is preserved outside the repo in the session artifacts folder as
`corpus-statistics.random-split.json`.

---

## Step 2 — first live CLI run — **done**

```powershell
dotnet run --project src\CrossIndexQuery.Cli -- doctor
```

`doctor` now reports **all checks passed, 0 warnings** against the live service.

Two bugs were found and fixed here, both in this codebase's wiring rather than in Azure, exactly as
predicted:

- **`InitCommand` deserialized `GenreMap` with a bare `JsonSerializer`.** `GenreMap` has a private
  constructor and a static `Load` factory that flattens `stripeGroups` into the A/B genre lists, so
  this threw `NotSupportedException`. Every other call site already used `GenreMap.Load`. Had the
  type been constructible, the failure would have been worse than a crash: the JSON shape does not
  match the properties, so it would have produced empty stripe groups and silently routed the whole
  corpus by hash while reporting `Stripe mode: Genre`.
- **`doctor`'s debug-subscore check could never pass.** It probed with `RetrievalMode.Keyword`, but
  `StripeRetriever` only sets `QueryDebugMode.Vector` when the request has a vector leg, so the
  subscores were always absent and the check always warned — while telling you to go and change
  `HybridLegFusion` for no reason. It now runs a genuine hybrid probe, and `CheckEmbeddingAsync`
  returns its vector instead of discarding it so the probe has one. All probes share one
  `ProbeQuery` constant, because searching for one string while supplying the embedding of another
  produces a text leg that contributes nothing and a "missing" subscore that the query shape
  guaranteed.

---

## Step 3 — build the indexes — **done**

```powershell
dotnet run --project src\CrossIndexQuery.Cli -- init
```

Creates `stripe-a`, `stripe-b`, `oracle` and uploads. Remember: **`IndexProvisioner` does its own
partitioning** — don't pre-partition the input.

Measured under genre routing: **stripe A 5,292 / stripe B 4,708 / oracle 10,000**. That is the pure
genre split of 5,282 / 4,698 plus the 20 documents with no allow-listed genre, which fall back to
the hash router and land 10/10. Under hash routing it is A 5,054 / B 4,946.

Immediately after upload the oracle reported 9,500 — indexing is eventually consistent, and
`VerifyCountsAsync` reports rather than asserts for exactly this reason. It read 10,000 on the next
`doctor` run.

---

## Step 4 — eyeball a few queries before trusting the harness — **done**

```powershell
dotnet run --project src\CrossIndexQuery.Cli -- query "<something>" --explain
```

`--explain` is the teaching surface: it makes the abstract corpus-statistics argument concrete for a
single query.

Worth knowing when picking demo queries: a **single-genre** query is a poor demonstration. `"koontz
suspense"` returns all ten results from stripe A because stripe B genuinely has nothing relevant
(its best hits score ~3.65 against A's 15.7), so naive merging looks fine. The damage lives in
**cross-cutting** queries. `"war and betrayal"` splits 5/5 across the stripes, and switching from
`naive-score` to `idf-correct-probe` reorders positions 6 and 7 — promoting a stripe-A "betrayal"
match over a stripe-B "war" match.

First-query latency was 2,504 ms and 3,976 ms against the two stripes. That is serverless cold
start, and it is the concrete case for why warmup is mandatory before any timing measurement.

---

## Step 5 — evaluate

```powershell
dotnet run --project src\CrossIndexQuery.Cli -- evaluate --limit 10   # smoke first
dotnet run --project src\CrossIndexQuery.Cli -- evaluate              # full matrix
dotnet run --project src\CrossIndexQuery.Cli -- evaluate --semantic   # semantic strategies
```

Defaults: `WarmupQueries=10`, `Repetitions=3`, `TopK=10`, `PerStripeK=50`, output to `results/`.

Two more bugs were found and fixed by the first smoke run:

- **`ReadSubscores` threw `NullReferenceException` on every hybrid run.** `subscores.Vectors` can
  contain a **null entry** for a document retrieved by the text leg alone — the mirror image of
  probe #3's bonus finding — and the loop dereferenced it. Keyword and vector modes completed 10/10
  before hybrid crashed, so this was invisible until hybrid ran.
- **Results were written outside the sample.** `EvaluateCommand` called
  `RepositoryLocator.ResolveDataDirectory("..")` to find the repo root, which searches for
  `<dir>/../genre-map.json` — a marker that cannot exist. The search always failed and fell through
  to `Path.GetFullPath("..")`, the parent of the working directory, so output landed in
  `C:\dev\results`. Added `RepositoryLocator.ResolveRepositoryRoot`, which derives the root from the
  already-working data-directory resolution.

### Semantic strategies are excluded from non-semantic runs

The first smoke run scored `semantic-rerank` at nDCG 0.484 in keyword mode, against 0.908 for
`naive-score` — apparently contradicting the design's claim that semantic rerank is the *best*
cross-index option. It was a measurement artifact, not a finding.

Ground truth is the oracle's ordering for the same query, and `useSemanticRanker` is one flag
applied to both the oracle and the stripes. Ten of the eleven strategies only reshuffle the fan-out
they were handed, so they rank by the same function the oracle did. `SemanticRerankFusion` issues
its *own* reranker queries unconditionally — visible in the cost, 0.0014 CU and 189 ms against
0.0006 and 63 ms for everything else — so with `--semantic` off it produced a cross-encoder ordering
scored against a BM25 baseline. It was being penalised for not being BM25.

`IFusionStrategy.RequiresSemanticRanker` (default `false`) now marks the two semantic strategies,
and the harness skips them unless `--semantic` is set. This mirrors what `SemanticScoreFusion`
already did by throwing — which is why `semantic-score` correctly produced no row while
`semantic-rerank` produced a misleading one. The guard is in the harness, not the strategy, so
`cli query -s semantic-rerank` still works: there is no baseline there to be inconsistent with.

**The rule worth keeping:** the oracle and the strategy must rank by the same function, or the
comparison measures the scoring function instead of the striping.

Two things still to watch:

- **Warmup must be discarded.** If cold-start latency leaks into the measured runs, the latency
  column is fiction.
- **Check the 2×50 semantic hypothesis** in the `--semantic` run, which is where
  the oracle is reranked too. Striping may beat the oracle on deep recall because two stripes push
  100 documents through the cross-encoder versus the oracle's 50. If that shows up, it is a
  genuinely interesting finding — and it must be reported as a measurement with its mechanism
  explained, never as general guidance.

---

## Step 6 — the four patterns — **built and measuring**

Open question 11a was answered: agentic retrieval appears as a row in the **same** results table,
with each approach charged only for what it actually spends. `IFusionStrategy.PerformsOwnRetrieval`
carries that — the harness skips the shared fan-out cost for a strategy that retrieved for itself.

The catalog now spans all four usage patterns:

| pattern | strategies | added query cost |
| --- | --- | --- |
| 1 — query only | `naive-score` `interleave` `quota-merge` `global-rrf` `minmax-norm` `zscore-norm` `vector-similarity` `hybrid-legs` `idf-correct-sidecar` `idf-correct-probe` **`global-bm25`** | none |
| 2 — external rerank | **`external-rerank`** | a model call per candidate, ~24 s/query |
| 3 — built-in semantic | `semantic-score` `semantic-rerank` | separate meter |
| 4 — agentic | **`agentic-retrieval`** | separate meter, service-side retrieval |

`global-bm25` was design proposal item 6 and had never been implemented. It recomputes BM25 from
document text using global statistics rather than rescaling the scores the indexes returned, and it
is the best-scoring option measured: **0.582 judged nDCG against the single index's 0.542**
(+0.040, p=0.029) at identical query cost.

---

## Step 7 — the report — **drafted**

`docs/report.md` is the customer-facing deliverable, with `samples/` carrying one readable program
per pattern. Structure: confirm the objection, explain the mechanism, present both scenarios, price
every remedy, state the threats to validity.

Outstanding before it can ship:

- **Cross-model judge agreement.** The judge and the corpus came from the same model family. This is
  the largest unaddressed threat and it is named in the report rather than hidden.
- **Semantic-tier numbers for Scenario A** are still running at the time of writing; the report's
  tier-3 table carries the imbalance-sweep figures.
- A README that points at the report rather than duplicating it.

---

## Step 8 — before any wider publication

`HANDOFF.md` has been removed — it carried the live subscription id, tenant id, service endpoints
and signed-in user OID, none of which belong in a shared repository.

`decisions.md` and this file are kept deliberately. They are build notes rather than polished
artifacts, but they record what was measured, what was predicted wrongly, and why each choice was
made — which is the context a reader needs to trust or challenge the report. If this ever moves to
a public Azure-Samples repository, fold the durable parts of `decisions.md` into
`design-proposal.md` and drop both working files then.

`design-proposal.md` is still the pre-measurement design and has **not** been reconciled with what
was actually found. Several of its hypotheses were refuted — notably the expectation that damage
concentrates in cross-stripe queries, and the framing of the 2x50 semantic effect. Read it as a
record of the original plan, not as a description of the system.

---

## Step 9 — control experiments (2026-09-04) — **done**

Added because the study's largest claim had never been attacked by anything capable of refuting it.

- [x] **`local-bm25`** — same tokenizer, constants, fields and arithmetic as `global-bm25`; only the
      statistics source differs. Isolates "global statistics" from "client-side rescoring".
- [x] **`single-index-rescored`** — the identical `global-bm25` instance applied to the single
      index's own results. The only arm in the study that differs from a striped arm in the split
      alone.
- [x] **Result: the +0.096 headline was 95% rescorer.** Striping-attributable effect is +0.005,
      p=0.28, interval spanning zero. The "striping can beat a single index" claim is withdrawn;
      "striping is free when you merge on recomputed scores" replaces it. See `docs/decisions.md`.
- [x] **Exhaustive KNN** — proved vector striping is *exactly* free (fidelity 1.000, recall 1.000),
      and that the 0.974 seen under HNSW is approximate-search error, not a striping cost.
- [x] `ControlEquivalenceTests` pins the one-variable property so a later edit cannot silently
      introduce a second difference and invalidate the decomposition.

## Step 10 — statistics moved into the tool — **done**

Previously computed ad hoc in PowerShell, which is neither reproducible nor reviewable.

- [x] `SignificanceTests` — paired bootstrap CI (10,000 resamples, fixed seed), paired t, Wilcoxon
      signed-rank with tie correction, Holm step-down correction.
- [x] Pinned against R reference values (`t.test`, `p.adjust`) in `SignificanceTestsTests`.
- [x] Results markdown now carries a significance table with intervals, effect sizes, corrected
      p-values and win/loss/tie counts.
- [x] `cli compare` re-runs any pairwise test against the committed CSVs — **no Azure access
      required**, so a sceptical reader can verify every claim for free.

## Step 11 — bugs found while hardening

- [x] **Results files could silently destroy each other.** Filenames encoded only split and tier, so
      two runs differing in `--modes` collided. Observed live: a hybrid run overwrote the keyword
      results. Mode suffix added; full sweeps keep the short name.
- [x] **Judgment pool was replaced rather than unioned**, so a mode-limited run discarded documents a
      previous run had pooled, lowering coverage and biasing comparisons.
- [x] **`IndexProvisioner`'s 207 handler was unreachable.** `UploadDocumentsAsync` defaults
      `ThrowOnAnyError` to false, so partial failure returns per-document results instead of
      throwing; the catch could never fire and `pending` was never narrowed. One throttled document
      would abort a 10,000-document load. Now inspects `result.Results`, resends only transient
      failures, and fails fast on genuinely malformed documents.

## Step 12 — remaining

- [ ] Re-run the temporal (scale-striping) sweeps under the current code. Their committed results
      predate the RBO fix, the significance table and the controls.
- [ ] Re-run the semantic tier for the same reason.
- [ ] Push the corrected report, README and results to `mrcarter8/cross-index-query`.
- [ ] Delete the abandoned private `mcarter_microsoft/cross-index-query` repo (needs a token with
      `delete_repo`; must be done by hand).
