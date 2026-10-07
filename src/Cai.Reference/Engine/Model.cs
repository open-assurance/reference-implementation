using Cai.Reference.Scoring;
using Microsoft.CodeAnalysis;

namespace Cai.Reference.Engine;

/// <summary>A located (or repository-level) finding. RuleId is the benchmark taxonomy concept it denotes.</summary>
public sealed record Finding(string RuleId, string Dimension, string Message, string? File = null, int? Line = null, int? EndLine = null, double Weight = 1.0, string? CommitSha = null)
{
    public Dictionary<string, string>? Properties { get; init; }
}

/// <summary>One measured dimension: a 0–10 score with how sure and how complete the measurement was.</summary>
public sealed record Measurement(string Id, double Score, double Confidence = 1.0, double Coverage = 1.0, bool Advisory = false, string? Note = null);

public sealed record NotMeasured(string Id, string Reason);

/// <summary>Everything the detectors read and write while one repository is measured.</summary>
public sealed class ScanContext
{
    public required Repository Repo { get; init; }
    public required RubricCatalog Catalog { get; init; }
    public required Workspace Workspace { get; init; }
    public required GitHistory History { get; init; }
    public required DataSnapshots Data { get; init; }
    public string? NuGetPackagesDir { get; init; }
    public List<Finding> Findings { get; } = new();
    public List<Measurement> Measurements { get; } = new();
    public List<NotMeasured> NotMeasured { get; } = new();
    public Dictionary<string, string> Facts { get; } = new(StringComparer.Ordinal);   // descriptive, never scored
    private CodeGraph? _graph;
    public CodeGraph Graph => _graph ??= CodeGraph.Build(Workspace);

    public void Add(Finding f) => Findings.Add(f);
    public void Measure(string id, double score, double confidence = 1.0, double coverage = 1.0, bool advisory = false, string? note = null) =>
        Measurements.Add(new Measurement(id, Math.Clamp(Math.Round(score, 2, MidpointRounding.AwayFromZero), 0, 10), Math.Round(confidence, 2), Math.Round(coverage, 2), advisory, note));
    public void Skip(string id, string reason) => NotMeasured.Add(new NotMeasured(id, reason));

    public IEnumerable<Finding> FindingsFor(string dimension) => Findings.Where(f => f.Dimension == dimension);

    /// <summary>Production kLOC (non-test, non-generated C#), floored at 1 so small repositories are not punished per finding.</summary>
    public double ProductionKloc => Math.Max(1.0, Workspace.ProductionLoc / 1000.0);

    public Location Loc(SyntaxNode node)
    {
        var span = node.GetLocation().GetLineSpan();
        return new Location(Workspace.RelPath(span.Path), span.StartLinePosition.Line + 1, span.EndLinePosition.Line + 1);
    }
    public Location Loc(SyntaxToken token)
    {
        var span = token.GetLocation().GetLineSpan();
        return new Location(Workspace.RelPath(span.Path), span.StartLinePosition.Line + 1, span.EndLinePosition.Line + 1);
    }
}

public readonly record struct Location(string File, int Line, int EndLine);

/// <summary>The shared scoring shapes, so every dimension's number is explainable the same way (COVERAGE.md).</summary>
public static class Shape
{
    /// <summary>
    /// Finding dimensions: per-file load 1 − 0.5^(Σ weights in the file) — rises steeply over the first few instances
    /// and levels off — summed over files and normalised per production kLOC; score = 10·exp(−k·density).
    /// k = 1 means one default finding in a 1 kLOC codebase scores 6.1; one finding in 5 kLOC scores 9.0.
    /// </summary>
    public static double FromFindings(IEnumerable<Finding> findings, double kloc, double k = 1.0)
    {
        var perFile = findings.GroupBy(f => f.File ?? "", StringComparer.Ordinal).Select(g => 1 - Math.Pow(0.5, g.Sum(f => f.Weight)));
        var load = perFile.Sum();
        return 10 * Math.Exp(-k * load / Math.Max(1.0, kloc));
    }

    /// <summary>Share dimensions: a clean share maps linearly, with an optional floor below which it is scored 0.</summary>
    public static double FromShare(double goodShare, double floor = 0) => goodShare <= floor ? 0 : 10 * (goodShare - floor) / (1 - floor);

    /// <summary>Posture dimensions: passed items over the checklist.</summary>
    public static double FromChecklist(int passed, int total) => total == 0 ? 10 : 10.0 * passed / total;
}

/// <summary>The engine's rule table: every concept it can emit, the dimension that owns it and the ones that also carry it.</summary>
public static class Rules
{
    public sealed record Rule(string Concept, string Dimension, string[] AlsoDimensions, string Title, string? Cwe = null)
    {
        public string[] AllDimensions => new[] { Dimension }.Concat(AlsoDimensions).Distinct().ToArray();
        /// <summary>Sibling concepts one scanner may legitimately confuse (CONTRACT.md 1.1 families); symmetric, so it cannot flatter the engine.</summary>
        public string? Family => Concept switch
        {
            "hardcoded-credential" or "hardcoded-password" or "hardcoded-cryptographic-key" => "hardcoded-secret",
            "weak-hash-algorithm" or "insufficient-password-hashing" => "weak-password-hashing",
            "insecure-deserialization" or "code-injection" => "untrusted-data-executed",
            "high-cyclomatic-complexity" or "high-cognitive-complexity" => "complexity",
            "not-implemented-placeholder" or "incomplete-implementation" => "incomplete",
            _ => null,
        };
    }

    private static Rule R(string concept, string dim, string title, string? cwe = null, params string[] also) => new(concept, dim, also, title, cwe);

    public static readonly IReadOnlyList<Rule> All = new List<Rule>
    {
        // code health
        R("high-cyclomatic-complexity", "D1", "Method with high cyclomatic complexity", "CWE-1121"),
        R("high-cognitive-complexity", "D2", "Method with high cognitive complexity"),
        R("god-class", "D3", "Oversized class (god class)"),
        R("long-method", "D3", "Overlong method"),
        R("oversized-source-file", "R3", "Oversized source file", "CWE-1080", "D3"),
        R("duplicated-code", "D4", "Duplicated code", "CWE-1041"),
        R("technical-debt-marker", "D17", "TODO / FIXME / HACK marker", "CWE-546"),
        R("suppressed-diagnostic", "D17", "Suppressed compiler or analyzer diagnostic"),
        R("commented-out-code", "D17", "Commented-out code"),
        R("unused-code", "D17", "Unused private symbol", "CWE-561"),
        R("unreachable-code", "D17", "Unreachable branch or region", "CWE-561", "X12", "IC1"),
        R("obsolete-symbol-still-used", "D17", "Obsolete symbol still used by its own codebase", "CWE-477"),
        R("not-implemented-placeholder", "GD1", "Not-implemented stub in shipped code", null, "IC1"),
        R("incomplete-implementation", "IC1", "Incomplete implementation by code shape"),
        R("empty-catch-block", "X3", "Empty catch block", "CWE-1069", "D17"),
        R("pointless-catch-rethrow", "X3", "Catch that only rethrows"),
        R("rethrow-resets-stack-trace", "X3", "Rethrow discards the original stack trace"),
        R("blocking-on-async-code", "X1", "Blocking on asynchronous code", null, "PF3"),
        R("async-void-method", "X1", "async void outside an event handler"),
        R("missing-cancellation-propagation", "X2", "Async operation without a cancellation token"),
        R("non-structured-log-message", "X4", "Log message built by string interpolation"),
        R("nullable-analysis-disabled", "X5", "Nullable reference types not enabled"),
        R("null-forgiving-suppression", "X5", "Null-safety suppressed with the null-forgiving operator"),
        R("null-dereference", "X5", "Possible null dereference", "CWE-476"),
        R("hand-rolled-structured-format-parsing", "X6", "Hand-rolled JSON/XML parsing"),
        R("silent-error-fallback", "X7", "Failure path silently falls back to a constant", "CWE-390"),
        R("js-interop-contract-mismatch", "X8", "JavaScript interop call to a missing function"),
        R("redundant-condition-operand", "X9", "Subsumed operand in a boolean condition"),
        R("undrained-child-process-stream", "X13", "Child process stream never drained", "CWE-833"),
        R("unbounded-truncation-loop", "X16", "Truncation loop without a floor", "CWE-835"),
        R("improper-resource-disposal", "X18", "Disposable resource ownership defect", "CWE-404"),
        R("unrestored-process-global-state", "X19", "Process-global state not restored on failure"),
        R("argument-guard-tests-wrong-condition", "X20", "Argument guard throws the wrong exception"),
        R("side-effect-in-conditional-guard", "X21", "Side effect inside a pattern guard"),
        R("lock-release-state-mismatch", "X22", "Lock release guarded by inconsistent state", "CWE-667"),
        R("unguarded-expensive-debug-logging", "X23", "Expensive argument built for a disabled log level"),
        R("inert-configuration-option", "X25", "Configuration option that has no effect"),
        R("unsynchronized-callback-handoff", "X26", "Non-thread-safe collection handed across callbacks", "CWE-362"),
        R("collection-modified-during-enumeration", "X27", "Collection modified while being enumerated"),
        R("index-access-outside-bounds-guard", "X28", "Index access outside its own emptiness guard", "CWE-129"),
        R("loop-decision-on-fixed-element", "X29", "Per-element decision taken from a fixed element"),
        R("contradictory-support-guard", "X30", "Support guard admits what it should reject"),
        R("type-lookup-by-simple-name", "X32", "Type resolved by simple name across loaded assemblies"),
        R("missing-configure-await", "PF3", "Library awaits without ConfigureAwait(false)"),
        R("allocation-awareness", "PF2", "Allocation-aware API usage"),
        R("benchmark-discipline", "PF1", "Performance benchmarks"),
        // architecture
        R("module-dependency-cycle", "AX3", "Circular project reference", "CWE-1047", "D5", "D7"),
        R("unstable-dependency", "D5", "Stable project depends on a volatile one"),
        R("module-off-main-sequence", "D5", "Project far from the main sequence"),
        R("layer-dependency-violation", "AX4", "Layer dependency points the wrong way", null, "D7", "D5"),
        R("architecture-rules-unenforced", "D7", "Recorded architecture rule not enforced"),
        R("boundary-type-leakage", "D23", "Domain type leaks across a bounded-context boundary"),
        R("oversized-module", "D26", "Oversized project"),
        R("call-indirection", "D27", "Excessive call indirection"),
        R("change-coupling", "D35", "Files change together without a declared dependency"),
        R("captive-dependency", "AX1", "Singleton captures a shorter-lived dependency"),
        R("unsynchronized-shared-state", "AX2", "Unsynchronised mutable state in a singleton", "CWE-362"),
        R("architecture-style-fit", "AX5", "Recognisable, scale-appropriate structure"),
        R("fat-interface", "AX6", "Fat interface"),
        R("cross-slice-coupling", "AX7", "Direct reference between feature slices"),
        R("production-depends-on-test-code", "AX8", "Production project references a test project"),
        R("query-with-side-effects", "AX9", "Query handler with side effects"),
        R("business-logic-share", "AX10", "Business-logic share of production code"),
        R("business-logic-in-controller", "AX10", "Business logic in a controller"),
        R("low-class-cohesion", "D6", "Low class cohesion (LCOM4)"),
        R("solution-structure", "D18", "Solution structure"),
        R("compiled-code-size", "D39", "Oversized compiled method body"),
        // maturity
        R("churn-complexity-hotspot", "D15", "Churn × complexity hotspot"),
        R("knowledge-concentration", "D16", "Knowledge concentrated in one contributor"),
        R("knowledge-freshness", "D34", "Orphaned knowledge: stale file whose authors have gone quiet"),
        R("readme-quality", "M1", "README present and substantive"),
        R("architecture-documentation", "M2", "Architecture documentation and ADRs"),
        R("folder-structure", "M3", "Folder and project structure conventions"),
        // readiness
        R("test-pyramid-distribution", "D9", "Test distribution across levels"),
        R("test-without-assertion", "D10", "Test without an assertion"),
        R("skipped-test-without-reason", "D10", "Skipped test without a structured reason", null, "IC1"),
        R("excessive-mocking", "D10", "Test dominated by mocks"),
        R("test-failure-swallowed", "D10", "Test whose failure is swallowed"),
        R("flaky-test", "D11", "Flaky (non-deterministic) test", null, "D10"),
        R("test-coverage", "R4", "Static test reachability"),
        R("domain-vs-controller-coverage", "P9", "Domain versus controller test coverage"),
        R("ci-build-and-test-pipeline", "P1", "CI pipeline builds and tests"),
        R("observability", "P2", "Observability: structured logging, tracing, health checks"),
        R("security-tooling-in-ci", "P3", "Security and performance tooling gates changes"),
        R("deployment-rollback-safety", "P4", "Deployment and rollback safety"),
        R("disaster-recovery-evidence", "P5", "Disaster recovery and backup evidence"),
        R("release-hygiene", "P6", "Release hygiene: changelog and versioning"),
        R("outbound-http-resilience", "P7", "Outbound HTTP call without timeout/retry/breaker"),
        R("versioned-schema-migrations", "P8", "Schema changes without versioned migrations"),
        R("library-api-versioning", "P10", "Library public API and semantic versioning"),
        R("executable-specifications", "P11", "Executable specifications (BDD)"),
        R("ci-test-gate-integrity", "P12", "CI test gate honesty"),
        R("vulnerable-dependency", "D30", "Dependency with a known vulnerability", "CWE-1395", "D12"),
        R("malicious-dependency", "D43", "Known-malicious dependency", "CWE-506"),
        R("end-of-life-platform", "D44", "End-of-life runtime or framework", "CWE-1104"),
        R("prerelease-dependency", "D12", "Dependency resolved to a pre-release build"),
        R("dependencies-not-locked", "SC1", "Dependencies not locked", null, "D12"),
        R("license-policy-violation", "D14", "Dependency licence incompatible with policy"),
        // security & compliance
        R("hardcoded-credential", "D13", "Hard-coded credential", "CWE-798", "D28"),
        R("hardcoded-password", "D13", "Hard-coded password", "CWE-259", "D28"),
        R("hardcoded-cryptographic-key", "D13", "Hard-coded cryptographic key", "CWE-321", "D28"),
        R("committed-private-key", "D13", "Committed private key material", "CWE-798", "D28"),
        R("secret-in-version-history", "D28", "Secret retained in version-control history", "CWE-540"),
        R("sql-injection", "D29", "SQL injection", "CWE-89"),
        R("command-injection", "D29", "OS command injection", "CWE-78"),
        R("code-injection", "D29", "Code / template injection", "CWE-94"),
        R("path-traversal", "D29", "Path traversal", "CWE-22"),
        R("xml-external-entity", "D29", "XML external entity processing", "CWE-611"),
        R("insecure-deserialization", "D29", "Deserialization of untrusted data", "CWE-502"),
        R("cross-site-scripting", "D29", "Cross-site scripting / unescaped markup output", "CWE-79"),
        R("regex-denial-of-service", "D29", "Regular expression denial of service", "CWE-1333"),
        R("ldap-injection", "D29", "LDAP injection", "CWE-90"),
        R("xpath-injection", "D29", "XPath injection", "CWE-643"),
        R("server-side-request-forgery", "D29", "Server-side request forgery", "CWE-918", "X14"),
        R("open-redirect", "D29", "Open redirect", "CWE-601"),
        R("weak-cryptographic-algorithm", "D29", "Broken or risky cryptographic algorithm", "CWE-327", "S1"),
        R("weak-hash-algorithm", "D29", "Weak hash used for a security purpose", "CWE-328", "S1"),
        R("insufficient-password-hashing", "D29", "Password stored without a key-derivation function", "CWE-916", "S1"),
        R("insecure-randomness", "D29", "Insecure randomness for security values", "CWE-338"),
        R("cleartext-transmission", "D29", "Cleartext transmission", "CWE-319", "S1", "D31"),
        R("missing-authorization", "D29", "Missing authorization on an endpoint", "CWE-862", "C2"),
        R("error-information-exposure", "D29", "Error detail exposed to clients", "CWE-209"),
        R("improper-certificate-validation", "D29", "Improper certificate validation", "CWE-295", "S1"),
        R("token-signature-or-expiry-not-validated", "D29", "Token signature or lifetime not validated", "CWE-347", "S1"),
        R("log-injection", "D29", "Log injection", "CWE-117"),
        R("sensitive-data-in-logs", "D32", "Sensitive or personal data written to logs", "CWE-532", "D29"),
        R("sensitive-data-in-url", "D32", "Sensitive data in URL / query string", "CWE-598", "S1"),
        R("unbounded-allocation-from-untrusted-length", "X15", "Allocation sized by an unvalidated length", "CWE-789", "D29"),
        R("uncontrolled-recursion", "X17", "Uncontrolled recursion over caller-supplied input", "CWE-674"),
        R("ci-workflow-injection", "D36", "CI workflow expression injection", "CWE-78", "D29"),
        R("ci-secret-exposure", "D36", "CI secret exposed to untrusted code", null, "D29"),
        R("secret-in-process-arguments", "D36", "Secret passed as a process argument", "CWE-214"),
        R("ci-token-excessive-permissions", "D36", "CI job token broader than the job needs", "CWE-250"),
        R("unpinned-ci-action", "D36", "Third-party CI action not pinned to a commit", "CWE-829"),
        R("download-without-integrity-check", "D31", "Remote code downloaded and executed without integrity check", "CWE-494", "D36"),
        R("build-provenance-and-signing", "D36", "Build provenance, signing and SBOM"),
        R("vulnerability-disclosure-policy", "D37", "Vulnerability disclosure policy present"),
        R("container-runs-as-root", "D31", "Container runs as root", "CWE-250"),
        R("privileged-container", "D31", "Privileged container", "CWE-250"),
        R("host-namespace-sharing", "D31", "Container shares a host namespace", "CWE-250"),
        R("host-path-mount", "D31", "Host path or runtime socket mounted into a container", "CWE-250"),
        R("container-privilege-escalation-allowed", "D31", "Container may escalate its privileges", "CWE-250"),
        R("container-excess-capabilities", "D31", "Container keeps or adds capabilities it does not need", "CWE-250"),
        R("container-writable-root-filesystem", "D31", "Container root filesystem is writable"),
        R("container-confinement-profile-unset", "D31", "Container has no seccomp or AppArmor profile"),
        R("container-security-context-missing", "D31", "Container declares no security context", "CWE-250"),
        R("container-missing-resource-limits", "D31", "Workload without resource limits", "CWE-770"),
        R("container-missing-resource-requests", "D31", "Workload without resource requests"),
        R("mutable-image-reference", "D31", "Container image referenced by mutable tag", null, "D36"),
        R("missing-health-probes", "D31", "Kubernetes workload without probes"),
        R("missing-image-healthcheck", "D31", "Container image without a HEALTHCHECK"),
        R("automounted-service-account-token", "D31", "Service-account token mounted into a pod that does not need it"),
        R("overly-permissive-rbac", "D31", "Overly permissive RBAC role or binding", "CWE-269"),
        R("image-not-from-allowed-registry", "D31", "Container image from a registry outside the allowed list"),
        R("network-egress-policy", "D40", "Network egress restricted for workloads"),
        R("workload-syscall-confinement", "D41", "Workload syscall / MAC confinement"),
        R("runtime-threat-detection-and-admission", "D42", "Runtime threat detection and admission control"),
        R("security-response-headers", "S1", "Security response headers configured"),
        R("https-enforcement", "S1", "HTTPS enforced by the application"),
        R("insecure-cookie-flags", "S1", "Cookie without Secure/HttpOnly/SameSite", "CWE-614"),
        R("inbound-input-validation", "S1", "Inbound request model validation"),
        R("data-encryption-controls", "C1", "Data protection / encryption controls", "CWE-311"),
        R("authorization-enforcement", "C2", "Authorization enforcement posture"),
        R("audit-trail", "C3", "Audit trail of changes", "CWE-778"),
        R("data-retention-policy", "C4", "Data retention / TTL controls"),
        R("data-subject-rights", "C5", "Data-subject rights (erasure / export / consent)"),
        // domain, events, event sourcing
        R("cross-aggregate-object-reference", "DM1", "Aggregate holds an object reference to another aggregate"),
        R("primitive-entity-identifier", "DM2", "Entity identified by a raw primitive"),
        R("integration-event-leaks-domain-type", "DM3", "Integration event exposes producer domain types"),
        R("anemic-domain-model", "DM4", "Anemic domain model"),
        R("publicly-mutable-entity-state", "DM5", "Entity state publicly mutable"),
        R("domain-depends-on-infrastructure", "DM6", "Domain layer depends on infrastructure"),
        R("repository-for-non-aggregate", "DM7", "Repository over a non-aggregate"),
        R("scattered-domain-rule", "DM9", "Domain rule decided outside its owning type"),
        R("multi-aggregate-transaction", "DM10", "One operation mutates several aggregates"),
        R("constructible-invalid-entity", "DM11", "Entity constructible in an invalid state"),
        R("ambient-nondeterminism-in-domain", "DM12", "Ambient clock or random read inside the domain"),
        R("value-object-mutability", "DM5", "Value object that is mutable or compared by identity"),
        R("synchronous-remote-call-in-event-handler", "ED1", "Synchronous remote call inside an event handler"),
        R("command-with-multiple-handlers", "ED2", "Command handled by more or fewer than one handler"),
        R("event-not-named-in-past-tense", "ED3", "Event not named in the past tense"),
        R("dual-write-without-outbox", "ED4", "Database write and message publish without an outbox"),
        R("domain-event-never-handled", "ED2", "Domain event raised but never handled"),
        R("nondeterministic-event-fold", "ES1", "Non-deterministic event application"),
        R("mutable-persisted-event", "ES2", "Persisted event is mutable"),
        R("event-schema-change-without-upcaster", "ES2", "Persisted event schema changed without an upcaster"),
        // accessibility
        R("missing-text-alternative", "AC1", "Missing text alternative"),
        R("form-control-without-label", "AC2", "Form control without an accessible label"),
        R("page-structure-violation", "AC3", "Page structure / landmark violation"),
        R("non-keyboard-accessible-interaction", "AC4", "Interaction not keyboard accessible"),
        R("invalid-aria-usage", "AC5", "Invalid ARIA usage"),
        R("focus-outline-removed", "AC6", "Keyboard focus indicator removed"),
        R("motion-without-reduced-motion", "AC6", "Animation that ignores the reduced-motion preference"),
        R("visual-and-motion-safety", "AC6", "Contrast, focus visibility and motion safety"),
        R("form-error-not-associated", "AC2", "Form error not programmatically associated with its field"),
        R("modal-focus-not-managed", "AC4", "Modal dialog does not manage focus"),
        R("autoplay-media-without-control", "AC1", "Media plays automatically with no way to stop it"),
        R("accessibility-checks-in-ci", "AC7", "Accessibility checks enforced"),
    };

    /// <summary>Concepts whose findings describe an absent or partial posture rather than a located defect.</summary>
    public static readonly HashSet<string> PostureConcepts = new(StringComparer.Ordinal)
    {
        "readme-quality", "architecture-documentation", "folder-structure", "solution-structure", "ci-build-and-test-pipeline", "observability", "security-tooling-in-ci",
        "deployment-rollback-safety", "disaster-recovery-evidence", "release-hygiene", "library-api-versioning", "executable-specifications",
        "architecture-style-fit", "test-pyramid-distribution", "domain-vs-controller-coverage", "build-provenance-and-signing", "vulnerability-disclosure-policy",
        "network-egress-policy", "workload-syscall-confinement", "runtime-threat-detection-and-admission", "security-response-headers", "https-enforcement",
        "inbound-input-validation", "data-encryption-controls", "authorization-enforcement", "audit-trail", "data-retention-policy", "data-subject-rights",
        "accessibility-checks-in-ci", "benchmark-discipline", "allocation-awareness", "dependencies-not-locked", "business-logic-share",
    };

    private static readonly Dictionary<string, Rule> ByConcept = All.ToDictionary(r => r.Concept, StringComparer.Ordinal);
    public static Rule Get(string concept) => ByConcept.TryGetValue(concept, out var r) ? r : throw new ArgumentException($"unknown rule '{concept}'");
    public static bool Exists(string concept) => ByConcept.ContainsKey(concept);
}
