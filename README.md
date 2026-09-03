---
page_type: sample
languages:
- csharp
- azdeveloper
products:
- azure
- azure-ai-search
- azure-openai
urlFragment: cross-index-query
name: Cross-index query — what striping costs you, measured
description: |
  Measures the relevance cost of splitting one corpus across multiple Azure AI Search indexes, and
  compares four patterns for merging the results — from free client-side arithmetic to agentic
  retrieval.
---

# Cross-Index Query

**What striping a corpus across two Azure AI Search indexes actually costs you — measured.**

A single Azure AI Search index on S3 holds up to 2.4 TB. Past that, one logical corpus has to span
several indexes, and a question appears that did not exist before: *given two result lists, each
ranked by its own index, what single list do you show the user?*

The common assumption is that the answer is "a worse one, unavoidably." This repository tests that
assumption against 10,000 documents, 100 queries, and 6,805 relevance judgments graded by two
independent LLM judges.

## → **[Read the report](docs/report.md)**

The short version:

| what you do when merging | keyword nDCG vs a single index |
| --- | ---: |
| Merge on **ranks** (RRF, interleave) | **−0.061 to −0.081** — the worst option measured |
| Merge on **raw scores** | −0.015 to −0.002 — small, and judge-dependent |
| Merge on **corrected scores** | **+0.013 to +0.016** |
| **Recompute BM25** client-side | **+0.096 to +0.102** |
| **Rerank** on either side | **parity** |
| **Vector-only** workloads | **parity** (Kendall τ = 1.000) |

Ranges span the two independent judges; 26 of 27 conclusions were identical under both.

The cost of striping lives almost entirely in the merge step, and it is recoverable with arithmetic
over a file you build once — no model, no extra queries, no tier upgrade. The largest and most
reproducible loss comes from **Reciprocal Rank Fusion**, which is the technique most commonly
recommended for merging across indexes.

## What's here

| | |
| --- | --- |
| **[`docs/report.md`](docs/report.md)** | The study. Scenarios, method, results, guidance, threats to validity. |
| **[`samples/`](samples/)** | Four small programs — one per merge pattern — written to be read and pasted. |
| [`src/CrossIndexQuery.Core`](src/CrossIndexQuery.Core) | Retrieval, the fusion catalog, metrics, evaluation harness. |
| [`src/CrossIndexQuery.Cli`](src/CrossIndexQuery.Cli) | `doctor`, `init`, `query`, `evaluate`. |
| [`src/CrossIndexQuery.DataPrep`](src/CrossIndexQuery.DataPrep) | Offline corpus pipeline and the relevance-judging harness. |
| [`data/`](data) | The committed corpus, query set, corpus statistics and relevance judgments. See [`DATA.md`](DATA.md). |
| [`results/`](results) | Raw per-query output behind every number in the report. |
| [`infra/`](infra) | azd + Bicep for a one-command environment. |

> **The corpus contains AI-generated text.** The book descriptions are model-generated, not real
> publisher blurbs, and may contain factual errors about the books they describe. The relevance
> judgments are model-generated rather than human. The corpus also derives from a CC BY-SA 4.0
> dataset, so `data/` is licensed differently from the code. All of this is set out in
> **[`DATA.md`](DATA.md)** — read it before reusing anything in `data/`.

## The four merge patterns

Ordered by what they cost at query time:

| # | pattern | who ranks | extra queries | extra bill | measured p50 |
| --- | --- | --- | --- | --- | ---: |
| 1 | [Query only](samples/Pattern1_QueryOnly.cs) | your code, arithmetic | none | none | **54 ms** |
| 2 | [Self-rerank, external](samples/Pattern2_ExternalRerank.cs) | a model you host | none | your model | 21,792 ms |
| 3 | [Built-in semantic ranker](samples/Pattern3_SemanticRanker.cs) | the service | none | semantic meter | 157 ms |
| 4 | [Agentic retrieval](samples/Pattern4_AgenticRetrieval.cs) | the service, end to end | replaces yours | agentic meter | 327 ms |

## Running it

Prerequisites: .NET 10 SDK, an Azure AI Search service, an Azure OpenAI deployment of
`text-embedding-3-small`, and `az login`. Authentication is `DefaultAzureCredential` throughout —
there are no API keys anywhere in this repository and none should be added.

```powershell
# Point at your own resources
Copy-Item appsettings.Development.json.example appsettings.Development.json
#   ...then edit the two endpoints

dotnet build                                                        # 0 warnings
dotnet test                                                         # 48/48

dotnet run --project src/CrossIndexQuery.Cli -- doctor              # verify the environment
dotnet run --project src/CrossIndexQuery.Cli -- init                # build the three indexes
dotnet run --project src/CrossIndexQuery.Cli -- query "war and betrayal" --explain
dotnet run --project src/CrossIndexQuery.Cli -- evaluate            # reproduce the matrix
```

`init` builds three indexes: two stripes and an **oracle** holding the whole corpus. The oracle is
the un-striped baseline every measurement is taken against — without it, "how much did striping
cost?" has no answer.

Switch scenarios with configuration alone:

```powershell
$env:CIQ_Corpus__StripeMode = 'Genre'       # intentional striping - balanced, divergent vocabulary
$env:CIQ_Corpus__StripeMode = 'Temporal'    # striping to scale - imbalanced, drifting vocabulary
$env:CIQ_Corpus__StripeYearCut = '2013'     #   ...at 9.4:1 imbalance
$env:CIQ_Corpus__StripeMode = 'Random'      # the control
```

## The corpus

**10,000 books** with LLM-generated ~120-word descriptions and `text-embedding-3-small` vectors,
committed in `data/books.enriched.json` (30 MB) so the sample runs without a generation step.

Derived from [goodbooks-10k](https://github.com/zygmuntz/goodbooks-10k) (CC BY-SA 4.0). The
descriptions are **synthetic text about real books** — generated from title, author, year and genre,
not sourced from publishers — and exist because the source dataset ships no description field.

Vectors are stored as **base64 int8** rather than decimal text — 30 MB instead of 192 MB, at a
measured cost of 0.10% recall@10. Read the corpus through `CorpusFile`; a bare `JsonSerializer` will
not decode them.

`data/judgments.json` holds 6,805 graded (query, document) relevance judgments, and
`data/judgments.second-judge.json` the same pool graded by a second model. Both are committed so
that two people comparing results are comparing their search services rather than their judges.

Full provenance, licensing and AI-content disclosure: **[`DATA.md`](DATA.md)**.

## Limitations

Stated up front, and in more detail in [report section 9](docs/report.md#9-threats-to-validity):

- **10,000 documents, not 2.4 TB.** The core distortion is scale-invariant, but candidate-window
  effects at real scale are untested.
- **Two stripes.** Everything here is N=2; more stripes should make rank-fusion damage worse.
- **Model-generated relevance judgments**, cross-checked by a second model but not by humans. Both
  models come from the same family, so a shared bias would be invisible.
- **One corpus, one query set, measured once.** Nothing has been replicated on a second domain.
- **Relevance tuning held constant.** No scoring profiles or field boosting — those are orthogonal
  to striping, but it means these numbers describe striping in isolation, not a tuned system.

## Contributing

See [`CONTRIBUTING.md`](CONTRIBUTING.md). Adding a fusion strategy is deliberately cheap: strategies
are pure functions with no Azure dependency, so a new one can be written and unit-tested entirely
offline, and it joins the evaluation matrix automatically.

If you think a published number is wrong, the per-query data behind all of them is in
[`results/`](results) — open an issue with the specific comparison.

## Notes for contributors

- Fusion strategies and metrics are **pure functions** with no Azure dependency, so a new strategy
  can be added and validated entirely offline. Implement `IFusionStrategy`, register it in
  `FusionStrategyRegistry.CreateDefault`, and it appears in the evaluation matrix automatically.
- Run `dotnet test` **without** `--nologo`. Under the .NET 10 Microsoft.Testing.Platform runner that
  flag causes a silent "zero tests ran" with exit code 5.
- Working notes live in [`docs/state.md`](docs/state.md) and
  [`docs/decisions.md`](docs/decisions.md). The decisions file records what was measured and why,
  including the predictions that turned out to be wrong.

## Licence

Code, documentation and results: [MIT](LICENSE).
Data under `data/`: [CC BY-SA 4.0](https://creativecommons.org/licenses/by-sa/4.0/), inherited from
[goodbooks-10k](https://github.com/zygmuntz/goodbooks-10k). See [`DATA.md`](DATA.md).

## Trademarks

This project may contain trademarks or logos for projects, products, or services. Authorized use of
Microsoft trademarks or logos is subject to and must follow
[Microsoft's Trademark & Brand Guidelines](https://www.microsoft.com/legal/intellectualproperty/trademarks/usage/general).
Use of Microsoft trademarks or logos in modified versions of this project must not cause confusion
or imply Microsoft sponsorship. Any use of third-party trademarks or logos is subject to those
third-party's policies.
