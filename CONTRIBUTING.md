# Contributing

Thank you for helping. The most useful contributions right now are, in order: **human review** of the generated code,
**the dimensions COVERAGE.md lists as not measured**, and **a second language**. Small fixes are just as welcome.

Questions and ideas go to [Discussions](https://github.com/open-assurance/reference-implementation/discussions); bugs and
concrete work to [issues](https://github.com/open-assurance/reference-implementation/issues). For anything larger than a
small fix, open or comment on an issue first so the approach can be agreed before you write it.

## Build and test

Requirements: the .NET 10 SDK, git, and Python 3 for the scripts in `tools/` and `benchmark/`.

```bash
dotnet build Cai.Reference.slnx          # engine, scoring library, tests
dotnet test Cai.Reference.slnx           # all tests (fold vectors, detectors, outputs); must be green before a PR

# scan a C# repository and fold the result
dotnet run --project src/Cai.Reference -- scan <path-to-repo> --out <out-dir>
dotnet run --project src/Cai.Reference -- verify <out-dir>/evidence.json

python3 tools/coverage-md.py             # regenerate COVERAGE.md after changing coverage or a method text
```

Scans are deterministic: two scans of the same tree must be byte-identical, and a test checks it. Do not introduce
timestamps, culture-dependent formatting, unordered iteration in output, network calls or package restore.

## Sign your commits (DCO)

Every commit must carry a `Signed-off-by:` line certifying the [Developer Certificate of Origin](https://developercertificate.org/)
— that you wrote the change or otherwise have the right to submit it under Apache-2.0. Use:

```bash
git commit -s
```

A check on every pull request rejects commits without the line. To fix a branch: `git rebase --signoff main` and
force-push.

## The clean-room rule

Implement from the **published** CAI text only: the specification and documentation at
[codeassuranceindex.info](https://codeassuranceindex.info), the
[standard's repository](https://github.com/code-assurance-initiative/CodeAssuranceIndex) docs, ADRs, schemas and rubric
catalog, and the [scanner benchmark](https://github.com/code-assurance-initiative/scanner-benchmark) contract and
taxonomy. **Never read or copy another CAI engine's source**, including the standard's own scorer source and other
engines' benchmark mappings or results. Black-box use of a published CLI (e.g. `cai score`, `cai verify`) to check a
reading is fine, and that is how the existing gaps were settled.

When the text does not decide something, do not guess silently: add an entry to [SPEC-GAPS.md](SPEC-GAPS.md) with the
quote, the open question, the resolution you chose and what the oracle confirmed, and reference it in your PR. If it is
a real defect in the standard, also report it upstream to the standard's repository.

## Adding or fixing a dimension

1. Read the dimension in `rubrics/<version>/rubric-catalog.json` (`whatItMeasures`, `evaluator`, `family`, polarity) and
   the matching benchmark taxonomy concepts.
2. Write a failing test first in `tests/Cai.Reference.Tests/` using `Fixture` (see `CodeHealthDetectorTests.cs`): one
   case that must fire, and one near-miss that must not.
3. Implement the detector in the lens's `src/Cai.Reference/Engine/Detectors.<Lens>.cs`. Emit findings with
   `ctx.Add(new Finding(rule, dimension, …))`, a score with `ctx.Measure(id, …)`, or `ctx.Skip(id, reason)` when the
   dimension does not apply to this repository. Use the shared scoring shapes in `Model.cs` rather than inventing one.
4. Register any new taxonomy concept in the rule table in `Model.cs`, remove the dimension from
   `Coverage.StaticReasons` in `Coverage.cs`, add its method text to `METHOD` in `tools/coverage-md.py`, and regenerate
   COVERAGE.md.
5. Run the benchmark units that touch the dimension
   (`benchmark/run-benchmark.sh <units-dir> <scanner-benchmark-clone> <out-dir> [unit ...]`; see the script header) and
   report recall, trap resistance and noise in the PR. Detectors must generalise: nothing may name a benchmark file, path or string.

Dimensions with `evaluator: llm` and the runtime `AX*1` metas are advisory: anything emitted for them must carry
`advisory: true` and must never enter the fold (SPEC-GAPS G-01, G-02). The engine itself stays deterministic and offline;
discuss any model-backed or runtime evaluator in an issue first — it would have to be opt-in and off by default.

## Adding a language

A language is a large change; open a `language` issue first. The shape that fits this code base:

- an inventory/project model for the ecosystem (manifests, lockfiles, package roles) next to `Repo.cs`;
- a parser that gives syntax (and, where possible offline, semantics) the way `Workspace.cs` does for Roslyn;
- detectors that reuse the existing taxonomy concepts and dimension ids, plus the language-specific ones the catalog
  defines (e.g. the `R*` family for JavaScript/TypeScript);
- removal of the corresponding "outside this engine's target" entries in `Coverage.cs`;
- fixtures and tests for each detector, and a benchmark run over that language's units.

## Pull requests

Keep them focused; one dimension or one concern per PR. The PR description says what changed, how it was tested, which
benchmark numbers moved, and which SPEC-GAPS entries were added or changed. All conduct is governed by the
[Code of Conduct](CODE_OF_CONDUCT.md).
