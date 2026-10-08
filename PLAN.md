# Plan — CAI reference implementation (C#/.NET), clean room

Status: proposed, awaiting go. Rubric target: `rubric-2026.10.2` (165 dimensions, 10 lenses).

## 1. What the published text pins down, and what the oracle had to settle

Read: spec README, docs/*.md, ADRs, examples, schemas, rubric catalogs; codeassuranceindex.info /spec, /dimensions,
/rubric, /implementations, /page-cli, the two founding articles; benchmark CONTRACT.md 1.4, taxonomy, answer-key
schema, registry, coverage matrix, the 15 C# unit keys and READMEs. Not read: ./spec/src, ./spec/tests,
benchmark/mappings/watchdog*, benchmark/results/*.

The fold, from the text: dimension effective = score × coverage; category = confidence-weighted mean of its
dimensions (score 0–10 → ×10); meta-dimensions enter their lens directly (×10); lens = worst-first OWA over
categories + metas with q = 0.75 (weights ∝ q^rank, renormalised); headline = worst-first OWA over measured lenses
with q = 0.55; bands cut at 25/50/70/90 on the unrounded number; advisory items never enter; architecture lens dropped
with no analysable project and capped at 69 when thin; a contributor below 4/10 caps its lens band at Adequate;
headline band ≤ weakest category band + 1; quality bar shifts lens cutlines (offset × lens-group factor, clamped by
exemplaryCeiling 98 / poorFloor 5), never the headline's.

Settled only by black-box probes of `cai score` (each is a SPEC-GAPS entry):
- advisory exclusion is driven by the bundle's `advisory` flag, not by the catalog's `evaluator: llm` (an LLM
  dimension sent without the flag is folded); SC1 / AX*1 say "advisory, never scored" in prose but fold unless flagged;
- confidence 0 ⇒ absent; coverage 0 ⇒ a measured zero (still folds, still gates);
- gate is strict `< 4` on the effective (coverage-scaled) value, applies to metas too, skips advisory;
- architecture: dropped iff analyzableProjects == 0; capped iff projects < 2 AND productionLoc < 1500;
- band coherence uses the weakest *category*, banded at baseline cutlines;
- unknown dimension ids and meta ids inside `dimensions` are accepted and routed by category; a meta's declared
  `lens` that contradicts the catalog is accepted (while a dimension's contradicting `category` is refused);
- lens → quality-bar group is not in any catalog: codeHealth and architecture behave as `foundational`,
  securityCompliance as `safety`; maturity / readiness / conditional lenses still to be separated (operational vs default);
- quality-bar names outside the four resolve to production silently;
- headline shown to 1 dp by the CLI, 2 dp in deliveries; `verify` tolerance ±0.5; the text defines no precision.

## 2. Architecture

```
reference-implementation/
  Cai.Reference.slnx
  src/Cai.Reference.Scoring/   the fold: catalog, evidence model, OWA, bands, gates, quality bar, verify  (library)
  src/Cai.Reference/           the engine + CLI: repo model, offline Roslyn, git mining, detectors, evidence, SARIF
  tests/Cai.Reference.Tests/   xunit: conformance vectors (oracle-checked), detector fixtures, determinism, schema
  rubrics/rubric-2026.10.2/    vendored catalog (Apache-2.0 content of the standard); --rubrics overrides
  data/                        osv-nuget snapshot (date + sha256 + regen script), endoflife dotnet snapshot, licence policy
  benchmark/                   mapping (generated from the rule table), run script, per-unit scores
  COVERAGE.md  SPEC-GAPS.md  RESULTS.md  README.md  LICENSE  NOTICE
```

Pipeline: `git ls-files` inventory → project model (csproj / Directory.*.props / packages.lock.json / global.json /
sln; role = test | web | domain | application | infrastructure | tool | generated) → one Roslyn `CSharpCompilation` per
project from source + the installed shared-framework reference assemblies (no restore, offline; third-party types stay
unresolved and detectors degrade to syntax) → Razor parser for .razor/.cshtml/.html → YAML/Dockerfile/HCL readers →
`git log --numstat` and `git log -p --all` → detectors → findings (rule = concept id, dimension attribution) +
per-dimension measurements → evidence bundle + SARIF 2.1.0 + scores.json → fold → headline.

Determinism: ordinal sorting everywhere, invariant culture, no timestamps, history windows relative to the HEAD
commit date, data snapshots carry their as-of date, stable JSON writer. Test: scan twice ⇒ byte-identical.

Scoring shapes (parameters tabulated in COVERAGE.md): finding dimensions — per-file saturating load
(1 − 0.5^n) summed and normalised per production kLOC, score = 10·exp(−k·load); metric dimensions — thresholds on
shares; posture dimensions — checklist / Documented→Verified→Prevented ladder; reward dimensions — emitted only when
there is something to credit (the fold has no polarity, so a neutral number would still be a deduction).

Lens applicability (the text gives no rule — recorded as a gap): accessibility iff markup files exist;
domainModelling iff a domain model is detected (Domain project/namespace, aggregate/entity base types, or
identity-bearing classes with behaviour); eventDriven iff a messaging library, handler pattern or event types exist;
eventSourcing iff an event-fold/event-store pattern exists; performance whenever there is C# code.

Evidence extras (allowed by the schema, ignored by the oracle — verified): `notMeasured[{id, reason}]`,
`engine{name, version, snapshots}`. Quality bar: `--quality-bar`, default production (the text says the codebase
"declares" it but names no place — gap).

CLI: `cai-ref scan <repo> [--rubrics DIR] [--rubric V] [--out DIR] [--quality-bar B] [--nuget-packages DIR]`,
`cai-ref score <evidence>`, `cai-ref verify <evidence> [--expect N]`, `cai-ref mapping`.

## 3. External tools / packages (all pinned, all offline at scan time)

- Microsoft.CodeAnalysis.CSharp 5.9.0 — parsing + semantic model.
- AngleSharp 1.8.4 — HTML DOM (with source positions) for .razor/.cshtml/.html accessibility checks after Razor code is stripped. (Plan originally named Microsoft.AspNetCore.Razor.Language; its syntax tree is internal, so it was replaced.)
- YamlDotNet 18.1.0 — Kubernetes, compose, workflows.
- JsonSchema.Net 9.4.0 (tests only) — evidence validated against `cai-delivery-1.0.schema.json#/$defs/evidence`.
- xunit + Microsoft.NET.Test.Sdk.
- Data: OSV `NuGet/all.zip` snapshot (2.5 MB; trimmed to id/aliases/affected/severity; date + sha256 + script);
  endoflife.date `dotnet.json` snapshot; SPDX copyleft list for the licence policy.
- git CLI. No other binaries (gitleaks was considered and rejected: a Go binary for ~30 regexes).
- Not used: `dotnet restore`, NuGet feeds, LLMs, network.

## 4. Dimension inventory (165). M = deterministic detector · NM = not measured (reason)

See the table in the reply / COVERAGE.md (kept in sync).

## 5. Milestones

1. Scoring library + `score`/`verify` + conformance vectors cross-checked against `cai verify`; SPEC-GAPS seeded.
2. Engine skeleton (repo/project model, offline Roslyn, git, evidence + SARIF, mapping, benchmark runner,
   determinism test) + Code Health core, secrets (D13/D28), Maturity history and docs.
3. Security & compliance: D29, D30/D43/D44 (snapshots), D31, D36, D37, D40–42, S1, C1–C5, D32, D14, D12, X14/15/17/24.
4. Architecture, Domain, Event-Driven, Event Sourcing.
5. Readiness and tests (D9–D11, P1–P12, R4, PF1–PF3, X8).
6. Accessibility (AC1–AC7).
7. Full benchmark run (15 C# units at registered tags) → RESULTS.md; COVERAGE.md, SPEC-GAPS.md, README final.

## 6. Status (2026-10-08)

All milestones delivered and committed locally (no remote yet): fold + 80 oracle vectors, engine over 128 of 165
dimensions (COVERAGE.md), benchmark run over all 15 C# units (RESULTS.md: 95 % recall, 100 % trap resistance, 4 % noise),
`cai verify` Δ 0.00 on every bundle, scans of stellae, Oqtane and Blazored Modal, 107 tests, 26 specification gaps.
