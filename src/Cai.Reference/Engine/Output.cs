using System.Text.Json;
using System.Text.Json.Nodes;
using Cai.Reference.Scoring;

namespace Cai.Reference.Engine;

/// <summary>Writes the evidence bundle, the SARIF log, the benchmark scores file and the mapping. All byte-stable.</summary>
public static class Output
{
    public const string EngineName = "cai-reference";
    public static string EngineVersion => typeof(Output).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static EvidenceBundle BuildEvidence(ScanContext ctx, string? qualityBar)
    {
        var catalog = ctx.Catalog;
        var bundle = new EvidenceBundle
        {
            RubricVersion = catalog.RubricVersion,
            Commit = ctx.Repo.HeadCommit.Length > 0 ? ctx.Repo.HeadCommit : null,
            QualityBar = qualityBar,
            AnalyzableProjects = ctx.Workspace.Production.Count(),
            ProductionLoc = ctx.Workspace.ProductionLoc,
        };
        foreach (var m in ctx.Measurements.OrderBy(m => CatalogIndex(catalog, m.Id)))
        {
            var dim = catalog.Find(m.Id) ?? throw new InvalidOperationException($"measured '{m.Id}' is not in {catalog.RubricVersion}");
            if (dim.IsMeta || dim.Category is null)
                bundle.MetaDimensions.Add(new MetaDimensionEvidence { Id = m.Id, Lens = dim.Lens, Score = m.Score, Advisory = m.Advisory ? true : null });
            else
                bundle.Dimensions.Add(new DimensionEvidence { Id = m.Id, Category = dim.Category, Score = m.Score, Confidence = m.Confidence, Coverage = m.Coverage == 1.0 ? null : m.Coverage, Advisory = m.Advisory ? true : null });
        }
        var result = CaiScorer.Score(bundle, catalog);
        bundle.HeadlineScore = Math.Round(result.Cai, 2, MidpointRounding.AwayFromZero);
        bundle.Lenses = result.Lenses.Select(l => new LensEvidence { Lens = l.Lens, Score = Math.Round(l.Score, 2, MidpointRounding.AwayFromZero), OwaWeight = Math.Round(l.OwaWeight, 4, MidpointRounding.AwayFromZero) }).ToList();
        var measured = ctx.Measurements.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        var notMeasured = new JsonArray();
        foreach (var nm in ctx.NotMeasured.Where(n => !measured.Contains(n.Id)).GroupBy(n => n.Id).Select(g => g.First()).OrderBy(n => CatalogIndex(catalog, n.Id)))
            notMeasured.Add(new JsonObject { ["id"] = nm.Id, ["reason"] = nm.Reason });
        var extra = new Dictionary<string, JsonElement>
        {
            ["notMeasured"] = JsonSerializer.SerializeToElement(notMeasured),
            ["engine"] = JsonSerializer.SerializeToElement(new JsonObject
            {
                ["name"] = EngineName, ["version"] = EngineVersion,
                ["snapshots"] = new JsonObject(ctx.Data.Provenance.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => KeyValuePair.Create<string, JsonNode?>(k.Key, k.Value))),
                ["referenceAssemblies"] = new JsonArray(ctx.Workspace.ReferenceSets.Select(s => (JsonNode)s).ToArray()),
            }),
            ["measurement"] = JsonSerializer.SerializeToElement(new JsonObject
            {
                ["headDate"] = ctx.Repo.HeadDate == default ? null : ctx.Repo.HeadDate.ToString("yyyy-MM-ddTHH:mm:ssK"),
                ["testLoc"] = ctx.Workspace.TestLoc, ["files"] = ctx.Repo.Files.Count, ["commits"] = ctx.History.Commits.Count,
                ["projects"] = new JsonArray(ctx.Repo.Projects.Select(p => (JsonNode)new JsonObject { ["path"] = p.Path, ["role"] = p.Role.ToString() }).ToArray()),
            }),
        };
        foreach (var f in ctx.Facts.OrderBy(k => k.Key, StringComparer.Ordinal)) extra[f.Key] = JsonSerializer.SerializeToElement(f.Value);
        bundle.Extra = extra;
        return bundle;
    }

    private static int CatalogIndex(RubricCatalog c, string id) { for (var i = 0; i < c.Dimensions.Count; i++) if (c.Dimensions[i].Id == id) return i; return int.MaxValue; }

    public static string Sarif(ScanContext ctx)
    {
        var usedRules = ctx.Findings.Select(f => f.RuleId).Distinct().OrderBy(r => r, StringComparer.Ordinal).ToList();
        var ruleIndex = usedRules.Select((r, i) => (r, i)).ToDictionary(x => x.r, x => x.i, StringComparer.Ordinal);
        var rules = new JsonArray(usedRules.Select(r =>
        {
            var rule = Rules.Get(r);
            var dim = ctx.Catalog.Find(rule.Dimension);
            var o = new JsonObject
            {
                ["id"] = r, ["name"] = ToPascal(r),
                ["shortDescription"] = new JsonObject { ["text"] = rule.Title },
                ["fullDescription"] = new JsonObject { ["text"] = $"{rule.Title}. Measured under CAI dimension {rule.Dimension} ({dim?.Name}). {dim?.WhatItMeasures}" },
                ["properties"] = new JsonObject { ["dimension"] = rule.Dimension, ["dimensions"] = new JsonArray(rule.AllDimensions.Select(d => (JsonNode)d).ToArray()), ["lens"] = dim?.Lens },
            };
            if (rule.Cwe is not null) ((JsonObject)o["properties"]!)["cwe"] = rule.Cwe;
            return (JsonNode)o;
        }).ToArray());
        var results = new JsonArray(ctx.Findings
            .OrderBy(f => f.File ?? "", StringComparer.Ordinal).ThenBy(f => f.Line ?? 0).ThenBy(f => f.RuleId, StringComparer.Ordinal).ThenBy(f => f.Message, StringComparer.Ordinal).ThenBy(f => f.CommitSha ?? "", StringComparer.Ordinal)
            .Select(f =>
            {
                var r = new JsonObject
                {
                    ["ruleId"] = f.RuleId, ["ruleIndex"] = ruleIndex[f.RuleId], ["level"] = "warning",
                    ["message"] = new JsonObject { ["text"] = f.Message },
                };
                if (f.File is not null)
                {
                    var loc = new JsonObject { ["physicalLocation"] = new JsonObject { ["artifactLocation"] = new JsonObject { ["uri"] = f.File, ["uriBaseId"] = "%SRCROOT%" } } };
                    if (f.Line is > 0) ((JsonObject)loc["physicalLocation"]!)["region"] = new JsonObject { ["startLine"] = f.Line, ["endLine"] = f.EndLine is > 0 && f.EndLine >= f.Line ? f.EndLine : f.Line };
                    r["locations"] = new JsonArray(loc);
                }
                var props = new JsonObject { ["dimension"] = f.Dimension, ["weight"] = f.Weight };
                if (f.CommitSha is not null) props["commitSha"] = f.CommitSha;
                if (f.Properties is not null) foreach (var kv in f.Properties.OrderBy(k => k.Key, StringComparer.Ordinal)) props[kv.Key] = kv.Value;
                r["properties"] = props;
                return (JsonNode)r;
            }).ToArray());
        var log = new JsonObject
        {
            ["$schema"] = "https://json.schemastore.org/sarif-2.1.0.json",
            ["version"] = "2.1.0",
            ["runs"] = new JsonArray(new JsonObject
            {
                ["tool"] = new JsonObject { ["driver"] = new JsonObject { ["name"] = EngineName, ["version"] = EngineVersion, ["informationUri"] = "https://github.com/code-assurance-initiative/reference-implementation", ["rules"] = rules } },
                ["originalUriBaseIds"] = new JsonObject { ["%SRCROOT%"] = new JsonObject { ["uri"] = "file:///", ["description"] = new JsonObject { ["text"] = "repository root" } } },
                ["versionControlProvenance"] = new JsonArray(new JsonObject { ["repositoryUri"] = "file:///", ["revisionId"] = ctx.Repo.HeadCommit }),
                ["columnKind"] = "utf16CodeUnits",
                ["results"] = results,
                ["properties"] = new JsonObject { ["rubricVersion"] = ctx.Catalog.RubricVersion },
            }),
        };
        return log.ToJsonString(Pretty) + "\n";
    }

    /// <summary>The benchmark's score-band input: every measured dimension on the 0–100 scale.</summary>
    public static string Scores(ScanContext ctx)
    {
        var o = new JsonObject();
        foreach (var m in ctx.Measurements.OrderBy(m => m.Id, StringComparer.Ordinal)) o[m.Id] = Math.Round(m.Score * 10, 1, MidpointRounding.AwayFromZero);
        return o.ToJsonString(Pretty) + "\n";
    }

    /// <summary>The scanner-benchmark mapping (CONTRACT.md 1.4) generated from the rule table so it cannot drift.</summary>
    public static string Mapping(RubricCatalog catalog, IEnumerable<string> taxonomyConcepts, IReadOnlyDictionary<string, string> unmappedReasons)
    {
        var concepts = new JsonObject();
        foreach (var r in Rules.All.OrderBy(r => r.Concept, StringComparer.Ordinal))
        {
            var o = new JsonObject { ["rules"] = new JsonArray($"^{System.Text.RegularExpressions.Regex.Escape(r.Concept)}$"), ["dimensions"] = new JsonArray(r.AllDimensions.Select(d => (JsonNode)d).ToArray()) };
            if (r.AlsoDimensions.Length > 0) o["scoreDimensions"] = new JsonArray(r.Dimension);
            concepts[r.Concept] = o;
        }
        var unmapped = new JsonArray();
        foreach (var c in taxonomyConcepts.OrderBy(c => c, StringComparer.Ordinal))
        {
            if (Rules.Exists(c)) continue;
            concepts[c] = new JsonObject { ["rules"] = new JsonArray(), ["dimensions"] = new JsonArray() };
            unmapped.Add(new JsonObject { ["concept"] = c, ["reason"] = unmappedReasons.GetValueOrDefault(c) ?? "no detector in this engine" });
        }
        var ruleDimension = new JsonArray(Rules.All.OrderBy(r => r.Concept, StringComparer.Ordinal).Select(r => (JsonNode)new JsonObject { ["rule"] = $"^{System.Text.RegularExpressions.Regex.Escape(r.Concept)}$", ["dimension"] = r.Dimension }).ToArray());
        var m = new JsonObject
        {
            ["scanner"] = EngineName, ["version"] = catalog.RubricVersion,
            ["notes"] = "Generated by `cai-ref mapping` from the engine's rule table. Rule ids are taxonomy concept ids; the dimension named first is the one whose score the concept measures.",
            ["concepts"] = concepts, ["ruleDimension"] = ruleDimension, ["unmapped"] = unmapped,
        };
        return m.ToJsonString(Pretty) + "\n";
    }

    private static string ToPascal(string kebab) => string.Concat(kebab.Split('-').Select(p => p.Length == 0 ? "" : char.ToUpperInvariant(p[0]) + p[1..]));
}
