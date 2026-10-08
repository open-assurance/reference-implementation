# SPEC-GAPS — where the published text did not decide, and how this implementation resolved it

This file is a primary deliverable of the clean-room exercise. Each entry quotes the text that was available, states the
question it leaves open, records the resolution taken here, and says what the black-box oracle (`cai score` /
`cai verify` built from the published repository, used without reading its source) confirmed or contradicted.

Sources quoted: **SPEC** = codeassuranceindex.info/spec ("How the CAI is computed"); **DIM** = /dimensions;
**ART** = "How a CAI score is constructed" (founding explainer, 17 Sep 2026); **ADR-n** = docs/adr in the standard's
repository; **CAT** = `rubrics/rubric-2026.10.2/rubric-catalog.json`; **SCHEMA** = `schemas/cai-delivery-1.0.schema.json`
(`$defs/evidence`); **README** = the repository README; **BENCH** = scanner-benchmark `docs/CONTRACT.md`.

## A. The fold

### G-01 Advisory exclusion is decided by the bundle, not by the catalog
- **Quote.** SPEC: "Sixteen of the 165 are read by an AI model … Those readings are marked advisory, and the scorer leaves
  every advisory reading out of the number." CAT marks those sixteen `"evaluator": "llm"`. SCHEMA gives each dimension an
  optional boolean `advisory`.
- **Open.** Does the scorer exclude by the catalog's evaluator, by the bundle's flag, or either?
- **Resolution.** The bundle flag alone. The engine sets `advisory: true` on everything it would not want folded.
- **Oracle.** Confirmed: D19 (llm) sent without the flag folds into Maturity at 20 and critical-gates it; with the flag it
  vanishes. M4 (llm meta) likewise. See also G-02.

### G-02 Dimensions whose own description says "never scored" are scored unless flagged
- **Quote.** CAT, SC1: "Advisory; never scored." AXH1, AXI1, AXK1, AXO1, AXP1, AXS1, AXA1, AXR1, AXB1, AXB2: "Advisory;
  never scored" / "never affects the CAI score". All carry `"evaluator": "tool"` and no machine-readable advisory marker.
- **Open.** Is the prose binding on the fold?
- **Resolution.** No; the engine emits SC1 with `advisory: true` and emits none of the runtime AX*1 dimensions.
- **Oracle.** Contradicts the prose: SC1 and AXH1 sent at 2.0 without the flag fold into Security & Compliance (20,
  Critical, gated).

### G-03 Confidence 0 and coverage 0 mean different things
- **Quote.** ART: "coverage changes the effective value; confidence changes the weight of that value inside its
  category." CHALLENGE.md: "a dimension measured at confidence 0 is absent, never a raw 0."
- **Open.** Is coverage 0 also "absent"? Is a missing confidence 1 or 0?
- **Resolution.** confidence ≤ 0 or missing ⇒ absent (no fold, no gate). coverage 0 ⇒ effective value 0 ⇒ folds as a
  measured zero and critical-gates. The engine never emits coverage 0; a dimension with no surface is `notMeasured`.
- **Oracle.** Confirmed both (D3 at conf 0 disappears; D3 at coverage 0 folds to 0 and gates; a dimension with no
  `confidence` field gives "No measured lenses to score").

### G-04 The critical gate: which value, which comparison, which items
- **Quote.** SPEC: "A single measurement inside a lens scoring below 4 out of 10 holds that lens's band at Adequate …
  Advisory readings do not trigger it." ART: "a measured contributor whose effective value falls below the rubric's
  threshold". CAT: `criticalGate: 4`.
- **Open.** Raw or coverage-scaled value? Strict or inclusive? Meta-dimensions too? Does the gate look at the category?
- **Resolution.** Effective (coverage-scaled) value, strict `< 4`, dimensions and meta-dimensions alike, per
  contributor (a 3.0 dimension gates its lens even when its category averages to 60). Band is `min(band, Adequate)`.
- **Oracle.** Confirmed all four (3.9 gates, 4.0 does not; 7.0 × coverage 0.5 gates; meta 3.0 gates; advisory 3.0 does not).

### G-05 Architecture surface rule: AND or OR, zero or "fewer than"
- **Quote.** SPEC: "where there is no project to analyse it is left out rather than scored, and where there is only one
  small project, under 1,500 lines of production code, it can score no higher than 69." CAT: `architectureSurface:
  { minProjects: 2, minProductionLoc: 1500, lowSurfaceCap: 69 }`.
- **Open.** `minProjects: 2` reads as "fewer than 2 ⇒ left out", which contradicts the prose ("no project ⇒ left out").
  Is the cap conditioned on projects AND lines, or either?
- **Resolution.** Dropped iff `analyzableProjects == 0`; capped iff `analyzableProjects < minProjects` AND
  `productionLoc < minProductionLoc` (strict). The parameter name is misleading.
- **Oracle.** Confirmed: (1 project, 5000 loc) not capped; (1, 1499) capped; (1, 1500) not capped; (3, 500) not capped;
  (0, any) dropped. Missing `analyzableProjects` reads as 0 (dropped).

### G-06 Band coherence: "weakest category" means dimension categories, at baseline cutlines
- **Quote.** SPEC: "the headline band is never more than one band above the weakest category." ART: "never sits more
  than one band above the weakest measured category."
- **Open.** Are meta-dimensions (which bypass categories) categories for this purpose? Lenses? Do quality-bar cutlines apply?
- **Resolution.** Only the categories formed from `dimensions`, banded at the baseline cutlines; meta-dimensions and
  lenses do not participate. A bundle with no dimensions has no coherence cap.
- **Oracle.** Confirmed: a single meta at 1.0 (Critical lens) leaves a 57.3 headline "Adequate"; a category at 10
  (Critical) holds a 74.2 headline at "Weak"; the same under `qualityBar: prototype` behaves identically.

### G-07 Lens → quality-bar group is published nowhere
- **Quote.** SPEC: "A lens's cutlines move with the quality bar the codebase declares." CAT: `lensGroupFactors:
  { foundational: 0.4, operational: 1, safety: 0.25, default: 0.7 }` — but no lens names a group.
- **Open.** Which lens is in which group? Without that the per-lens band cannot be reproduced.
- **Resolution.** Derived by probing band flips at known offsets: codeHealth, architecture = foundational;
  securityCompliance = safety; maturity, productionReadiness = operational; the five conditional lenses = default. Hard-coded
  in `CaiScorer.LensGroup`; a catalog field (`lenses[].group`) would remove the gap.
- **Oracle.** Determined by: preview (−8) at 63 → maturity/readiness Strong (cutline 62), conditional lenses Adequate
  (64.4); mission-critical (+6) at 75/73/72 → flips at 76 (operational), 74.2 (default), 72.4 (foundational), 71.5 (safety).

### G-08 Quality-bar clamps and unknown bars
- **Quote.** CAT: `exemplaryCeiling: 98, poorFloor: 5`; SPEC says only "lower for a prototype, higher for a mission-critical service".
- **Open.** What do the ceiling and floor clamp? What does an unknown bar do?
- **Resolution.** The Exemplary cutline is `min(90 + shift, 98)`, the Weak/poor cutline `max(25 + shift, 5)`. With the
  published offsets (−18…+6) and factors (≤ 1) neither clamp can ever engage, so the reading is unobservable. An unknown
  bar is treated as production (no shift). The headline's cutlines never shift.
- **Oracle.** Unknown bar `bogus` ⇒ identical to production; headline unchanged under every bar. Clamps: not observable.

### G-09 Validation is asymmetric
- **Quote.** ADR-0004: "evidence that contradicts the frozen map is refused rather than scored under a map nobody can fetch."
- **Open.** Which contradictions are refused: category, lens, unknown id, duplicate id, a meta id in `dimensions`?
- **Resolution (mirrors the oracle).** Refused: a known dimension in a category other than the catalog's; a category the
  catalog does not know; score outside 0–10; coverage outside 0–1; unknown rubric version; empty bundle. Accepted: an
  unknown dimension id (routed by its category); a meta id inside `dimensions` with a category; a dimension id inside
  `metaDimensions`; a meta whose declared `lens` contradicts the catalog (the bundle's lens wins); duplicate ids (both
  averaged); confidence above 1 (used as a weight as given). The engine itself emits none of the accepted oddities.
- **Oracle.** Each case probed; see tests `Vectors/p3_*`, `p4_*`, `p5_*`, `p13_*`, `p18_*`, `q8b_*`, `q12_*`.

### G-10 Catalogs before rubric-2026.08.18 carry no category → lens map
- **Quote.** ADR-0004: "Catalogs published before `.18` carry no category and keep verifying on the bundle's own."
- **Open.** The bundle's category names a group, not a lens. Which lens does `security` or `docs` fold into under such a
  catalog? The sample bundle (rubric-2026.08.15) puts D8 in `code-quality` although every catalog lists D8 under
  Readiness, and puts D30 in `security`.
- **Resolution.** The category decides the lens, via the map later catalogs pin (code-quality, explicit-debt → codeHealth;
  architecture → architecture; git-mining, docs → maturity; testing, dependencies, security → productionReadiness;
  security-compliance → securityCompliance), used as a fallback when the named catalog has no categories.
- **Oracle.** Confirmed on the published sample: Code Health has 2 inputs (code-quality incl. D8, explicit-debt),
  Readiness 3 (testing, dependencies, security), Security & Compliance 1; headline 70.3.

### G-11 Thin lens-only bundles: whose weights?
- **Quote.** README: "the headline is a worst-first ordered-weighted average of the lens scores (Σ lensScore × owaWeight)
  … Because the weights are published in the evidence, anyone can reproduce or falsify a published number". SCHEMA:
  `lenses[] { lens, score, owaWeight }` "(or a thin `lenses` fallback)".
- **Open.** Are the bundle's `owaWeight`s inputs or outputs? What if they are not OWA-shaped?
- **Resolution.** On the lens-only path the bundle's weights are used as given when they sum to 1 (±0.01), even when
  they are not worst-first; otherwise worst-first weights are recomputed. Unknown lens keys are accepted. The
  architecture surface rules do not run on this path. When any dimension or meta is present, `lenses` is ignored.
- **Oracle.** Confirmed: weights (0.2, 0.8) on (60, 90) give 84.0; (0.5, 0.499) still used; (0.1, 0.1, 0.1) and (0, 0)
  recomputed; an unknown lens `bogus` is folded; architecture with `analyzableProjects: 0` survives on this path.

### G-12 Ties between equal lens scores
- **Quote.** SPEC: "They are ranked from weakest to strongest, and each step up the ranking carries 0.55 of the weight of
  the step below it."
- **Open.** Two lenses with equal scores receive different weights; the text does not say which gets the heavier one.
  It does not move the headline (equal scores × any permutation of the weights), but it changes the published per-lens weights.
- **Resolution.** Catalog lens order breaks ties (codeHealth first … performance last).
- **Oracle.** Confirmed with three lenses at 65.0 and with ten.

### G-13 Precision and rounding
- **Quote.** ENGINE-QUALIFICATION.md: "reproduces every dimension score, every lens score, the headline and the band
  exactly, at the precision the specification defines." The specification defines none; the delivery spec rounds
  "score 2 dp, weight 4 dp, contribution 2 dp" at build time; the CLI prints 1 dp; `verify` accepts ±0.5.
- **Resolution.** Bands are decided on the unrounded number (69.99 is Adequate, 70.0 Strong; 49.995 prints "50.0" and is
  Weak). Evidence `headlineScore` is written with 2 decimals (away-from-zero); `verify` uses ±0.5.
- **Oracle.** Confirmed the unrounded banding; the rounding mode at an exact tie is not observable.

### G-14 Reward polarity has no effect in the fold
- **Quote.** CAT: `"scoringPolarity": "reward"` on D40, D41, D42, PF1, PF2; PF1: "Presence is credited as a bonus, never a
  deduction."; PF2: "credited where present, never penalised where a simpler style is fine."
- **Open.** Nothing in the fold reads polarity; any emitted number enters the worst-first average, where a "neutral" 5/10
  is the heaviest-weighted deduction a lens can take.
- **Resolution.** The engine emits a reward dimension only when there is something to credit, and omits it (listed under
  `notMeasured` with the reason) otherwise.
- **Oracle.** Not applicable (a bundle field, not a fold rule).

## B. The evidence bundle

### G-15 "Not measured, with the reason" has no field
- **Quote.** DIM: "A dimension that could not be measured is recorded as not measured, with the reason, and takes no part
  in the score." SCHEMA `evidence.dimensions[]` is `additionalProperties: false` with no reason field; absence is the
  only representation.
- **Resolution.** The bundle carries a top-level `notMeasured: [{ id, reason }]` (SCHEMA `evidence` allows additional
  top-level properties) plus `engine: { name, version, snapshots }`.
- **Oracle.** Accepted and ignored (headline unchanged).

### G-16 Where a codebase "declares" its quality bar
- **Quote.** SPEC: "the quality bar the codebase declares". SCHEMA: `qualityBar` "(absent ⇒ production baseline)".
- **Open.** No file, attribute or convention is named.
- **Resolution.** `--quality-bar` on the command line; omitted otherwise.

### G-17 No applicability rule for the conditional lenses
- **Quote.** SPEC: "Domain Modelling, Event-Driven, Event Sourcing, Accessibility and Performance count only where the
  architecture calls for them."
- **Open.** What calls for them is undefined; the fold sees only presence or absence.
- **Resolution.** Heuristics in COVERAGE.md ("Lens applicability"); each is deterministic and listed in `notMeasured`
  reasons when a lens is left out.

### G-18 `ceilingRung` is unexplained
- **Quote.** CAT gives the 42 scored dimensions a `ceilingRung` of Documented / Verified / Prevented; AC7 mentions "the
  Documented→Verified→Prevented ladder". No text says what a rung does to a score.
- **Resolution.** Treated as descriptive; not used in scoring. Posture dimensions here use the ladder as their
  checklist shape where their own text describes one.

### G-19 `productionLoc` and `analyzableProjects` are undefined
- **Quote.** SPEC: "under 1,500 lines of production code"; SCHEMA: integers, no definition.
- **Resolution.** `productionLoc` = non-blank, non-comment lines of C# in non-test, non-generated files;
  `analyzableProjects` = production (non-test) C# projects whose sources were loaded.

## C. The benchmark contract (noted for completeness; no resolution needed by the fold)

### G-20 Score bands name concepts, dimensions name scores
- **Quote.** BENCH: "score-band: … expected score range on a 0–100 scale … The score is the concept's own, if supplied;
  else that of each of the concept's scoreDimensions".
- **Resolution.** `scores.json` publishes every measured dimension at `score × 10`, and the mapping names
  `scoreDimensions` per concept where attribution is broader than the measuring dimension.

## D. Gaps met while building the measurement side

These are not fold ambiguities; they are places where the specification or the catalog left a measuring engine to
choose, recorded so the choices are visible and can be challenged.

### G-21 Conditional lenses have no applicability rule (companion to G-17)
- **Where:** catalog lenses `domainModelling`, `eventDriven`, `eventSourcing`, `accessibility` carry no "applies when".
- **Resolution here:** domainModelling applies when DDD building blocks exist (aggregate/entity base types or repositories);
  eventDriven when commands, events or a bus exist; eventSourcing when an event store or fold methods exist; accessibility
  when shipped markup exists. Dimensions of an inapplicable lens are written to `notMeasured`, never scored 0. Recorded in
  the bundle as `lensApplicability` (an extension field, G-15). Oracle: a bundle without those dimensions folds without the
  lens; the headline reproduces.

### G-22 `evaluator: llm` dimensions and a deterministic engine
- **Where:** 16 dimensions are `evaluator: llm`; the benchmark plants defects for several of them (`primitive-obsession`,
  `non-idempotent-message-handler`, `personal-data-in-event-store`, the three text-quality concepts, `adr-conformance`,
  `documentation-accuracy`).
- **Resolution here:** not measured, with the reason "needs judgment". A heuristic score for a judged dimension would be an
  unmeasured score presented as a measured one. Recall on those plants is forfeited and listed in RESULTS.md.

### G-23 "IL Efficiency" (D39) without compilation
- **Where:** D39 `whatItMeasures` presumes compiled IL. This engine does not restore packages, so it never emits IL.
- **Resolution here:** a syntactic estimate (calls, member loads, literals, operators, allocations, interpolations per body),
  reported at confidence 0.7 and named as an estimate in the finding text. An alternative reading, "not measured", was
  rejected because the benchmark's clean baseline expects a score for the dimension (band 70–100).

### G-24 What counts as "documentation" and "shipped markup"
- **Where:** several dimensions read "documentation" (P5 restore procedures, AC7 accessibility statements) or "pages" (AC1–AC6)
  without saying where those live.
- **Resolution here:** documentation = README files, `docs/`, `doc/`, `runbooks/`, `ops/`, `wiki/`, decision folders; markup =
  `.razor/.cshtml/.html` outside vendored, build, mock-up, wireframe, prototype and documentation folders. A Markdown file in
  a fixture or a benchmark key is not the project's documentation. Without this, a unit's own answer-key README credited it
  with a restore procedure.

### G-25 Benchmark contract: pairs, regions and repository-level entries
- **Where:** CONTRACT 1.4 matches a result to an entry by concept, file and start line (±3), or by concept alone when the entry
  has no file.
- **Observed:** (a) a finding that covers the key's region but starts more than three lines earlier is counted as noise;
  (b) findings on the second half of a pair (clone, change coupling, stale module) are noise when the key labels one half;
  (c) a repository-level finding given a file stops matching a file-less entry, and a file-less finding never matches a clean
  file. This engine reports pairs on both sides (predicates, change coupling) or on the newer side (clone blocks) and keeps
  `scattered-domain-rule` repository-level; the resulting noise is itemised in RESULTS.md rather than tuned away.

### G-26 Scores file for score bands
- **Where:** the harness takes `--scores {concept-or-dimension: 0–100}`; the spec defines no such artefact.
- **Resolution here:** `scores.json` beside the evidence bundle, dimension id → effective score ×10, written from the same
  measurements the bundle carries (so a band is judged on what the fold used).
