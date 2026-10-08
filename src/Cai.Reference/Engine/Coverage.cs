namespace Cai.Reference.Engine;

/// <summary>
/// The measure-or-not decision for every catalog dimension (mirrors COVERAGE.md). A dimension a detector did not
/// measure at scan time is recorded under `notMeasured` with the reason from this table, or with the dynamic reason
/// the detector gave (a lens that did not apply, a surface that was empty).
/// </summary>
public static class Coverage
{
    public const string Llm = "needs judgment: the catalog evaluates this dimension with a language model; this engine makes no model calls";
    public const string Runtime = "needs runtime: observed on a booted application, which this engine never starts";
    public const string JsTs = "JavaScript/TypeScript surface: outside this engine's C#/.NET target";

    public static readonly IReadOnlyDictionary<string, string> StaticReasons = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["D8"] = "needs runtime: line coverage requires executing the test suite; static test reachability is reported as R4",
        ["D19"] = Llm, ["D20"] = Llm, ["D21"] = Llm, ["D22"] = Llm, ["D24"] = Llm, ["D25"] = Llm, ["M4"] = Llm,
        ["DM8"] = Llm, ["ED5"] = Llm, ["ES3"] = Llm, ["LA1"] = Llm, ["LA2"] = Llm, ["LA3"] = Llm, ["LA4"] = Llm, ["LA5"] = Llm, ["LA6"] = Llm,
        ["AXA1"] = Runtime, ["AXB1"] = Runtime, ["AXB2"] = Runtime, ["AXH1"] = Runtime, ["AXI1"] = Runtime, ["AXK1"] = Runtime, ["AXO1"] = Runtime, ["AXP1"] = Runtime, ["AXR1"] = Runtime, ["AXS1"] = Runtime,
        ["R1"] = JsTs, ["R2"] = JsTs + " (C# complexity is D1/D2)", ["R5"] = JsTs + " (npm freshness)", ["R6"] = JsTs + " (package.json scripts)", ["R7"] = JsTs + " (C# dead code is D17)",
        ["R8"] = JsTs + " (npm dependency truthfulness)", ["R9"] = JsTs + " (C# cycles are AX3)", ["R10"] = JsTs + " (C# duplication is D4)", ["R11"] = JsTs,
        ["X31"] = "Erlang-specific (export lists); not applicable to C#",
    };

    /// <summary>Taxonomy concepts this engine has no detector for, with the reason, for the mapping's `unmapped` list.</summary>
    public static readonly IReadOnlyDictionary<string, string> UnmappedConceptReasons = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["outdated-dependency"] = "needs registry data (latest versions); the engine runs offline",
        ["deprecated-dependency"] = "needs registry data (deprecation metadata); the engine runs offline",
        ["unused-dependency"] = "npm concept", ["undeclared-dependency"] = "npm concept", ["misplaced-dev-dependency"] = "npm concept",
        ["floating-promise"] = "JavaScript concept", ["prototype-pollution"] = "JavaScript concept", ["unchecked-any-external-data"] = "TypeScript concept",
        ["untyped-javascript-share"] = "JavaScript concept", ["frontend-tooling-scripts"] = "npm concept",
        ["react-index-as-key"] = "React concept", ["react-hook-missing-dependency"] = "React concept", ["react-state-mutation"] = "React concept",
        ["sensitive-data-in-browser-storage"] = "browser-side JavaScript concept",
        ["test-only-api-in-production-code"] = "Erlang concept",
        ["missing-subresource-integrity"] = "not implemented",
        ["sensitive-data-in-token-payload"] = "not implemented",
        ["nosql-injection"] = "not implemented (no NoSQL sink model)",
        ["mass-assignment"] = "not implemented",
        ["dependency-release-cooldown-missing"] = "not implemented",
        ["iac-misconfiguration"] = "umbrella: this engine emits the precise child concepts",
        ["container-excessive-privilege"] = "umbrella: this engine emits the precise child concepts",
        ["low-value-comments"] = Llm, ["inconsistent-naming"] = Llm, ["internal-api-inconsistency"] = Llm, ["documentation-quality"] = Llm, ["adr-quality"] = Llm,
        ["adr-conformance"] = Llm, ["documentation-accuracy"] = Llm, ["primitive-obsession"] = Llm, ["non-idempotent-message-handler"] = Llm,
        ["personal-data-in-event-store"] = Llm, ["personal-data-inventory"] = Llm, ["alt-text-quality"] = Llm, ["link-and-button-text-quality"] = Llm,
        ["heading-and-label-text-quality"] = Llm, ["vulnerability-disclosure-policy-quality"] = Llm, ["environment-separation"] = Llm,
        ["unauthenticated-reachable-endpoint"] = Runtime, ["reproducible-boot"] = Runtime, ["undocumented-api-endpoint"] = Runtime, ["third-party-data-flow"] = Runtime, ["unexpected-exposed-port"] = Runtime,
        ["secret-in-process-arguments"] = "not implemented",
        ["consent-not-checked"] = "not implemented",
    };

    public static void FillNotMeasured(ScanContext ctx)
    {
        var measured = ctx.Measurements.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        var skipped = ctx.NotMeasured.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var d in ctx.Catalog.Dimensions)
        {
            if (measured.Contains(d.Id) || skipped.Contains(d.Id)) continue;
            ctx.Skip(d.Id, StaticReasons.GetValueOrDefault(d.Id) ?? "no detector ran for this dimension");
        }
    }
}
