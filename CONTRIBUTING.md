# Contributing

Thank you for your interest. This sample exists to be argued with: its whole value is that its
numbers are measured rather than asserted, so a contribution that shows a number is wrong is as
welcome as one that adds a feature.

## Contributor License Agreement

This project welcomes contributions and suggestions. Most contributions require you to agree to a
Contributor License Agreement (CLA) declaring that you have the right to, and actually do, grant us
the rights to use your contribution. For details, visit https://cla.opensource.microsoft.com.

When you submit a pull request, a CLA bot will automatically determine whether you need to provide a
CLA and decorate the PR appropriately (e.g., status check, comment). Simply follow the instructions
provided by the bot. You will only need to do this once across all repos using our CLA.

This project has adopted the [Microsoft Open Source Code of Conduct](CODE_OF_CONDUCT.md).

## Adding a fusion strategy

This is the most useful kind of contribution, and it is deliberately cheap to make.

Fusion strategies and metrics are **pure functions with no Azure dependency**, so a new strategy can
be written and validated entirely offline:

1. Implement `IFusionStrategy` in `src/CrossIndexQuery.Core/Fusion/`.
2. Register it in `FusionStrategyRegistry.CreateDefault`.
3. Add tests in `tests/CrossIndexQuery.Tests/` — `FusionStrategyTests` shows the pattern.
4. Run `dotnet test`.

It will then appear in the evaluation matrix automatically. If you have an Azure AI Search service,
`dotnet run --project src/CrossIndexQuery.Cli -- evaluate` will measure it against the same oracle
and the same committed relevance judgments as everything else.

Two conventions worth following, both of which exist because they were got wrong once:

- **Declare your preconditions rather than guessing.** A strategy that cannot operate on the results
  it was given should throw `InvalidOperationException`; the harness records no row for it. Silently
  producing a degraded answer misrepresents it as having tried and failed.
- **If your strategy reranks, set `RequiresSemanticRanker`.** The harness compares against a baseline
  ranked by the same function, and a reranking strategy scored against a BM25 baseline measures the
  difference between two scoring functions rather than the cost of striping.

## Challenging a result

If you think a number in [`docs/report.md`](docs/report.md) is wrong, the raw per-query data behind
every one of them is committed in [`results/`](results). Open an issue with the specific comparison
and what you think it should be. Disagreements about method are more useful than disagreements about
conclusions.

## Building

```powershell
dotnet build     # 0 warnings; TreatWarningsAsErrors is on
dotnet test      # do not pass --nologo; see below
```

`dotnet test --nologo` reports "zero tests ran" and exits 5 under the .NET 10
Microsoft.Testing.Platform runner, without loading the test assembly. Bare `dotnet test` is correct.
