#!/usr/bin/env python3
"""Render COVERAGE.md: every dimension of the vendored catalog, measured or not, with the method or the reason.
    tools/coverage-md.py [rubric-version]   (writes COVERAGE.md in the repository root)
The method texts live in this file (METHOD); the not-measured reasons are read from src/Cai.Reference/Engine/Coverage.cs
so the document and the engine cannot drift apart silently; the concept→dimension mapping comes from Model.cs."""
import json, os, re, sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
VERSION = sys.argv[1] if len(sys.argv) > 1 else "rubric-2026.10.2"
catalog = json.load(open(os.path.join(ROOT, "rubrics", VERSION, "rubric-catalog.json")))
model = open(os.path.join(ROOT, "src/Cai.Reference/Engine/Model.cs")).read()
coverage = open(os.path.join(ROOT, "src/Cai.Reference/Engine/Coverage.cs")).read()
rules = {}
for concept, dim in re.findall(r'R\("([\w-]+)", "([A-Z]+\d+)"', model): rules.setdefault(dim, []).append(concept)
consts = dict(re.findall(r'public const string (\w+) = "([^"]*)";', coverage))
nm = {}
for dim, val in re.findall(r'\["([A-Z]+\d+)"\] = ((?:"[^"]*"|\w+)(?:\s*\+\s*"[^"]*")*)', coverage.split("StaticReasons")[1].split("UnmappedConceptReasons")[0]):
    text = ""
    for part in re.findall(r'"([^"]*)"|(\w+)', val): text += part[0] if part[0] else consts.get(part[1], part[1])
    nm[dim] = text

METHOD = {
 "D1": "Roslyn: cyclomatic complexity per method body (branches, loops, boolean operators, `??`; lookup-table switches count once); finding above 15; score 10·exp(−Σ/kLOC).",
 "D2": "Roslyn: Sonar-style cognitive complexity (nesting-weighted branches, operator-sequence changes, recursion); finding above 15.",
 "D3": "Roslyn: god classes by member count, LOC, fan-out and distinct responsibilities per type.",
 "D4": "Token-window clone detection over production syntax tokens (≥70 tokens, ≥7 lines, data-only shapes excluded); the copy side is reported when git dates the files apart; score 10·(1−5·cloned share).",
 "D5": "Project-reference graph: instability/abstractness distance from the main sequence and stable-dependency violations; project cycles (AX3) feed in.",
 "D6": "LCOM4 over fields and methods per class (connected components of member usage).",
 "D7": "Architecture rule documents (ADRs, architecture.md) with checkable dependency/type rules, versus architecture tests/analyzers in the repository and layer violations the project graph shows.",
 "D9": "Test methods classified unit/integration/e2e/BDD by project role and fixtures; checklist of pyramid properties.",
 "D10": "Test bodies without assertions, skipped without reason, swallowed failures, mock-dominated classes.",
 "D11": "Non-determinism patterns in tests (sleep-waits, wall clock in assertions, unseeded randomness reaching assertions, real network hosts, shared paths, unordered-collection order).",
 "D12": "Dependency hygiene: pre-release references, floating/unpinned versions, lock files, central package management, EOL target frameworks (snapshot in data/).",
 "D13": "Secret patterns over the working tree (vendor formats with checksums where they exist, assignments, connection strings, PEM) with placeholder/template/test exclusions.",
 "D14": "Licence of every resolved package against data/licence-policy.json, read from nuspecs in a local NuGet cache (`--nuget-packages`); skipped when no cache is given.",
 "D15": "git log: churn × current complexity per file, hotspots above the knee.",
 "D16": "git log: files owned by one still-active author across ≥4 changes over ≥60 days, weighted by size and fan-in; skipped when one author wrote ≥90 % of the history.",
 "D17": "Technical-debt markers, suppressed diagnostics, commented-out code, obsolete symbols still used, unreachable code.",
 "D18": "Solution shape: project count, orphan projects, solution/folder mismatches, oversized files (R3).",
 "D23": "Types from infrastructure packages (EF, HTTP, broker clients) appearing in public signatures of domain/application projects.",
 "D26": "Project cohesion: types per project that reference nothing else in it; split/merge signals.",
 "D27": "Navigability: folder depth, files per folder, namespace↔folder agreement.",
 "D28": "git history: secret patterns (D13 rules) in blobs added by earlier commits and no longer present (commitSha reported).",
 "D29": "Roslyn taint-style sinks: SQL/command/path/LDAP/XPath/code injection, SSRF, open redirect, XSS (including Razor MarkupString/Html.Raw), weak crypto/hashing/password hashing, insecure randomness, certificate validation, deserialisation, XXE, regex DoS, cleartext transmission, token validation, error exposure.",
 "D30": "Resolved package versions against the OSV snapshot for NuGet in data/ (ranges and exact versions).",
 "D31": "Kubernetes manifests, Dockerfiles, docker-compose, Terraform: privileges, root, capabilities, filesystems, probes, limits, mutable images, host mounts, RBAC, service-account tokens, committed Secrets, Ingress without TLS.",
 "D32": "Personal-data members (name patterns) written to logs — including `[LoggerMessage]` parameters — URLs and query strings.",
 "D34": "git log: files untouched for long and whose every author has been silent (orphaned knowledge), weighted by size; skipped under 180 days of history.",
 "D35": "git log: file pairs that change together far more than chance, excluding explicit dependencies and test↔subject pairs.",
 "D36": "Supply chain: CI workflow injection, secrets in run blocks or process arguments, token permissions, unpinned actions, downloads without integrity, SBOM/signing/provenance presence.",
 "D37": "SECURITY.md / disclosure policy presence and required sections (contact, scope, response time).",
 "D39": "Syntactic estimate of compiled IL per method body (calls, member loads, literals, operators, allocations, interpolations); finding above ~700 estimated instructions; confidence 0.7 because packages are not restored and nothing is compiled to IL.",
 "D40": "Reward: egress-restricting NetworkPolicy / CiliumNetworkPolicy per workload.",
 "D41": "Reward: seccomp and AppArmor/SELinux profiles per workload.",
 "D42": "Reward: runtime detection agent (0.5), admission policy / signed-image gate (0.3), Pod Security Admission restricted (0.2) or baseline (0.1).",
 "D43": "Resolved packages against the malicious-package entries of the OSV snapshot and typosquat shapes of well-known ids.",
 "D44": "Target frameworks and base images against the end-of-life snapshot in data/.",
 "AC1": "AngleSharp over .razor/.cshtml/.html (Razor stripped): img/area/input[type=image] alt, meaningful svg titles, video captions, autoplay without controls, object/embed/canvas names.",
 "AC2": "Programmatic labels (label[for], asp-for, label-like components, wrapping labels, aria-label/labelledby), button and link names, fieldset legends, error messages not tied to their field.",
 "AC3": "html lang (BCP-47), title, one main landmark, heading order and empty headings, zoom disabled, meta refresh, iframe titles, data tables without th.",
 "AC4": "Click handlers on non-interactive elements without role/tabindex/key handler, pointer-only cursors, positive tabindex, href=\"#\" buttons, anchors without targets, hand-rolled modals without focus management.",
 "AC5": "Role validity and abstract roles, required states per role, aria-* attribute names and token values, aria-hidden on focusable content.",
 "AC6": "Repository CSS and inline styles: outline removal without a same-selector replacement, animation without prefers-reduced-motion (per stylesheet, or a universal site guard), literal colour-pair contrast (4.5:1 / 3:1 large text).",
 "AC7": "Enforcement ladder: a11y lint rules, accessibility assertions in component tests (axe/pa11y/Lighthouse), browser accessibility checks in CI, accessibility documentation in docs/README.",
 "AX1": "DI registrations: scoped/transient services captured by singletons.",
 "AX2": "Singletons with mutable instance state and no synchronisation.",
 "AX3": "Project-reference cycles (impossible when the solution builds) and namespace cycles within a project, excluding persistence hubs and data-only contract files.",
 "AX4": "Dependency direction between layers (domain → application → infrastructure/web) from project references and usings.",
 "AX5": "Structure: layer violations, boundary leaks and shape findings aggregated per project.",
 "AX6": "Fat interfaces (≥12 members, or ≥6 with not-supported holes in implementers).",
 "AX7": "Vertical slices (Features/Slices/Modules/<slice>/): references into another slice that are real project references and not through its Contracts/Abstractions surface; skipped without ≥2 slices.",
 "AX8": "Production projects referencing test projects or test packages.",
 "AX9": "Query handlers / Get methods that write (SaveChanges, Publish, Add) — command/query separation.",
 "AX10": "Code composition: share of generated, test, production and markup lines (descriptive; feeds facts).",
 "C1": "Checklist over production code: transport security, protection of stored sensitive values, secrets held outside the code; skipped when no personal data is modelled.",
 "C2": "Controllers and minimal-API endpoints without [Authorize]/RequireAuthorization or an in-body authorization check, versus a fallback policy; skipped when no authentication is configured.",
 "C3": "Checklist: audit interceptor/table, who/when stamps (`*By`, `*Actor`) on changes.",
 "C4": "Checklist: retention period/TTL and purge jobs that concern personal data (outbox/inbox/cache/log trimming does not count).",
 "C5": "Checklist: erasure, export/portability, consent.",
 "DM1": "Aggregates holding other aggregate roots by object reference.",
 "DM2": "Raw primitive identifiers on entities whose domain has strongly-typed ids for the same concept (a Guid naming another service's concept is a reference).",
 "DM3": "Integration events carrying domain types.",
 "DM4": "Property bags without behaviour (no public methods, validating factories or computed properties) driven by services.",
 "DM5": "Public setters on entities (including factory-built, identity-bearing classes); value objects without value equality or with setters.",
 "DM6": "Domain projects taking DbContext/IDocumentSession/HttpClient directly.",
 "DM7": "Repositories for non-aggregate types.",
 "DM9": "The same multi-member predicate over one entity's data decided in several classes outside it (repository-level finding).",
 "DM10": "Handlers saving more than one aggregate in one transaction.",
 "DM11": "Public constructors storing primitives unvalidated with no factory beside them.",
 "DM12": "Wall clock, Random and environment reads inside domain types.",
 "ED1": "Handlers whose correctness depends on another handler having run first (shared mutable state, ordering assumptions).",
 "ED2": "Commands with several handlers; events raised that nothing handles, folds or maps.",
 "ED3": "Event type names without a past-participle word.",
 "ED4": "Publish after SaveChanges without an outbox (dual write).",
 "ES1": "Fold methods (Apply/When) reading ambient inputs or I/O.",
 "ES2": "Stored event types with setters or mutable collections.",
 "GD1": "NotImplementedException, placeholder bodies, TODO-stubbed members in production code.",
 "IC1": "Incomplete implementations: empty method bodies, `default` returns in otherwise meaningful members, unfinished switch coverage.",
 "M1": "README presence and section checklist (purpose, build/run, test, configuration, licence).",
 "M2": "Architecture documentation: ADR folder with accepted decisions, architecture overview, diagrams.",
 "M3": "Folder and project structure: src/tests/docs conventions, naming consistency between projects and folders.",
 "P1": "CI workflows: build, test, lint/analyzers, gates on pull requests.",
 "P2": "Observability: structured logging, OpenTelemetry/metrics/tracing, health checks, correlation.",
 "P3": "Security and performance tooling in CI (CodeQL/analyzers, dependency scanning, benchmarks).",
 "P4": "Deployment and rollback: manifests/pipelines with health probes, resource limits, rolling strategy, pinned images; skipped when nothing is deployed.",
 "P5": "Disaster recovery: backup job/snapshot policy (in IaC or documented for a managed service), restore procedure/RTO/RPO in documentation locations, persistence declared, deletion protection; skipped without persistent state.",
 "P6": "Release hygiene: changelog, version stamping, release tags, Keep-a-Changelog shape, changelog covering released tags (double weight).",
 "P7": "Outbound HttpClient sites with resilience (Polly/standard resilience handler, timeouts, retries); skipped without outbound HTTP.",
 "P8": "Schema migrations present, applied by code or pipeline, reversible.",
 "P9": "Static test reachability split by domain versus web/controller files.",
 "P10": "Library API and versioning: PublicAPI analyzers, SemVer, package metadata (packable libraries only).",
 "P11": "BDD/executable specifications: feature files, bound steps, Given/When/Then, run in CI.",
 "P12": "CI test-gate honesty: tests actually run, no `continue-on-error`/filters that hide failures, no skipped-test inflation.",
 "PF1": "Reward: a benchmark project (BenchmarkDotNet) and benchmarks in CI.",
 "PF2": "Reward: allocation-aware APIs (Span/Memory, ArrayPool, ValueTask, pooled buffers) in hot paths.",
 "PF3": "Async and latency hygiene: sync-over-async, blocking waits, Task.Result, unbounded parallelism.",
 "R3": "Files over the size budget (production).",
 "R4": "Static reachability of logic-bearing production files from test files through the type graph; infrastructure in projects hosted by integration tests counts as reached.",
 "S1": "Web posture checklist: HTTPS/HSTS, security response headers (and branches skipping them), model validation (attributes, validators, Validate() methods), cookie flags, auth order, no crypto shortcuts.",
 "SC1": "Supply-chain hygiene: lock files, central package management, source mapping, floating versions.",
 "X1": "async void outside handlers, blocking on tasks, missing ConfigureAwait in packable libraries (role Library, not Exe).",
 "X2": "CancellationToken accepted but not forwarded to awaited I/O (including EF query terminals), or awaited I/O with no token to pass on.",
 "X3": "Empty catch blocks, pointless catch-rethrow, `throw ex` resetting the stack trace.",
 "X4": "Non-structured log messages (interpolated/concatenated) versus message templates.",
 "X5": "Nullable disabled, null-forgiving operators, null dereference shapes.",
 "X6": "Hand-rolled parsing of structured formats (JSON/XML/CSV/URL) by string ops where a parser exists.",
 "X7": "Silent fallback defaults swallowing failures (catch → default value, TryParse ignored).",
 "X8": "JS interop: `IJSRuntime.InvokeAsync` identifiers against functions defined in the repository's JavaScript, argument counts.",
 "X9": "Boolean operands subsumed by a neighbouring operand (redundant condition operands).",
 "X10": "Identical non-trivial predicates written out in several files (reported on every side).",
 "X12": "Branches made unreachable by preceding conditions or constant guards.",
 "X13": "Process stdout/stderr redirected and never drained before WaitForExit.",
 "X14": "Address classification bypassable by alternative spellings (IP literals, host forms).",
 "X15": "Lengths read from an untrusted reader used to allocate without a cap.",
 "X16": "Truncation loops without a floor (can run to zero or negative).",
 "X17": "Recursion over caller-supplied documents without a depth cap.",
 "X18": "Disposal-pattern correctness (IDisposable without using/finalizer discipline, double dispose).",
 "X19": "Process-global state (culture, current directory, environment) changed and not restored.",
 "X20": "Argument guards testing the wrong parameter or condition.",
 "X21": "Side effects inside pattern-matching guards.",
 "X22": "Release/lock guards contradicted by the state they check.",
 "X23": "Expensive diagnostic materialisation (serialisation, string building) outside an IsEnabled guard.",
 "X24": "Document values interpolated into markup/HTML/XML unescaped.",
 "X25": "Configuration knobs read and never used (inert options).",
 "X26": "Callbacks handed across threads without synchronisation.",
 "X27": "Collections modified while being enumerated.",
 "X28": "Index access outside the emptiness guard that protects it.",
 "X29": "Per-element actions decided by a fixed element (loop decision on index 0 / first).",
 "X30": "Support guards that admit what they claim to reject (inverted conditions).",
 "X32": "Types resolved by simple name across all loaded assemblies (Type.GetType by name, assembly scans).",
}

LENSES = """## Lens applicability

| Lens | Applies when | Otherwise |
|---|---|---|
| codeHealth, maturity, productionReadiness, securityCompliance, performance | always (production C# source present) | dimensions skipped individually with a reason |
| architecture | ≥1 analysable project | dropped by the fold; capped at 69 while <2 projects and <1 500 production LOC (spec rule) |
| domainModelling | DDD building blocks exist (aggregate/entity base types or repositories) | DM1–DM12 "not measured: no DDD building blocks" |
| eventDriven | commands, domain or integration events, or a message bus exist | ED1–ED4 not measured |
| eventSourcing | an event store / fold (Apply/When) exists | ES1–ES2 not measured |
| accessibility | .razor/.cshtml/.html markup exists outside vendored, build, mock-up and documentation folders | AC1–AC7 "lens not applicable" |

A dimension that is skipped is written to `notMeasured` with its reason and never carries a score (G-15 in SPEC-GAPS.md).
"""

def table(dims):
    out = ["| Dimension | Name | Evaluator | Status | Method / reason | Concepts (SARIF rule ids) |", "|---|---|---|---|---|---|"]
    for d in dims:
        i = d["id"]
        if i in nm: status, text = "**not measured**", nm[i]
        else: status, text = "measured", METHOD.get(i, "(method text missing)")
        out.append(f"| {i} | {d['name']} | {d['evaluator']} | {status} | {text} | {', '.join(f'`{c}`' for c in rules.get(i, []))} |")
    return "\n".join(out)

dims = catalog["dimensions"]
measured = [d for d in dims if d["id"] not in nm]; unmeasured = [d for d in dims if d["id"] in nm]
missing = [d["id"] for d in measured if d["id"] not in METHOD]
if missing: print("WARNING: no method text for", missing, file=sys.stderr)
by_lens = {}
for d in dims: by_lens.setdefault(d.get("lens", "(meta)"), []).append(d)
doc = [f"# Coverage — {VERSION}", "",
 f"The catalog has **{len(dims)} dimensions**. This engine measures **{len(measured)}** of them deterministically and records the other **{len(unmeasured)}** as not measured, each with a reason, in the evidence bundle's `notMeasured` list. An unmeasured dimension never carries a score: it is absent from `dimensions`, so the fold distributes its weight to what was measured (spec: coverage 0 ≠ score 0, see SPEC-GAPS.md G-03/G-15).", "",
 "Reasons for not measuring fall into four groups:", "",
 f"- **{sum(1 for d in unmeasured if 'judgment' in nm[d['id']])} LLM-evaluated dimensions** (`evaluator: llm` in the catalog): this engine makes no model calls and produces no score for them (D19–D22, D24, D25, M4, DM8, ED5, ES3, LA1–LA6).",
 f"- **{sum(1 for d in unmeasured if 'runtime' in nm[d['id']].lower())} runtime dimensions** (AX?1 family, D8): they observe a booted application or an executed test suite; this engine never starts anything.",
 f"- **{sum(1 for d in unmeasured if 'JavaScript' in nm[d['id']])} JavaScript/TypeScript dimensions** (R1, R2, R5–R11): outside the C#/.NET target; the C# counterparts are D1/D2, D4, D17, AX3.",
 "- **1 Erlang-specific dimension** (X31).", "",
 "Everything measured is static: Roslyn syntax and (restore-free) semantics, git history, manifests and configuration, markup via AngleSharp, and the offline data snapshots under `data/`. Scoring shapes: findings fold per file as 1−0.5^Σweights and per repository as 10·exp(−k·Σ/kLOC); checklists score passed/total; shares score linearly; reward dimensions are emitted only when there is something to credit.", "",
 LENSES, "## Dimensions by lens", ""]
for lens in ["codeHealth", "architecture", "maturity", "productionReadiness", "securityCompliance", "performance", "domainModelling", "eventDriven", "eventSourcing", "accessibility"] + [l for l in by_lens if l not in ("codeHealth", "architecture", "maturity", "productionReadiness", "securityCompliance", "performance", "domainModelling", "eventDriven", "eventSourcing", "accessibility")]:
    if lens not in by_lens: continue
    doc += [f"### {lens} ({sum(1 for d in by_lens[lens] if d['id'] not in nm)} of {len(by_lens[lens])} measured)", "", table(by_lens[lens]), ""]
doc += ["## Concepts without a detector", "", "The benchmark taxonomy concepts this engine does not emit, with the reason recorded in the mapping file's `unmapped` list (`benchmark/cai-reference.json`):", "", "| Concept | Reason |", "|---|---|"]
for concept, reason in re.findall(r'\["([\w-]+)"\] = ((?:"[^"]*"|\w+)(?:\s*\+\s*"[^"]*")*),', coverage.split("UnmappedConceptReasons")[1]):
    text = "".join(p[0] if p[0] else consts.get(p[1], p[1]) for p in re.findall(r'"([^"]*)"|(\w+)', reason))
    doc.append(f"| `{concept}` | {text} |")
open(os.path.join(ROOT, "COVERAGE.md"), "w").write("\n".join(doc) + "\n")
print(f"COVERAGE.md: {len(measured)} measured, {len(unmeasured)} not measured")
