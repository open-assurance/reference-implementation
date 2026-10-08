# Coverage — rubric-2026.10.2

The catalog has **165 dimensions**. This engine measures **128** of them deterministically and records the other **37** as not measured, each with a reason, in the evidence bundle's `notMeasured` list. An unmeasured dimension never carries a score: it is absent from `dimensions`, so the fold distributes its weight to what was measured (spec: coverage 0 ≠ score 0, see SPEC-GAPS.md G-03/G-15).

Reasons for not measuring fall into four groups:

- **16 LLM-evaluated dimensions** (`evaluator: llm` in the catalog): this engine makes no model calls and produces no score for them (D19–D22, D24, D25, M4, DM8, ED5, ES3, LA1–LA6).
- **11 runtime dimensions** (AX?1 family, D8): they observe a booted application or an executed test suite; this engine never starts anything.
- **9 JavaScript/TypeScript dimensions** (R1, R2, R5–R11): outside the C#/.NET target; the C# counterparts are D1/D2, D4, D17, AX3.
- **1 Erlang-specific dimension** (X31).

Everything measured is static: Roslyn syntax and (restore-free) semantics, git history, manifests and configuration, markup via AngleSharp, and the offline data snapshots under `data/`. Scoring shapes: findings fold per file as 1−0.5^Σweights and per repository as 10·exp(−k·Σ/kLOC); checklists score passed/total; shares score linearly; reward dimensions are emitted only when there is something to credit.

## Lens applicability

| Lens | Applies when | Otherwise |
|---|---|---|
| codeHealth, maturity, productionReadiness, securityCompliance, performance | always (production C# source present) | dimensions skipped individually with a reason |
| architecture | ≥1 analysable project | dropped by the fold; capped at 69 while <2 projects and <1 500 production LOC (spec rule) |
| domainModelling | DDD building blocks exist (aggregate/entity base types or repositories) | DM1–DM12 "not measured: no DDD building blocks" |
| eventDriven | commands, domain or integration events, or a message bus exist | ED1–ED4 not measured |
| eventSourcing | an event store / fold (Apply/When) exists | ES1–ES2 not measured |
| accessibility | .razor/.cshtml/.html markup exists outside vendored, build, mock-up and documentation folders | AC1–AC7 "lens not applicable" |

A dimension that is skipped is written to `notMeasured` with its reason and never carries a score (G-15 in SPEC-GAPS.md).

## Dimensions by lens

### codeHealth (36 of 41 measured)

| Dimension | Name | Evaluator | Status | Method / reason | Concepts (SARIF rule ids) |
|---|---|---|---|---|---|
| D1 | Cyclomatic Complexity | tool | measured | Roslyn: cyclomatic complexity per method body (branches, loops, boolean operators, `??`; lookup-table switches count once); finding above 15; score 10·exp(−Σ/kLOC). | `high-cyclomatic-complexity` |
| D2 | Cognitive Complexity | tool | measured | Roslyn: Sonar-style cognitive complexity (nesting-weighted branches, operator-sequence changes, recursion); finding above 15. | `high-cognitive-complexity` |
| D3 | God Classes | tool | measured | Roslyn: god classes by member count, LOC, fan-out and distinct responsibilities per type. | `god-class`, `long-method` |
| D4 | Code Duplication | tool | measured | Token-window clone detection over production syntax tokens (≥70 tokens, ≥7 lines, data-only shapes excluded); the copy side is reported when git dates the files apart; score 10·(1−5·cloned share). | `duplicated-code` |
| D17 | Explicit Debt | tool | measured | Technical-debt markers, suppressed diagnostics, commented-out code, obsolete symbols still used, unreachable code. | `technical-debt-marker`, `suppressed-diagnostic`, `commented-out-code`, `unused-code`, `unreachable-code`, `obsolete-symbol-still-used` |
| D18 | Solution Shape | tool | measured | Solution shape: project count, orphan projects, solution/folder mismatches, oversized files (R3). | `solution-structure` |
| D39 | IL Efficiency | tool | measured | Syntactic estimate of compiled IL per method body (calls, member loads, literals, operators, allocations, interpolations); finding above ~700 estimated instructions; confidence 0.7 because packages are not restored and nothing is compiled to IL. | `compiled-code-size` |
| GD1 | Unfinished & placeholder code | tool | measured | NotImplementedException, placeholder bodies, TODO-stubbed members in production code. | `not-implemented-placeholder` |
| IC1 | Incompleteness & stubs | tool | measured | Incomplete implementations: empty method bodies, `default` returns in otherwise meaningful members, unfinished switch coverage. | `incomplete-implementation` |
| R1 | Type Safety | tool | **not measured** | JavaScript/TypeScript surface: outside this engine's C#/.NET target |  |
| R2 | Cyclomatic Complexity | tool | **not measured** | JavaScript/TypeScript surface: outside this engine's C#/.NET target (C# complexity is D1/D2) |  |
| R3 | Large Files | tool | measured | Files over the size budget (production). | `oversized-source-file` |
| R7 | Dead Code | tool | **not measured** | JavaScript/TypeScript surface: outside this engine's C#/.NET target (C# dead code is D17) |  |
| R10 | Code Duplication | tool | **not measured** | JavaScript/TypeScript surface: outside this engine's C#/.NET target (C# duplication is D4) |  |
| X1 | Async correctness | tool | measured | async void outside handlers, blocking on tasks, missing ConfigureAwait in packable libraries (role Library, not Exe). | `blocking-on-async-code`, `async-void-method` |
| X2 | Cancellation propagation | tool | measured | CancellationToken accepted but not forwarded to awaited I/O (including EF query terminals), or awaited I/O with no token to pass on. | `missing-cancellation-propagation` |
| X3 | Exception handling | tool | measured | Empty catch blocks, pointless catch-rethrow, `throw ex` resetting the stack trace. | `empty-catch-block`, `pointless-catch-rethrow`, `rethrow-resets-stack-trace` |
| X4 | Structured logging | tool | measured | Non-structured log messages (interpolated/concatenated) versus message templates. | `non-structured-log-message` |
| X5 | Nullable reference types | tool | measured | Nullable disabled, null-forgiving operators, null dereference shapes. | `nullable-analysis-disabled`, `null-forgiving-suppression`, `null-dereference` |
| X6 | Hand-rolled structured-format parsing | tool | measured | Hand-rolled parsing of structured formats (JSON/XML/CSV/URL) by string ops where a parser exists. | `hand-rolled-structured-format-parsing` |
| X7 | Silent fallback defaults | tool | measured | Silent fallback defaults swallowing failures (catch → default value, TryParse ignored). | `silent-error-fallback` |
| X8 | JS interop contract | tool | measured | JS interop: `IJSRuntime.InvokeAsync` identifiers against functions defined in the repository's JavaScript, argument counts. | `js-interop-contract-mismatch` |
| X9 | Subsumed condition operand | tool | measured | Boolean operands subsumed by a neighbouring operand (redundant condition operands). | `redundant-condition-operand` |
| X10 | Duplicated predicate | tool | measured | Identical non-trivial predicates written out in several files (reported on every side). |  |
| X12 | Unreachable branch | tool | measured | Branches made unreachable by preceding conditions or constant guards. |  |
| X13 | Undrained process stream | tool | measured | Process stdout/stderr redirected and never drained before WaitForExit. | `undrained-child-process-stream` |
| X16 | Unfloored truncation loop | tool | measured | Truncation loops without a floor (can run to zero or negative). | `unbounded-truncation-loop` |
| X18 | Disposal-pattern correctness | tool | measured | Disposal-pattern correctness (IDisposable without using/finalizer discipline, double dispose). | `improper-resource-disposal` |
| X19 | Unrestored process-global state | tool | measured | Process-global state (culture, current directory, environment) changed and not restored. | `unrestored-process-global-state` |
| X20 | Mistyped argument guard | tool | measured | Argument guards testing the wrong parameter or condition. | `argument-guard-tests-wrong-condition` |
| X21 | Side-effecting pattern guard | tool | measured | Side effects inside pattern-matching guards. | `side-effect-in-conditional-guard` |
| X22 | Contradicted release guard | tool | measured | Release/lock guards contradicted by the state they check. | `lock-release-state-mismatch` |
| X23 | Unguarded diagnostic materialisation | tool | measured | Expensive diagnostic materialisation (serialisation, string building) outside an IsEnabled guard. | `unguarded-expensive-debug-logging` |
| X25 | Inert configuration knob | tool | measured | Configuration knobs read and never used (inert options). | `inert-configuration-option` |
| X26 | Unsynchronised callback handoff | tool | measured | Callbacks handed across threads without synchronisation. | `unsynchronized-callback-handoff` |
| X27 | Collection changed while being enumerated | tool | measured | Collections modified while being enumerated. | `collection-modified-during-enumeration` |
| X28 | Index access outside its own emptiness guard | tool | measured | Index access outside the emptiness guard that protects it. | `index-access-outside-bounds-guard` |
| X29 | Per-element action decided by a fixed element | tool | measured | Per-element actions decided by a fixed element (loop decision on index 0 / first). | `loop-decision-on-fixed-element` |
| X30 | Support guard that admits what it rejects | tool | measured | Support guards that admit what they claim to reject (inverted conditions). | `contradictory-support-guard` |
| X31 | Test-only surface in a production module | tool | **not measured** | Erlang-specific (export lists); not applicable to C# |  |
| X32 | Type resolved by simple name across every loaded assembly | tool | measured | Types resolved by simple name across all loaded assemblies (Type.GetType by name, assembly scans). | `type-lookup-by-simple-name` |

### architecture (17 of 20 measured)

| Dimension | Name | Evaluator | Status | Method / reason | Concepts (SARIF rule ids) |
|---|---|---|---|---|---|
| D5 | Coupling | tool | measured | Project-reference graph: instability/abstractness distance from the main sequence and stable-dependency violations; project cycles (AX3) feed in. | `unstable-dependency`, `module-off-main-sequence` |
| D6 | Cohesion (LCOM4) | tool | measured | LCOM4 over fields and methods per class (connected components of member usage). | `low-class-cohesion` |
| D7 | Architectural Integrity | tool | measured | Architecture rule documents (ADRs, architecture.md) with checkable dependency/type rules, versus architecture tests/analyzers in the repository and layer violations the project graph shows. | `architecture-rules-unenforced` |
| D22 | Internal API Consistency | llm | **not measured** | needs judgment: the catalog evaluates this dimension with a language model; this engine makes no model calls |  |
| D23 | Boundary Type-Coupling | tool | measured | Types from infrastructure packages (EF, HTTP, broker clients) appearing in public signatures of domain/application projects. | `boundary-type-leakage` |
| D26 | Project Cohesion | tool | measured | Project cohesion: types per project that reference nothing else in it; split/merge signals. | `oversized-module` |
| D27 | Navigability | tool | measured | Navigability: folder depth, files per folder, namespace↔folder agreement. | `call-indirection` |
| D35 | Change Coupling | tool | measured | git log: file pairs that change together far more than chance, excluding explicit dependencies and test↔subject pairs. | `change-coupling` |
| AX1 | Captive dependencies | tool | measured | DI registrations: scoped/transient services captured by singletons. | `captive-dependency` |
| AX2 | Stateful singletons | tool | measured | Singletons with mutable instance state and no synchronisation. | `unsynchronized-shared-state` |
| AX3 | Project dependency cycles | tool | measured | Project-reference cycles (impossible when the solution builds) and namespace cycles within a project, excluding persistence hubs and data-only contract files. | `module-dependency-cycle` |
| AX4 | Dependency direction | tool | measured | Dependency direction between layers (domain → application → infrastructure/web) from project references and usings. | `layer-dependency-violation` |
| AX5 | Architecture & structure | tool | measured | Structure: layer violations, boundary leaks and shape findings aggregated per project. | `architecture-style-fit` |
| AX6 | Interface segregation | tool | measured | Fat interfaces (≥12 members, or ≥6 with not-supported holes in implementers). | `fat-interface` |
| AX7 | Slice cohesion | tool | measured | Vertical slices (Features/Slices/Modules/<slice>/): references into another slice that are real project references and not through its Contracts/Abstractions surface; skipped without ≥2 slices. | `cross-slice-coupling` |
| AX8 | Test isolation | tool | measured | Production projects referencing test projects or test packages. | `production-depends-on-test-code` |
| AX9 | CQS / query purity | tool | measured | Query handlers / Get methods that write (SaveChanges, Publish, Add) — command/query separation. | `query-with-side-effects` |
| AX10 | Code composition | tool | measured | Code composition: share of generated, test, production and markup lines (descriptive; feeds facts). | `business-logic-share`, `business-logic-in-controller` |
| R9 | Circular Imports | tool | **not measured** | JavaScript/TypeScript surface: outside this engine's C#/.NET target (C# cycles are AX3) |  |
| R11 | Import Boundaries | tool | **not measured** | JavaScript/TypeScript surface: outside this engine's C#/.NET target |  |

### maturity (6 of 14 measured)

| Dimension | Name | Evaluator | Status | Method / reason | Concepts (SARIF rule ids) |
|---|---|---|---|---|---|
| D15 | Churn × Complexity Hotspots | tool | measured | git log: churn × current complexity per file, hotspots above the knee. | `churn-complexity-hotspot` |
| D16 | Bus Factor | tool | measured | git log: files owned by one still-active author across ≥4 changes over ≥60 days, weighted by size and fan-in; skipped when one author wrote ≥90 % of the history. | `knowledge-concentration` |
| D19 | Documentation Quality | llm | **not measured** | needs judgment: the catalog evaluates this dimension with a language model; this engine makes no model calls |  |
| D20 | ADR Quality | llm | **not measured** | needs judgment: the catalog evaluates this dimension with a language model; this engine makes no model calls |  |
| D21 | Naming Consistency | llm | **not measured** | needs judgment: the catalog evaluates this dimension with a language model; this engine makes no model calls |  |
| D24 | Comment Value | llm | **not measured** | needs judgment: the catalog evaluates this dimension with a language model; this engine makes no model calls |  |
| D25 | ADR Conformance | llm | **not measured** | needs judgment: the catalog evaluates this dimension with a language model; this engine makes no model calls |  |
| D34 | Knowledge Freshness | tool | measured | git log: files untouched for long and whose every author has been silent (orphaned knowledge), weighted by size; skipped under 180 days of history. | `knowledge-freshness` |
| AXB1 | Runtime evidence locked — no reproducible boot | tool | **not measured** | needs runtime: observed on a booted application, which this engine never starts |  |
| AXB2 | Runtime readiness | tool | **not measured** | needs runtime: observed on a booted application, which this engine never starts |  |
| M1 | Documentation (README) | tool | measured | README presence and section checklist (purpose, build/run, test, configuration, licence). | `readme-quality` |
| M2 | Architecture documentation | tool | measured | Architecture documentation: ADR folder with accepted decisions, architecture overview, diagrams. | `architecture-documentation` |
| M3 | Folder & project structure | tool | measured | Folder and project structure: src/tests/docs conventions, naming consistency between projects and folders. | `folder-structure` |
| M4 | Documentation accuracy | llm | **not measured** | needs judgment: the catalog evaluates this dimension with a language model; this engine makes no model calls |  |

### productionReadiness (19 of 24 measured)

| Dimension | Name | Evaluator | Status | Method / reason | Concepts (SARIF rule ids) |
|---|---|---|---|---|---|
| D8 | Code Coverage | tool | **not measured** | needs runtime: line coverage requires executing the test suite; static test reachability is reported as R4 |  |
| D9 | Test Distribution | tool | measured | Test methods classified unit/integration/e2e/BDD by project role and fixtures; checklist of pyramid properties. | `test-pyramid-distribution` |
| D10 | Test Quality | tool | measured | Test bodies without assertions, skipped without reason, swallowed failures, mock-dominated classes. | `test-without-assertion`, `skipped-test-without-reason`, `excessive-mocking`, `test-failure-swallowed` |
| D11 | Test Reliability | tool | measured | Non-determinism patterns in tests (sleep-waits, wall clock in assertions, unseeded randomness reaching assertions, real network hosts, shared paths, unordered-collection order). | `flaky-test` |
| D12 | Dependency Hygiene | tool | measured | Dependency hygiene: pre-release references, floating/unpinned versions, lock files, central package management, EOL target frameworks (snapshot in data/). | `prerelease-dependency` |
| D13 | Secret Scanning | tool | measured | Secret patterns over the working tree (vendor formats with checksums where they exist, assignments, connection strings, PEM) with placeholder/template/test exclusions. | `hardcoded-credential`, `hardcoded-password`, `hardcoded-cryptographic-key`, `committed-private-key` |
| D14 | License Compliance | tool | measured | Licence of every resolved package against data/licence-policy.json, read from nuspecs in a local NuGet cache (`--nuget-packages`); skipped when no cache is given. | `license-policy-violation` |
| ED5 | Idempotency | llm | **not measured** | needs judgment: the catalog evaluates this dimension with a language model; this engine makes no model calls |  |
| P1 | CI/CD gates | tool | measured | CI workflows: build, test, lint/analyzers, gates on pull requests. | `ci-build-and-test-pipeline` |
| P2 | Observability | tool | measured | Observability: structured logging, OpenTelemetry/metrics/tracing, health checks, correlation. | `observability` |
| P3 | Security & performance tooling | tool | measured | Security and performance tooling in CI (CodeQL/analyzers, dependency scanning, benchmarks). | `security-tooling-in-ci` |
| P4 | Deployment & Rollback | tool | measured | Deployment and rollback: manifests/pipelines with health probes, resource limits, rolling strategy, pinned images; skipped when nothing is deployed. | `deployment-rollback-safety` |
| P5 | DR & Backup | tool | measured | Disaster recovery: backup job/snapshot policy (in IaC or documented for a managed service), restore procedure/RTO/RPO in documentation locations, persistence declared, deletion protection; skipped without persistent state. | `disaster-recovery-evidence` |
| P6 | Release Hygiene | tool | measured | Release hygiene: changelog, version stamping, release tags, Keep-a-Changelog shape, changelog covering released tags (double weight). | `release-hygiene` |
| P7 | Outbound HTTP resilience | tool | measured | Outbound HttpClient sites with resilience (Polly/standard resilience handler, timeouts, retries); skipped without outbound HTTP. | `outbound-http-resilience` |
| P8 | Schema migrations | tool | measured | Schema migrations present, applied by code or pipeline, reversible. | `versioned-schema-migrations` |
| P9 | Domain vs controller coverage | tool | measured | Static test reachability split by domain versus web/controller files. | `domain-vs-controller-coverage` |
| P10 | Library API & versioning | tool | measured | Library API and versioning: PublicAPI analyzers, SemVer, package metadata (packable libraries only). | `library-api-versioning` |
| P11 | BDD / executable specs | tool | measured | BDD/executable specifications: feature files, bound steps, Given/When/Then, run in CI. | `executable-specifications` |
| P12 | CI test-gate honesty | tool | measured | CI test-gate honesty: tests actually run, no `continue-on-error`/filters that hide failures, no skipped-test inflation. | `ci-test-gate-integrity` |
| R4 | Test Coverage | tool | measured | Static reachability of logic-bearing production files from test files through the type graph; infrastructure in projects hosted by integration tests counts as reached. | `test-coverage` |
| R5 | Dependency Freshness | tool | **not measured** | JavaScript/TypeScript surface: outside this engine's C#/.NET target (npm freshness) |  |
| R6 | Tooling | tool | **not measured** | JavaScript/TypeScript surface: outside this engine's C#/.NET target (package.json scripts) |  |
| R8 | Dependency Hygiene | tool | **not measured** | JavaScript/TypeScript surface: outside this engine's C#/.NET target (npm dependency truthfulness) |  |

### securityCompliance (23 of 33 measured)

| Dimension | Name | Evaluator | Status | Method / reason | Concepts (SARIF rule ids) |
|---|---|---|---|---|---|
| D28 | Secrets (history) | tool | measured | git history: secret patterns (D13 rules) in blobs added by earlier commits and no longer present (commitSha reported). | `secret-in-version-history` |
| D29 | Static Analysis (SAST) | tool | measured | Roslyn taint-style sinks: SQL/command/path/LDAP/XPath/code injection, SSRF, open redirect, XSS (including Razor MarkupString/Html.Raw), weak crypto/hashing/password hashing, insecure randomness, certificate validation, deserialisation, XXE, regex DoS, cleartext transmission, token validation, error exposure. | `sql-injection`, `command-injection`, `code-injection`, `path-traversal`, `xml-external-entity`, `insecure-deserialization`, `cross-site-scripting`, `regex-denial-of-service`, `ldap-injection`, `xpath-injection`, `server-side-request-forgery`, `open-redirect`, `weak-cryptographic-algorithm`, `weak-hash-algorithm`, `insufficient-password-hashing`, `insecure-randomness`, `cleartext-transmission`, `missing-authorization`, `error-information-exposure`, `improper-certificate-validation`, `token-signature-or-expiry-not-validated`, `log-injection` |
| D30 | Dependency Vulnerabilities | tool | measured | Resolved package versions against the OSV snapshot for NuGet in data/ (ranges and exact versions). | `vulnerable-dependency` |
| D31 | IaC & Container Security | tool | measured | Kubernetes manifests, Dockerfiles, docker-compose, Terraform: privileges, root, capabilities, filesystems, probes, limits, mutable images, host mounts, RBAC, service-account tokens, committed Secrets, Ingress without TLS. | `download-without-integrity-check`, `container-runs-as-root`, `privileged-container`, `host-namespace-sharing`, `host-path-mount`, `container-privilege-escalation-allowed`, `container-excess-capabilities`, `container-writable-root-filesystem`, `container-confinement-profile-unset`, `container-security-context-missing`, `container-missing-resource-limits`, `container-missing-resource-requests`, `mutable-image-reference`, `missing-health-probes`, `missing-image-healthcheck`, `automounted-service-account-token`, `overly-permissive-rbac`, `image-not-from-allowed-registry` |
| D32 | Data Compliance (PII/GDPR) | tool | measured | Personal-data members (name patterns) written to logs — including `[LoggerMessage]` parameters — URLs and query strings. | `sensitive-data-in-logs`, `sensitive-data-in-url` |
| D36 | Supply-chain Provenance & Signing | tool | measured | Supply chain: CI workflow injection, secrets in run blocks or process arguments, token permissions, unpinned actions, downloads without integrity, SBOM/signing/provenance presence. | `ci-workflow-injection`, `ci-secret-exposure`, `secret-in-process-arguments`, `ci-token-excessive-permissions`, `unpinned-ci-action`, `build-provenance-and-signing` |
| D37 | Vulnerability-disclosure Policy | tool | measured | SECURITY.md / disclosure policy presence and required sections (contact, scope, response time). | `vulnerability-disclosure-policy` |
| D40 | Network Egress Confinement | tool | measured | Reward: egress-restricting NetworkPolicy / CiliumNetworkPolicy per workload. | `network-egress-policy` |
| D41 | Kernel & Syscall Confinement | tool | measured | Reward: seccomp and AppArmor/SELinux profiles per workload. | `workload-syscall-confinement` |
| D42 | Runtime Threat Enforcement | tool | measured | Reward: runtime detection agent (0.5), admission policy / signed-image gate (0.3), Pod Security Admission restricted (0.2) or baseline (0.1). | `runtime-threat-detection-and-admission` |
| D43 | Malicious Dependencies | tool | measured | Resolved packages against the malicious-package entries of the OSV snapshot and typosquat shapes of well-known ids. | `malicious-dependency` |
| D44 | Platform End-of-Life | tool | measured | Target frameworks and base images against the end-of-life snapshot in data/. | `end-of-life-platform` |
| AXA1 | Runtime unauthenticated reachability | tool | **not measured** | needs runtime: observed on a booted application, which this engine never starts |  |
| AXH1 | Runtime security headers | tool | **not measured** | needs runtime: observed on a booted application, which this engine never starts |  |
| AXI1 | Runtime container hardening | tool | **not measured** | needs runtime: observed on a booted application, which this engine never starts |  |
| AXK1 | Runtime cookie security | tool | **not measured** | needs runtime: observed on a booted application, which this engine never starts |  |
| AXO1 | Runtime API surface | tool | **not measured** | needs runtime: observed on a booted application, which this engine never starts |  |
| AXP1 | Runtime third-party data flows | tool | **not measured** | needs runtime: observed on a booted application, which this engine never starts |  |
| AXS1 | Runtime exposed surface | tool | **not measured** | needs runtime: observed on a booted application, which this engine never starts |  |
| C1 | Data Protection | tool | measured | Checklist over production code: transport security, protection of stored sensitive values, secrets held outside the code; skipped when no personal data is modelled. | `data-encryption-controls` |
| C2 | Access Controls | tool | measured | Controllers and minimal-API endpoints without [Authorize]/RequireAuthorization or an in-body authorization check, versus a fallback policy; skipped when no authentication is configured. | `authorization-enforcement` |
| C3 | Audit Trail | tool | measured | Checklist: audit interceptor/table, who/when stamps (`*By`, `*Actor`) on changes. | `audit-trail` |
| C4 | Data Retention | tool | measured | Checklist: retention period/TTL and purge jobs that concern personal data (outbox/inbox/cache/log trimming does not count). | `data-retention-policy` |
| C5 | Data-Subject Rights | tool | measured | Checklist: erasure, export/portability, consent. | `data-subject-rights` |
| LA1 | Personal-Data Handling (GDPR) | llm | **not measured** | needs judgment: the catalog evaluates this dimension with a language model; this engine makes no model calls |  |
| LA3 | Vulnerability-Disclosure Policy | llm | **not measured** | needs judgment: the catalog evaluates this dimension with a language model; this engine makes no model calls |  |
| LA4 | Dev/Test/Prod Separation | llm | **not measured** | needs judgment: the catalog evaluates this dimension with a language model; this engine makes no model calls |  |
| S1 | Web-Security Posture | tool | measured | Web posture checklist: HTTPS/HSTS, security response headers (and branches skipping them), model validation (attributes, validators, Validate() methods), cookie flags, auth order, no crypto shortcuts. | `security-response-headers`, `https-enforcement`, `insecure-cookie-flags`, `inbound-input-validation` |
| SC1 | Supply-chain hygiene | tool | measured | Supply-chain hygiene: lock files, central package management, source mapping, floating versions. | `dependencies-not-locked` |
| X14 | Bypassable address classification | tool | measured | Address classification bypassable by alternative spellings (IP literals, host forms). |  |
| X15 | Unvalidated length from an untrusted reader | tool | measured | Lengths read from an untrusted reader used to allocate without a cap. | `unbounded-allocation-from-untrusted-length` |
| X17 | Uncapped recursion over a caller-supplied document | tool | measured | Recursion over caller-supplied documents without a depth cap. | `uncontrolled-recursion` |
| X24 | Document value interpolated into markup unescaped | tool | measured | Document values interpolated into markup/HTML/XML unescaped. |  |

### performance (3 of 3 measured)

| Dimension | Name | Evaluator | Status | Method / reason | Concepts (SARIF rule ids) |
|---|---|---|---|---|---|
| PF1 | Benchmark discipline | tool | measured | Reward: a benchmark project (BenchmarkDotNet) and benchmarks in CI. | `benchmark-discipline` |
| PF2 | Allocation hygiene | tool | measured | Reward: allocation-aware APIs (Span/Memory, ArrayPool, ValueTask, pooled buffers) in hot paths. | `allocation-awareness` |
| PF3 | Async & latency hygiene | tool | measured | Async and latency hygiene: sync-over-async, blocking waits, Task.Result, unbounded parallelism. | `missing-configure-await` |

### domainModelling (11 of 12 measured)

| Dimension | Name | Evaluator | Status | Method / reason | Concepts (SARIF rule ids) |
|---|---|---|---|---|---|
| DM1 | Aggregate boundaries | tool | measured | Aggregates holding other aggregate roots by object reference. | `cross-aggregate-object-reference` |
| DM2 | Strongly-typed ids | tool | measured | Raw primitive identifiers on entities whose domain has strongly-typed ids for the same concept (a Guid naming another service's concept is a reference). | `primitive-entity-identifier` |
| DM3 | Integration-event coupling | tool | measured | Integration events carrying domain types. | `integration-event-leaks-domain-type` |
| DM4 | Rich vs anemic model | tool | measured | Property bags without behaviour (no public methods, validating factories or computed properties) driven by services. | `anemic-domain-model` |
| DM5 | Encapsulated state | tool | measured | Public setters on entities (including factory-built, identity-bearing classes); value objects without value equality or with setters. | `publicly-mutable-entity-state`, `value-object-mutability` |
| DM6 | Domain ↔ infrastructure boundary | tool | measured | Domain projects taking DbContext/IDocumentSession/HttpClient directly. | `domain-depends-on-infrastructure` |
| DM7 | Repository granularity | tool | measured | Repositories for non-aggregate types. | `repository-for-non-aggregate` |
| DM8 | Value-object opportunities | llm | **not measured** | needs judgment: the catalog evaluates this dimension with a language model; this engine makes no model calls |  |
| DM9 | Scattered domain decisions | tool | measured | The same multi-member predicate over one entity's data decided in several classes outside it (repository-level finding). | `scattered-domain-rule` |
| DM10 | One transaction, one aggregate | tool | measured | Handlers saving more than one aggregate in one transaction. | `multi-aggregate-transaction` |
| DM11 | Constructible invalid state | tool | measured | Public constructors storing primitives unvalidated with no factory beside them. | `constructible-invalid-entity` |
| DM12 | Ambient inputs in the domain | tool | measured | Wall clock, Random and environment reads inside domain types. | `ambient-nondeterminism-in-domain` |

### eventDriven (4 of 4 measured)

| Dimension | Name | Evaluator | Status | Method / reason | Concepts (SARIF rule ids) |
|---|---|---|---|---|---|
| ED1 | Handler temporal coupling | tool | measured | Handlers whose correctness depends on another handler having run first (shared mutable state, ordering assumptions). | `synchronous-remote-call-in-event-handler` |
| ED2 | Event/command shape | tool | measured | Commands with several handlers; events raised that nothing handles, folds or maps. | `command-with-multiple-handlers`, `domain-event-never-handled` |
| ED3 | Event naming | tool | measured | Event type names without a past-participle word. | `event-not-named-in-past-tense` |
| ED4 | Outbox / dual-write | tool | measured | Publish after SaveChanges without an outbox (dual write). | `dual-write-without-outbox` |

### eventSourcing (2 of 3 measured)

| Dimension | Name | Evaluator | Status | Method / reason | Concepts (SARIF rule ids) |
|---|---|---|---|---|---|
| ES1 | Fold determinism | tool | measured | Fold methods (Apply/When) reading ambient inputs or I/O. | `nondeterministic-event-fold` |
| ES2 | Immutable events | tool | measured | Stored event types with setters or mutable collections. | `mutable-persisted-event`, `event-schema-change-without-upcaster` |
| ES3 | PII in the event store | llm | **not measured** | needs judgment: the catalog evaluates this dimension with a language model; this engine makes no model calls |  |

### accessibility (7 of 11 measured)

| Dimension | Name | Evaluator | Status | Method / reason | Concepts (SARIF rule ids) |
|---|---|---|---|---|---|
| AC1 | Text alternatives | tool | measured | AngleSharp over .razor/.cshtml/.html (Razor stripped): img/area/input[type=image] alt, meaningful svg titles, video captions, autoplay without controls, object/embed/canvas names. | `missing-text-alternative`, `autoplay-media-without-control` |
| AC2 | Forms & labels | tool | measured | Programmatic labels (label[for], asp-for, label-like components, wrapping labels, aria-label/labelledby), button and link names, fieldset legends, error messages not tied to their field. | `form-control-without-label`, `form-error-not-associated` |
| AC3 | Page structure | tool | measured | html lang (BCP-47), title, one main landmark, heading order and empty headings, zoom disabled, meta refresh, iframe titles, data tables without th. | `page-structure-violation` |
| AC4 | Keyboard semantics | tool | measured | Click handlers on non-interactive elements without role/tabindex/key handler, pointer-only cursors, positive tabindex, href="#" buttons, anchors without targets, hand-rolled modals without focus management. | `non-keyboard-accessible-interaction`, `modal-focus-not-managed` |
| AC5 | ARIA correctness | tool | measured | Role validity and abstract roles, required states per role, aria-* attribute names and token values, aria-hidden on focusable content. | `invalid-aria-usage` |
| AC6 | Visual & motion safety | tool | measured | Repository CSS and inline styles: outline removal without a same-selector replacement, animation without prefers-reduced-motion (per stylesheet, or a universal site guard), literal colour-pair contrast (4.5:1 / 3:1 large text). | `focus-outline-removed`, `motion-without-reduced-motion`, `visual-and-motion-safety` |
| AC7 | A11y enforcement | tool | measured | Enforcement ladder: a11y lint rules, accessibility assertions in component tests (axe/pa11y/Lighthouse), browser accessibility checks in CI, accessibility documentation in docs/README. | `accessibility-checks-in-ci` |
| AXR1 | Runtime accessibility (rendered) | tool | **not measured** | needs runtime: observed on a booted application, which this engine never starts |  |
| LA2 | Alt-Text Quality (WCAG 1.1.1) | llm | **not measured** | needs judgment: the catalog evaluates this dimension with a language model; this engine makes no model calls |  |
| LA5 | Link & Button Text Quality (WCAG 2.4.4) | llm | **not measured** | needs judgment: the catalog evaluates this dimension with a language model; this engine makes no model calls |  |
| LA6 | Heading & Label Text Quality (WCAG 2.4.6) | llm | **not measured** | needs judgment: the catalog evaluates this dimension with a language model; this engine makes no model calls |  |

## Concepts without a detector

The benchmark taxonomy concepts this engine does not emit, with the reason recorded in the mapping file's `unmapped` list (`benchmark/cai-reference.json`):

| Concept | Reason |
|---|---|
| `outdated-dependency` | needs registry data (latest versions); the engine runs offline |
| `deprecated-dependency` | needs registry data (deprecation metadata); the engine runs offline |
| `unused-dependency` | npm concept |
| `undeclared-dependency` | npm concept |
| `misplaced-dev-dependency` | npm concept |
| `floating-promise` | JavaScript concept |
| `prototype-pollution` | JavaScript concept |
| `unchecked-any-external-data` | TypeScript concept |
| `untyped-javascript-share` | JavaScript concept |
| `frontend-tooling-scripts` | npm concept |
| `react-index-as-key` | React concept |
| `react-hook-missing-dependency` | React concept |
| `react-state-mutation` | React concept |
| `sensitive-data-in-browser-storage` | browser-side JavaScript concept |
| `test-only-api-in-production-code` | Erlang concept |
| `missing-subresource-integrity` | not implemented |
| `sensitive-data-in-token-payload` | not implemented |
| `nosql-injection` | not implemented (no NoSQL sink model) |
| `mass-assignment` | not implemented |
| `dependency-release-cooldown-missing` | not implemented |
| `iac-misconfiguration` | umbrella: this engine emits the precise child concepts |
| `container-excessive-privilege` | umbrella: this engine emits the precise child concepts |
| `low-value-comments` | needs judgment: the catalog evaluates this dimension with a language model; this engine makes no model calls |
| `inconsistent-naming` | needs judgment: the catalog evaluates this dimension with a language model; this engine makes no model calls |
| `internal-api-inconsistency` | needs judgment: the catalog evaluates this dimension with a language model; this engine makes no model calls |
| `documentation-quality` | needs judgment: the catalog evaluates this dimension with a language model; this engine makes no model calls |
| `adr-quality` | needs judgment: the catalog evaluates this dimension with a language model; this engine makes no model calls |
| `adr-conformance` | needs judgment: the catalog evaluates this dimension with a language model; this engine makes no model calls |
| `documentation-accuracy` | needs judgment: the catalog evaluates this dimension with a language model; this engine makes no model calls |
| `primitive-obsession` | needs judgment: the catalog evaluates this dimension with a language model; this engine makes no model calls |
| `non-idempotent-message-handler` | needs judgment: the catalog evaluates this dimension with a language model; this engine makes no model calls |
| `personal-data-in-event-store` | needs judgment: the catalog evaluates this dimension with a language model; this engine makes no model calls |
| `personal-data-inventory` | needs judgment: the catalog evaluates this dimension with a language model; this engine makes no model calls |
| `alt-text-quality` | needs judgment: the catalog evaluates this dimension with a language model; this engine makes no model calls |
| `link-and-button-text-quality` | needs judgment: the catalog evaluates this dimension with a language model; this engine makes no model calls |
| `heading-and-label-text-quality` | needs judgment: the catalog evaluates this dimension with a language model; this engine makes no model calls |
| `vulnerability-disclosure-policy-quality` | needs judgment: the catalog evaluates this dimension with a language model; this engine makes no model calls |
| `environment-separation` | needs judgment: the catalog evaluates this dimension with a language model; this engine makes no model calls |
| `unauthenticated-reachable-endpoint` | needs runtime: observed on a booted application, which this engine never starts |
| `reproducible-boot` | needs runtime: observed on a booted application, which this engine never starts |
| `undocumented-api-endpoint` | needs runtime: observed on a booted application, which this engine never starts |
| `third-party-data-flow` | needs runtime: observed on a booted application, which this engine never starts |
| `unexpected-exposed-port` | needs runtime: observed on a booted application, which this engine never starts |
| `consent-not-checked` | not implemented |
