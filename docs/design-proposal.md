# Cross-Index Query — Design Proposal

> **Historical document — written before anything was measured.**
> This is the design as approved at the outset. Several of its predictions were subsequently
> **refuted** by measurement: damage does not concentrate in cross-stripe queries the way section 4
> assumes, the "counterintuitive result" in section 8 turned out not to be testable with an
> oracle-fidelity metric, and rank fusion — barely mentioned as a risk here — proved to be the
> single largest source of loss. For what was actually found, read
> **[`report.md`](report.md)**. For why each decision was made or overturned, read
> [`decisions.md`](decisions.md).

**Status:** Superseded by the report
**Supersedes (conceptually):** `Azure-Samples/azure-search-dotnet-scale/multiple-search-services`

---

## 1. The thesis

A customer hits the **2.4 TB index ceiling on S3** and is *forced* to stripe one logical
corpus across multiple indexes. They aren't doing this for fun; the service has a physical
limit. The single index is and remains the gold standard for relevance.

This sample answers one question, with numbers rather than adjectives:

> **When you are forced to stripe, how much relevance do you lose — and which techniques
> buy it back?**

Non-goals: pretending striping is free; pretending a single index isn't better.

---

## 2. What makes this a 2026 sample, not a 2022 one

The Gen-1 sample concatenated result pages and sorted by raw `@search.score`. That is
provably wrong the moment BM25 corpus statistics diverge between indexes. This sample:

| Gen 1 (existing) | Gen 2 (this) |
| --- | --- |
| Two search *services* | Two *indexes*, one service (the real 2.4 TB scenario) |
| Keyword only | Keyword, vector, and hybrid |
| Sort by raw score | 10 fusion strategies, measured against ground truth |
| No evaluation | Oracle index + nDCG / Recall / rank-correlation harness |
| Asserts correctness | **Quantifies** loss and recovery |
| Admin keys in config | `DefaultAzureCredential` / RBAC |
| Simplistic metadata | LLM-enriched 10k corpus with genres and blurbs |
| — | **Agentic retrieval** (GA) as a first-party collation answer |

---

## 3. The core asymmetry (why each query mode behaves differently)

| Mode | What `@search.score` is | Cross-index comparable? | Why |
| --- | --- | --- | --- |
| **Keyword** | BM25 | ❌ **No** | IDF and `avgdl` are computed *per index*. Different corpus ⇒ different frame of reference |
| **Vector** | Cosine, transformed to ~0.333–1.0 | ✅ **Yes** | Corpus-independent. Depends only on the embedding space |
| **Hybrid** | RRF score (Azure's `k=60`) | ❌❌ **Worse** | Derived from *ranks*, so rank 1 in a junk stripe ties rank 1 in a great stripe |
| **Semantic rerank** | `rerankerScore`, absolute 0–4 | ✅ **Best** | Cross-encoder over (query, doc). No corpus statistics anywhere in it |

Two consequences that drive the whole design:

1. **Vector striping should be nearly free.** We expect near-parity and intend to prove it.
2. **Hybrid must not be fused on Azure's per-index RRF score.** We have to decompose the
   query into its text and vector legs and run *one global* fusion over all legs.

---

## 4. The independent variable nobody expects: *how* you stripe

| Striping | Effect on corpus stats | Expected relevance cost |
| --- | --- | --- |
| **Random / hash** | Stripes converge to near-identical term distributions, `avgdl`, `N` | **Low.** BM25 is already nearly comparable |
| **Semantic / genre** | Each stripe has a distinct vocabulary; `avgdl` also diverges | **High.** IDF divergence is maximised |

Worked example: *"dragon"* is low-IDF inside a fantasy stripe (common ⇒ scores low) and
high-IDF inside a literary-fiction stripe (rare ⇒ scores high). Naive merge therefore
promotes a mediocre literary match above an excellent fantasy one.

**Likely headline guidance:** *if you are striping purely to escape a storage limit,
stripe randomly.* Customers choose semantic/entity striping for operational reasons
(independent update cadence, per-entity schema, selective query routing) and that
convenience has a measurable relevance cost. **We will put a number on it.**

Both strategies ship and both are measured. This is a first-class axis, not a footnote.

---

## 5. Corpus and data pipeline

**Source:** `goodbooks-10k` — `books.csv` + `book_tags.csv` + `tags.csv`.

Two real gaps in the raw data:
- **No genre field.** Genre lives in `book_tags` → `tags`, but the vocabulary is user shelf
  names and mostly junk (`-`, `0-owned`, `00-to-read-00`, `01-alphabet-authors`).
- **No description text.** Title + author is far too thin for vectors or semantic ranking.

Pipeline (`CrossIndexQuery.DataPrep`, committed and re-runnable):

1. Join the three CSVs on `goodreads_book_id`.
2. Map noisy shelf tags → **~20 canonical genres** via a curated allow-list, weighted by tag
   `count`; assign a primary genre + secondary genres.
3. Select **10,000 books** (the full set), genre-balanced where possible.
4. **LLM-generate a blurb** (~120 words) per book — plot, themes, tone, audience.
5. Embed with a single shared provider.
6. Emit `data/books-10k.enriched.json.gz` (~5 MB gzipped) — **committed**.

Committing the enriched corpus means consumers pay **zero** generation cost, the sample runs
offline, and **published eval numbers are exactly reproducible**. The generator ships anyway,
so the corpus is regenerable and extensible.

> **Why 10k, not 1k:** semantic L2 reranks the top 50. At 500 docs/stripe that's 10% of the
> stripe; at 5,000 it's 1% — far closer to real customer behaviour. At 1k we'd publish numbers
> that flatter semantic rerank and vector recall. 10k is materially more defensible.

### Pluggable corpus

`ICorpus` / `ICorpusDocument` abstract the dataset so a **CRM entity corpus**
(contacts / organizations / opportunities / cases) can be dropped in later without touching
fusion, indexing, or eval. That second corpus is the real customer shape — heterogeneous
schemas and wildly different document lengths, which breaks BM25 length normalization *on top
of* IDF divergence.

---

## 6. Index topology

| Index | Contents | Purpose |
| --- | --- | --- |
| `books-stripe-a` | ~5,000 docs | Stripe 1 |
| `books-stripe-b` | ~5,000 docs | Stripe 2 |
| `books-oracle` | **all 10,000** | **Ground truth.** The single-index ideal we measure against |

Identical schema, analyzer, vector profile, compression, and semantic configuration across all
three — otherwise we'd be measuring our own configuration drift instead of striping cost.

Generalized to **N stripes** (default 2); the fusion layer never assumes two.

**Preflight validator** reads back each index's vector dimensions, distance metric, compression
profile, and analyzer, and **hard-fails on mismatch**. Shipped as a reusable diagnostic — it is
useful to customers regardless of which fusion strategy they adopt.

> **Stated prerequisite (README, no hedging):** every stripe must share an identical embedding
> model, dimension count, and distance metric. This is a plain single-index requirement too —
> you can break it just as thoroughly by swapping models halfway through one ingest.

---

## 7. Fusion strategy catalog

`IFusionStrategy` — the extension point of the sample.

### Baselines (to establish the problem)
| # | Strategy | Notes |
| --- | --- | --- |
| 1 | **`NaiveScore`** | Sort by raw `@search.score`. **This is Gen-1 behaviour — the control** |
| 2 | **`Interleave`** | Round-robin by rank. Dumb, but a surprisingly strong floor |

### Rank-based
| # | Strategy | Notes |
| --- | --- | --- |
| 3 | **`GlobalRrf`** | Reciprocal rank fusion over all legs, `k` configurable (default 60 to match Azure), optional per-stripe weights. Immune to score-scale divergence |

### Score normalization (largely to show it underperforms)
| # | Strategy | Notes |
| --- | --- | --- |
| 4 | **`MinMaxNorm`** | Per-stripe min-max. **Demonstrates the junk-promotion failure**: a stripe with no good matches still has its best result normalized to 1.0 |
| 5 | **`ZScoreNorm`** | Per-stripe standardization; better tail behaviour than min-max |

### Recovery techniques — the substance
| # | Strategy | Notes |
| --- | --- | --- |
| 6 | **`GlobalBm25`** ⭐ | Precompute global `N`, per-term `df`, and `avgdl` at index time; ship as a sidecar; **re-score the merged candidate pool client-side with globally-consistent BM25**. The most direct answer to "how do we get things back" for keyword |
| 7 | **`ProbeIdf`** | Same idea, **no sidecar**: recover per-term `df` at query time via cheap `search=<term>&$top=0&$count=true` probes per stripe, then correct IDF. Works against indexes you don't control |
| 8 | **`SemanticRerank`** | Semantic ranking per stripe, merge on absolute 0–4 `rerankerScore`. Zero custom code; adoptable tomorrow. Tiebreak via `RerankerBoostedScore` |
| 9 | **`AgenticRetrieval`** ⭐ | One knowledge base, **one knowledge source per stripe**, GA no-LLM mode, `AlwaysQuerySource=true`. **The service performs the unified rerank.** First-party, deterministic, cheap |
| 10 | **`QuotaMerge`** | Guarantee *k* per stripe. For grouped/faceted UX, striping costs ≈ nothing — this sidesteps comparability entirely |

### Secondary (interface now, implementation deferred)
| # | Strategy | Notes |
| --- | --- | --- |
| 11 | `LocalCrossEncoder` | ONNX cross-encoder over the merged pool. One frame of reference by construction; no tier requirement; fully deterministic. **Interface ships, implementation held** |

---

## 8. The counterintuitive result we expect to find

Semantic L2 reranks **50 docs per query, per index** — a hard, non-configurable cap.

- Single index: **50** documents reach L2.
- Two stripes: **2 × 50 = 100** documents reach L2.

So under semantic reranking, **striping may actually beat the single index on deep recall.**
Agentic retrieval inherits the same property (50 candidates per source per subquery).

This is a real, publishable finding if it holds — and the harness will tell us whether it does
rather than us asserting it. It also implies the oracle index is *not* uniformly an upper bound,
which is a genuinely interesting nuance for the guidance.

---

## 9. Query modes × strategies

| Strategy | Keyword | Vector | Hybrid |
| --- | :---: | :---: | :---: |
| NaiveScore (control) | ✓ | ✓ | ✓ |
| Interleave | ✓ | ✓ | ✓ |
| GlobalRrf | ✓ | ✓ | ✓ |
| MinMaxNorm / ZScoreNorm | ✓ | ✓ | ✓ |
| GlobalBm25 | ✓ | — | ✓ (text leg) |
| ProbeIdf | ✓ | — | ✓ (text leg) |
| SemanticRerank | ✓ | ✓ | ✓ |
| AgenticRetrieval | ✓ | ✓ | ✓ |
| QuotaMerge | ✓ | ✓ | ✓ |

**Hybrid requires decomposition.** A single hybrid query returns only the fused RRF score, not
the component ranks — so we issue the text and vector legs separately per stripe
(4 queries for 2 stripes) and run **one global fusion** across all four lists. Cost: more
queries. Benefit: correctness. We measure both, and we also measure naive-fusing Azure's
per-index RRF score to show precisely how wrong it is.

---

## 10. Evaluation harness

**Ground truth:** the oracle index's ranking for the same query in the same mode.

**Metrics:**
- **nDCG@10 / @50** — graded relevance derived from oracle rank
- **Recall@k** — did the candidate set even contain the oracle's top-k?
- **Kendall's τ** and **RBO** — rank-order agreement (RBO is top-weighted, which matches how
  people actually consume results)
- **Jaccard@k** — raw set overlap

**Also reported per strategy — this is what turns it into guidance:**
- query count, p50/p95 latency, and estimated cost

A strategy that wins nDCG by 2 points at 4× the query cost is a *different recommendation*
than one that wins for free. The output is a **quality/cost/latency table**, not a leaderboard.

**Eval query set** (~100, committed) deliberately spanning:
- head / torso / tail frequency
- single-term vs. multi-term
- **genre-local vs. cross-genre** ← the damage from striping concentrates almost entirely in
  cross-cutting queries; genre-local queries mostly hit one stripe and barely need fusion
- lexical/exact (favours keyword) vs. conceptual (favours vector)

**Sweeps:** striping strategy (random | genre) × query mode (keyword | vector | hybrid) ×
fusion strategy × over-fetch factor.

Output: console table + `results.csv` + `results.md` for direct use in docs.

---

## 11. Solution structure

```
cross-index-query/
├─ README.md                     # page_type: sample front matter
├─ LICENSE  SECURITY.md  CODE_OF_CONDUCT.md  CONTRIBUTING.md
├─ .gitignore  .editorconfig  Directory.Build.props
├─ CrossIndexQuery.sln
├─ data/
│   ├─ books-10k.enriched.json.gz
│   ├─ corpus-stats.json          # global N, df, avgdl sidecar
│   └─ queries.eval.json
├─ src/
│   ├─ CrossIndexQuery.Core/
│   │   ├─ Corpus/        ICorpus, BookCorpus, CorpusDocument
│   │   ├─ Indexing/      IndexBuilder, StripeRouter, PreflightValidator
│   │   ├─ Retrieval/     Keyword | Vector | Hybrid retrievers, leg decomposition
│   │   ├─ Fusion/        IFusionStrategy + the 10 implementations
│   │   ├─ Scoring/       GlobalCorpusStats, Bm25Scorer
│   │   └─ Eval/          Metrics, EvalRunner, Report
│   ├─ CrossIndexQuery.Cli/       # the sample entry point
│   └─ CrossIndexQuery.DataPrep/  # one-time enrichment + embedding
└─ tests/
    └─ CrossIndexQuery.Tests/     # fusion math + metrics, pure & offline
```

Heavier than the Gen-1 flat console app — deliberately. Fusion strategies and metrics are
**pure functions**, so they're unit-testable with zero Azure dependency. That lets a contributor
add a fusion strategy and validate it offline, which is the entire "pluggable sample" premise.

---

## 12. CLI surface

Keeps the Gen-1 verbs recognizable, extends them:

```bash
dotnet run --project src/CrossIndexQuery.Cli -- \
    init      --stripes 2 --striping genre|random --with-oracle

dotnet run ... query   --q "космический опера" \
    --mode keyword|vector|hybrid \
    --fusion naive|rrf|globalbm25|semantic|agentic|... \
    --top 10 --overfetch 3 --explain

dotnet run ... evaluate --modes all --fusions all --striping both --out results.md

dotnet run ... doctor   # preflight: schema/dims/metric/profile parity across stripes
```

`--explain` prints the per-stripe provenance of every result: origin index, raw score,
normalized score, contributing rank in each leg, and final fused score. **Making the fusion
legible is half the teaching value.**

---

## 13. Config, auth, dependencies

- **`net10.0`** (verified: SDK 10.0.400 installed; current LTS)
- **`Azure.Search.Documents` 12.0.0** stable / api-version `2026-04-01` (verified on NuGet)
- **`Azure.Identity` 1.21.0** — `DefaultAzureCredential` default, key fallback for local dev
- Preview-only techniques (`hybridSearch.maxTextRecallSize`, vector
  `threshold: {kind: vectorSimilarity}`) gated behind a build flag on `12.1.0-beta.2`.
  Both are genuinely on-point — an *absolute* vector threshold is inherently cross-index
  comparable and cleanly drops a weak stripe's junk before fusion
- Embeddings: one shared `IEmbeddingProvider` for docs and queries; `text-embedding-3-large`,
  single deployment name, single dimension constant, asserted at startup

---

## 14. Open questions

1. **Packaging** — new standalone repo, a directory alongside the Gen-1 sample, or an in-place
   evolution of it?
2. **`infra/`** — ship azd + Bicep for one-command provisioning (search service, AOAI,
   deployments, RBAC), or keep it manual like Gen-1?
3. **Blurb generation model** — which model, and do we commit the generation prompts?
4. **Local cross-encoder (#11)** — confirmed deferred to interface-only for now.
