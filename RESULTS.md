# Benchmark results

Run of this engine over every C# unit of the scanner benchmark (`code-assurance-initiative/training-set-2026`, each unit
materialised at its latest registered tag) and scored with the harness (`cai_bench` 1.3.1, contract 1.4, `lineTolerance` 3)
through the generated mapping `benchmark/cai-reference.json`. Reproduce with:

```bash
benchmark/run-benchmark.sh <units-dir> <scanner-benchmark-clone> <out-dir>   # scans + scores, writes summary.tsv
benchmark/results-md.py <out-dir> RESULTS.md                                   # refreshes the tables below
```

Definitions (docs/CONTRACT.md): **recall** = planted defects found ÷ planted; **trap resistance** = traps left alone ÷ traps;
**noise** = findings on traps, on certified-clean code, on not-applicable concepts, or matching nothing the key expects ÷
all findings of the concepts the key covers. Score bands count only where the engine produced a score for the concept's
dimension; a band for an unmeasured dimension is "unscored", not a miss. The TypeScript units are not scanned (this engine
measures C#/.NET only).

## Reading the numbers

- **Trap resistance is 100 % on every unit**: no planted look-alike is reported.
- **Every miss on the bench units is one of two kinds**: (a) a concept the catalog evaluates with a language model
  (`adr-conformance`, `documentation-accuracy`, `primitive-obsession`, `non-idempotent-message-handler`,
  `personal-data-in-event-store`, `alt-text-quality`, `link-and-button-text-quality`, `heading-and-label-text-quality`) —
  this engine makes no model calls, so those stay unmeasured by design; or (b) a concept that needs registry data the engine
  does not have offline (`outdated-dependency`, `deprecated-dependency`) or a NuGet cache that the benchmark run did not
  provide (`license-policy-violation`, measured when `--nuget-packages` points at a cache).
- **Noise is almost entirely "the other side"**: both sides of a duplicated predicate, the second file of a change-coupled
  pair, the sibling file of a stale module, or a clone block whose start line sits more than three lines from the key's
  region although it covers it. These are reported deliberately (both halves of a pair are real locations) and are listed
  below so the choice is visible. The two `domain-event-never-handled` results on the domain-events unit are events raised
  and mapped nowhere; the key does not label them.
- **Estate units** are scored at integration level and carry hundreds of certified-clean files; on them the engine produces
  2–7 findings each, all but the clone-pair halves inside the key.

## Open-source and real-project scans

The same binary was run over three Blazor/ASP.NET Core repositories outside the benchmark to check that it runs, finishes
and stays quiet where it should (no answer key exists for these, so the numbers are descriptive):

| Repository | Projects | Production LOC | Time | Peak RSS | Findings | CAI |
|---|---|---|---|---|---|---|
| `stellae` (local, 89 projects, vertical slices, Marten, Blazor) | 89 | 45 450 | 126 s | 787 MB | 499 | 48.7 (Weak) |
| `oqtane/oqtane.framework` (Blazor CMS) | 14 | 41 962 | 97 s | 406 MB | 852 | 29.2 (Weak) |
| `Blazored/Modal` (component library + samples) | 4 | 555 | 3.5 s | 217 MB | 89 | 34.3 (Weak) |

Generalisation fixes these scans forced (none of them references a benchmark name, path or string): MSBuild `Condition`
attributes on inherited property groups are evaluated (a shared `Directory.Build.props` that scopes test defaults by project
name had made every project a test project), `<Compile Remove>` globs are honoured (uncompiled template sources under
`wwwroot`), a packable **web host** is not a library for the ConfigureAwait rule, label-like components (`<Label For>`)
count as labels, a slice reference must be a real project reference and not through the slice's Contracts project, mock-up
and documentation folders are not shipped markup, data-only contract files and persistence hubs are not namespace cycles,
one lock-file finding per repository rather than one per project.

Oracle cross-check: `cai verify` reproduces the headline of every bundle produced in these runs (15 benchmark units, 2
TypeScript estate units scanned for the fold only, 3 scans above) with Δ 0.00; `tools/oracle-compare.py` matches lens by lens.

## Tables (generated)

<!-- GENERATED:BEGIN -->
### Per unit

| Unit | Planted | Found | Recall | Traps | Trap resistance | Findings | Noise | Score bands in range |
|---|---|---|---|---|---|---|---|---|
| `bench-csharp-architecture` | 17 | 17 | 100 % | 22 | 100 % | 18 | 0 % | 7/7 (8 labelled) |
| `bench-csharp-baseline-clean` | 0 | 0 | – | 1 | 100 % | 0 | – | 31/31 (48 labelled) |
| `bench-csharp-blazor-a11y` | 25 | 22 | 88 % | 22 | 100 % | 22 | 0 % | 1/1 (4 labelled) |
| `bench-csharp-codehealth` | 50 | 50 | 100 % | 30 | 100 % | 54 | 7 % | 5/6 (8 labelled) |
| `bench-csharp-domain-events` | 30 | 27 | 90 % | 27 | 100 % | 31 | 6 % | 4/4 (8 labelled) |
| `bench-csharp-maturity-history` | 7 | 5 | 71 % | 10 | 100 % | 7 | 29 % | 8/8 (12 labelled) |
| `bench-csharp-readiness` | 12 | 12 | 100 % | 25 | 100 % | 13 | 0 % | 17/17 (18 labelled) |
| `bench-csharp-security-dependencies` | 8 | 5 | 62 % | 8 | 100 % | 27 | 0 % | 2/2 (2 labelled) |
| `bench-csharp-security-iac` | 19 | 19 | 100 % | 20 | 100 % | 22 | 0 % | 5/5 (7 labelled) |
| `bench-csharp-security-injection` | 20 | 20 | 100 % | 23 | 100 % | 22 | 0 % | 2/2 (3 labelled) |
| `bench-csharp-security-secrets` | 17 | 17 | 100 % | 24 | 100 % | 31 | 0 % | 1/1 (1 labelled) |
| `bench-csharp-tests` | 11 | 11 | 100 % | 13 | 100 % | 12 | 0 % | 5/5 (5 labelled) |
| `estate-quellbrook-dispatch` | 5 | 4 | 80 % | 15 | 100 % | 7 | 43 % | 30/30 (38 labelled) |
| `estate-quellbrook-notifier` | 2 | 2 | 100 % | 8 | 100 % | 2 | 0 % | 27/27 (36 labelled) |
| `estate-quellbrook-orders` | 2 | 2 | 100 % | 12 | 100 % | 2 | 0 % | 30/30 (38 labelled) |
| **all C# units** | 225 | 213 | 95 % | 260 | 100 % | 270 | 4 % | |

### Per dimension (all C# units)

| Dimension | Planted | Found | Recall | Traps | Resisted | Findings | Noise | Units |
|---|---|---|---|---|---|---|---|---|
| (no scanner rule) | 11 | 0 | 0 % | 14 | 100 % | 0 | – | blazor-a11y, domain-events, estate/dispatch, maturity-history, security-dependencies |
| AC1 | 3 | 3 | 100 % | 2 | 100 % | 3 | 0 % | blazor-a11y |
| AC2 | 3 | 3 | 100 % | 3 | 100 % | 3 | 0 % | blazor-a11y |
| AC3 | 2 | 2 | 100 % | 2 | 100 % | 2 | 0 % | blazor-a11y |
| AC4 | 4 | 4 | 100 % | 4 | 100 % | 4 | 0 % | blazor-a11y |
| AC5 | 1 | 1 | 100 % | 2 | 100 % | 1 | 0 % | blazor-a11y |
| AC6 | 5 | 5 | 100 % | 5 | 100 % | 5 | 0 % | blazor-a11y |
| AC7 | 1 | 1 | 100 % | 0 | – | 1 | 0 % | blazor-a11y |
| AX1 | 1 | 1 | 100 % | 2 | 100 % | 2 | 0 % | architecture |
| AX2 | 1 | 1 | 100 % | 2 | 100 % | 1 | 0 % | architecture |
| AX3 | 1 | 1 | 100 % | 1 | 100 % | 1 | 0 % | architecture |
| AX4 | 3 | 3 | 100 % | 6 | 100 % | 3 | 0 % | architecture |
| AX6 | 1 | 1 | 100 % | 1 | 100 % | 1 | 0 % | architecture |
| AX7 | 1 | 1 | 100 % | 1 | 100 % | 1 | 0 % | architecture |
| AX8 | 1 | 1 | 100 % | 1 | 100 % | 1 | 0 % | tests |
| AX9 | 1 | 1 | 100 % | 1 | 100 % | 1 | 0 % | architecture |
| AX10 | 1 | 1 | 100 % | 0 | – | 1 | 0 % | architecture |
| D1 | 2 | 2 | 100 % | 3 | 100 % | 2 | 0 % | codehealth, estate/dispatch |
| D2 | 2 | 2 | 100 % | 0 | – | 2 | 0 % | codehealth, estate/dispatch |
| D3 | 2 | 2 | 100 % | 2 | 100 % | 2 | 0 % | codehealth |
| D4 | 3 | 3 | 100 % | 2 | 100 % | 9 | 67 % | codehealth, estate/dispatch |
| D5 | 1 | 1 | 100 % | 2 | 100 % | 1 | 0 % | architecture |
| D6 | 1 | 1 | 100 % | 1 | 100 % | 1 | 0 % | codehealth |
| D7 | 2 | 2 | 100 % | 2 | 100 % | 2 | 0 % | architecture |
| D10 | 6 | 6 | 100 % | 6 | 100 % | 6 | 0 % | codehealth, tests |
| D11 | 3 | 3 | 100 % | 6 | 100 % | 3 | 0 % | tests |
| D13 | 18 | 18 | 100 % | 34 | 100 % | 33 | 0 % | security-iac, security-secrets |
| D14 | 1 | 0 | 0 % | 1 | 100 % | 0 | – | security-dependencies |
| D15 | 2 | 2 | 100 % | 3 | 100 % | 2 | 0 % | estate/dispatch, maturity-history |
| D16 | 1 | 1 | 100 % | 2 | 100 % | 1 | 0 % | maturity-history |
| D17 | 10 | 10 | 100 % | 12 | 100 % | 10 | 0 % | codehealth |
| D18 | 1 | 1 | 100 % | 4 | 100 % | 1 | 0 % | architecture |
| D23 | 2 | 2 | 100 % | 1 | 100 % | 2 | 0 % | domain-events |
| D26 | 1 | 1 | 100 % | 0 | – | 1 | 0 % | architecture |
| D27 | 1 | 1 | 100 % | 1 | 100 % | 1 | 0 % | architecture |
| D28 | 2 | 2 | 100 % | 0 | – | 2 | 0 % | estate/notifier, security-secrets |
| D29 | 19 | 19 | 100 % | 23 | 100 % | 20 | 0 % | blazor-a11y, readiness, security-iac, security-injection |
| D30 | 4 | 4 | 100 % | 2 | 100 % | 26 | 0 % | security-dependencies |
| D31 | 11 | 11 | 100 % | 15 | 100 % | 12 | 0 % | security-iac |
| D32 | 3 | 3 | 100 % | 3 | 100 % | 4 | 0 % | estate/notifier, security-injection |
| D34 | 1 | 1 | 100 % | 1 | 100 % | 2 | 50 % | maturity-history |
| D35 | 1 | 1 | 100 % | 2 | 100 % | 2 | 50 % | maturity-history |
| D36 | 5 | 5 | 100 % | 7 | 100 % | 6 | 0 % | security-iac |
| D39 | 1 | 1 | 100 % | 3 | 100 % | 1 | 0 % | codehealth |
| D44 | 1 | 1 | 100 % | 1 | 100 % | 1 | 0 % | security-dependencies |
| DM1 | 1 | 1 | 100 % | 2 | 100 % | 2 | 0 % | domain-events |
| DM2 | 1 | 1 | 100 % | 4 | 100 % | 1 | 0 % | domain-events |
| DM3 | 1 | 1 | 100 % | 2 | 100 % | 1 | 0 % | domain-events |
| DM4 | 1 | 1 | 100 % | 2 | 100 % | 1 | 0 % | domain-events |
| DM5 | 4 | 4 | 100 % | 3 | 100 % | 4 | 0 % | domain-events, estate/orders |
| DM6 | 3 | 3 | 100 % | 1 | 100 % | 3 | 0 % | architecture, domain-events |
| DM7 | 2 | 2 | 100 % | 1 | 100 % | 2 | 0 % | domain-events |
| DM9 | 1 | 1 | 100 % | 1 | 100 % | 2 | 50 % | domain-events, estate/dispatch |
| DM10 | 1 | 1 | 100 % | 1 | 100 % | 1 | 0 % | domain-events |
| DM11 | 1 | 1 | 100 % | 2 | 100 % | 1 | 0 % | domain-events |
| DM12 | 1 | 1 | 100 % | 1 | 100 % | 1 | 0 % | domain-events |
| ED1 | 1 | 1 | 100 % | 1 | 100 % | 1 | 0 % | domain-events |
| ED2 | 3 | 3 | 100 % | 2 | 100 % | 6 | 33 % | domain-events |
| ED3 | 1 | 1 | 100 % | 4 | 100 % | 1 | 0 % | domain-events |
| ED4 | 1 | 1 | 100 % | 3 | 100 % | 1 | 0 % | domain-events |
| ES1 | 2 | 2 | 100 % | 1 | 100 % | 2 | 0 % | domain-events |
| ES2 | 2 | 2 | 100 % | 2 | 100 % | 2 | 0 % | domain-events |
| GD1 | 1 | 1 | 100 % | 1 | 100 % | 1 | 0 % | codehealth |
| IC1 | 1 | 1 | 100 % | 1 | 100 % | 1 | 0 % | codehealth |
| P2 | 1 | 1 | 100 % | 2 | 100 % | 2 | 0 % | readiness |
| P6 | 1 | 1 | 100 % | 0 | – | 1 | 0 % | maturity-history |
| P7 | 1 | 1 | 100 % | 3 | 100 % | 1 | 0 % | readiness |
| P8 | 1 | 1 | 100 % | 2 | 100 % | 1 | 0 % | readiness |
| P12 | 2 | 2 | 100 % | 0 | – | 3 | 0 % | readiness, tests |
| R4 | 1 | 1 | 100 % | 0 | – | 1 | 0 % | tests |
| S1 | 3 | 3 | 100 % | 0 | – | 3 | 0 % | readiness |
| X1 | 3 | 3 | 100 % | 2 | 100 % | 3 | 0 % | codehealth |
| X2 | 4 | 4 | 100 % | 1 | 100 % | 4 | 0 % | codehealth, estate/orders, readiness |
| X3 | 4 | 4 | 100 % | 4 | 100 % | 4 | 0 % | codehealth, readiness |
| X4 | 2 | 2 | 100 % | 2 | 100 % | 2 | 0 % | codehealth, readiness |
| X5 | 3 | 3 | 100 % | 3 | 100 % | 3 | 0 % | codehealth |
| X6 | 1 | 1 | 100 % | 1 | 100 % | 1 | 0 % | codehealth |
| X7 | 1 | 1 | 100 % | 2 | 100 % | 1 | 0 % | codehealth |
| X8 | 2 | 2 | 100 % | 2 | 100 % | 2 | 0 % | blazor-a11y |
| X9 | 1 | 1 | 100 % | 0 | – | 1 | 0 % | codehealth |
| X13 | 1 | 1 | 100 % | 1 | 100 % | 1 | 0 % | codehealth |
| X15 | 1 | 1 | 100 % | 1 | 100 % | 1 | 0 % | security-injection |
| X16 | 1 | 1 | 100 % | 1 | 100 % | 1 | 0 % | codehealth |
| X17 | 1 | 1 | 100 % | 1 | 100 % | 1 | 0 % | security-injection |
| X18 | 1 | 1 | 100 % | 0 | – | 1 | 0 % | codehealth |
| X19 | 1 | 1 | 100 % | 1 | 100 % | 1 | 0 % | codehealth |
| X20 | 1 | 1 | 100 % | 0 | – | 1 | 0 % | codehealth |
| X21 | 1 | 1 | 100 % | 0 | – | 1 | 0 % | codehealth |
| X22 | 1 | 1 | 100 % | 0 | – | 1 | 0 % | codehealth |
| X23 | 1 | 1 | 100 % | 0 | – | 1 | 0 % | codehealth |
| X25 | 1 | 1 | 100 % | 0 | – | 1 | 0 % | codehealth |
| X26 | 1 | 1 | 100 % | 0 | – | 1 | 0 % | codehealth |
| X27 | 1 | 1 | 100 % | 1 | 100 % | 1 | 0 % | codehealth |
| X28 | 1 | 1 | 100 % | 1 | 100 % | 1 | 0 % | codehealth |
| X29 | 1 | 1 | 100 % | 0 | – | 1 | 0 % | codehealth |
| X30 | 1 | 1 | 100 % | 0 | – | 1 | 0 % | codehealth |
| X32 | 1 | 1 | 100 % | 0 | – | 1 | 0 % | codehealth |

### Every miss and every noise finding

| Unit | Entry | Concept | Where |
|---|---|---|---|
| `bench-csharp-blazor-a11y` | BA-023 | `alt-text-quality` | src/HarbourLane.Bookings.Web/Components/Pages/Home.razor |
| `bench-csharp-blazor-a11y` | BA-024 | `link-and-button-text-quality` | src/HarbourLane.Bookings.Web/Components/Pages/Home.razor |
| `bench-csharp-blazor-a11y` | BA-025 | `heading-and-label-text-quality` | src/HarbourLane.Bookings.Web/Pages/Admin/EditRoom.cshtml |
| `bench-csharp-codehealth` | unmatched | `duplicated-code` | src/Shipping.Rates.Core/Carriers/AlderParcelAdapter.cs:37 |
| `bench-csharp-codehealth` | unmatched | `duplicated-code` | src/Shipping.Rates.Core/Carriers/CorvidCourierAdapter.cs:33 |
| `bench-csharp-codehealth` | unmatched | `duplicated-code` | src/Shipping.Rates.Core/Carriers/CorvidCourierAdapter.cs:70 |
| `bench-csharp-codehealth` | unmatched | `duplicated-code` | src/Shipping.Rates.Tools/Printing/LabelPrinter.cs:19 |
| `bench-csharp-domain-events` | DM8-001 | `primitive-obsession` | src/Rentals.Lending.Domain/Members/Member.cs |
| `bench-csharp-domain-events` | ED5-001 | `non-idempotent-message-handler` | src/Rentals.Billing.Application/Handlers/EquipmentDamageReportedHandler.cs |
| `bench-csharp-domain-events` | ES3-001 | `personal-data-in-event-store` | src/Rentals.Billing.Domain/Accounts/Events/MemberAccountOpened.cs |
| `bench-csharp-domain-events` | unmatched | `domain-event-never-handled` | src/Rentals.Lending.Domain/Loans/ExtendLoanEvent.cs:5 |
| `bench-csharp-domain-events` | unmatched | `domain-event-never-handled` | src/Rentals.Lending.Domain/Members/MemberEvents.cs:9 |
| `bench-csharp-maturity-history` | ADR-001 | `adr-conformance` | docs/adr/0004-minimal-api-endpoints.md |
| `bench-csharp-maturity-history` | DOC-001 | `documentation-accuracy` | (repository) |
| `bench-csharp-maturity-history` | unmatched | `knowledge-freshness` | src/ClinicScheduling.Domain/Recurrence/RecurrenceRule.cs: |
| `bench-csharp-maturity-history` | unmatched | `change-coupling` | src/ClinicScheduling.Infrastructure/PatientPortal/PortalAppointmentFeed.cs: |
| `bench-csharp-security-dependencies` | DEP-005 | `deprecated-dependency` | Directory.Packages.props |
| `bench-csharp-security-dependencies` | DEP-006 | `outdated-dependency` | Directory.Packages.props |
| `bench-csharp-security-dependencies` | DEP-007 | `license-policy-violation` | Directory.Packages.props |
| `estate-quellbrook-dispatch` | DSP-004 | `adr-conformance` | docs/adr/0002-endpoints-call-application-handlers.md |
| `estate-quellbrook-dispatch` | unmatched | `scattered-domain-rule` | (repository): |
| `estate-quellbrook-dispatch` | unmatched | `duplicated-code` | src/Quellbrook.Dispatch.Domain/Assignment/ExpressAssignmentPolicy.cs:43 |
| `estate-quellbrook-dispatch` | unmatched | `duplicated-code` | src/Quellbrook.Dispatch.Domain/Assignment/StandardAssignmentPolicy.cs:35 |
<!-- GENERATED:END -->
