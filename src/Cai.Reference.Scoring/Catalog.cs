using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cai.Reference.Scoring;

/// <summary>A frozen rubric catalog: lenses, dimensions, and the scoring parameters the fold runs under.</summary>
public sealed class RubricCatalog
{
    public required string RubricVersion { get; init; }
    public required IReadOnlyList<CatalogLens> Lenses { get; init; }
    public required IReadOnlyList<CatalogDimension> Dimensions { get; init; }
    public required ScoringParameters Scoring { get; init; }

    private Dictionary<string, CatalogDimension>? _byId;
    private Dictionary<string, string>? _lensOfCategory;

    public CatalogDimension? Find(string id)
    {
        _byId ??= Dimensions.ToDictionary(d => d.Id, StringComparer.Ordinal);
        return _byId.GetValueOrDefault(id);
    }

    /// <summary>
    /// The lens a scored category belongs to, read off the catalog's own dimension→category map. Catalogs published
    /// before rubric-2026.08.18 carry no categories at all, yet the published scorer still routes a bundle's categories
    /// to lenses (oracle: the sample bundle's D8 in 'code-quality' folds into Code Health under rubric-2026.08.15), so
    /// the map that later catalogs pin is used as the fallback — see SPEC-GAPS.md.
    /// </summary>
    public string? LensOfCategory(string category)
    {
        _lensOfCategory ??= Dimensions.Where(d => d.Category is not null)
            .GroupBy(d => d.Category!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Lens, StringComparer.Ordinal);
        return _lensOfCategory.GetValueOrDefault(category) ?? DefaultCategoryLens.GetValueOrDefault(category);
    }

    public static readonly IReadOnlyDictionary<string, string> DefaultCategoryLens = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["code-quality"] = "codeHealth", ["explicit-debt"] = "codeHealth", ["architecture"] = "architecture",
        ["git-mining"] = "maturity", ["docs"] = "maturity", ["testing"] = "productionReadiness",
        ["dependencies"] = "productionReadiness", ["security"] = "productionReadiness", ["security-compliance"] = "securityCompliance",
    };

    public static RubricCatalog Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var lenses = root.GetProperty("lenses").EnumerateArray()
            .Select(l => new CatalogLens(l.GetProperty("key").GetString()!, l.GetProperty("label").GetString()!)).ToList();
        var dims = root.GetProperty("dimensions").EnumerateArray().Select(d => new CatalogDimension
        {
            Id = d.GetProperty("id").GetString()!,
            Name = d.GetProperty("name").GetString()!,
            Lens = d.GetProperty("lens").GetString()!,
            Category = d.TryGetProperty("category", out var c) ? c.GetString() : null,
            Evaluator = d.TryGetProperty("evaluator", out var e) ? e.GetString() ?? "tool" : "tool",
            Family = d.TryGetProperty("family", out var f) ? f.GetString() ?? "dimension" : "dimension",
            ScoringPolarity = d.TryGetProperty("scoringPolarity", out var p) ? p.GetString() : null,
            CeilingRung = d.TryGetProperty("ceilingRung", out var r) ? r.GetString() : null,
            WhatItMeasures = d.TryGetProperty("whatItMeasures", out var w) ? w.GetString() ?? "" : "",
        }).ToList();
        var scoring = root.TryGetProperty("scoring", out var s) ? ScoringParameters.FromJson(s) : ScoringParameters.Default;
        return new RubricCatalog
        {
            RubricVersion = root.GetProperty("rubricVersion").GetString()!,
            Lenses = lenses, Dimensions = dims, Scoring = scoring,
        };
    }

    /// <summary>Resolve <c>rubric-&lt;version&gt;/rubric-catalog.json</c> under a published archive directory.</summary>
    public static RubricCatalog LoadFromArchive(string archiveDir, string rubricVersion)
    {
        if (string.IsNullOrWhiteSpace(rubricVersion) || rubricVersion.Contains("..") || rubricVersion.Contains('/') || rubricVersion.Contains('\\'))
            throw new ArgumentException($"rubric version '{rubricVersion}' is not well-formed.");
        var path = Path.Combine(archiveDir, rubricVersion, "rubric-catalog.json");
        if (!File.Exists(path)) throw new FileNotFoundException($"rubric version '{rubricVersion}' is not published under {archiveDir}.", path);
        var catalog = Load(path);
        if (!string.Equals(catalog.RubricVersion, rubricVersion, StringComparison.Ordinal))
            throw new InvalidOperationException($"catalog at {path} declares '{catalog.RubricVersion}', not '{rubricVersion}' (unattested).");
        return catalog;
    }

    public static string LatestVersion(string archiveDir) =>
        Directory.GetDirectories(archiveDir, "rubric-*").Select(Path.GetFileName).Where(n => n is not null)
            .Select(n => n!).OrderBy(v => v, RubricVersionComparer.Instance).Last();
}

public sealed record CatalogLens(string Key, string Label);

public sealed class CatalogDimension
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Lens { get; init; }
    public string? Category { get; init; }
    public string Evaluator { get; init; } = "tool";
    public string Family { get; init; } = "dimension";
    public string? ScoringPolarity { get; init; }
    public string? CeilingRung { get; init; }
    public string WhatItMeasures { get; init; } = "";
    public bool IsMeta => Family == "meta";
    public bool IsLlm => Evaluator == "llm";
}

/// <summary>Orders <c>rubric-YYYY.MM.N</c> numerically (so .10 sorts after .9).</summary>
public sealed class RubricVersionComparer : IComparer<string>
{
    public static readonly RubricVersionComparer Instance = new();
    public int Compare(string? x, string? y)
    {
        static int[] Parts(string? v) => (v ?? "").Replace("rubric-", "").Split('.').Select(p => int.TryParse(p, out var n) ? n : -1).ToArray();
        var a = Parts(x); var b = Parts(y);
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var c = (i < a.Length ? a[i] : 0).CompareTo(i < b.Length ? b[i] : 0);
            if (c != 0) return c;
        }
        return 0;
    }
}

/// <summary>Every input to the fold that can move a score, as the catalog's <c>scoring</c> block publishes it.</summary>
public sealed record ScoringParameters(
    double WithinLensQ, double AcrossLensQ, double CriticalGate,
    ArchitectureSurface ArchitectureSurface, BandCutlines Bands, QualityBarRules QualityBar)
{
    /// <summary>The values every published catalog without a block resolves to (the article quotes these).</summary>
    public static readonly ScoringParameters Default = new(
        0.75, 0.55, 4,
        new ArchitectureSurface(2, 1500, 69),
        new BandCutlines(90, 70, 50, 25),
        new QualityBarRules(
            new Dictionary<string, double> { ["prototype"] = -18, ["preview"] = -8, ["production"] = 0, ["mission-critical"] = 6 },
            new Dictionary<string, double> { ["foundational"] = 0.4, ["operational"] = 1, ["safety"] = 0.25, ["default"] = 0.7 },
            98, 5));

    public static ScoringParameters FromJson(JsonElement s)
    {
        var d = Default;
        double D(JsonElement e, string n, double dflt) => e.TryGetProperty(n, out var v) ? v.GetDouble() : dflt;
        var arch = s.TryGetProperty("architectureSurface", out var a)
            ? new ArchitectureSurface((int)D(a, "minProjects", d.ArchitectureSurface.MinProjects), (int)D(a, "minProductionLoc", d.ArchitectureSurface.MinProductionLoc), D(a, "lowSurfaceCap", d.ArchitectureSurface.LowSurfaceCap))
            : d.ArchitectureSurface;
        var bands = s.TryGetProperty("bands", out var b)
            ? new BandCutlines(D(b, "exemplary", 90), D(b, "healthy", 70), D(b, "fair", 50), D(b, "poor", 25))
            : d.Bands;
        var qb = d.QualityBar;
        if (s.TryGetProperty("qualityBar", out var q))
        {
            var offsets = q.TryGetProperty("offsets", out var o) ? o.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetDouble(), StringComparer.Ordinal) : qb.Offsets;
            var factors = q.TryGetProperty("lensGroupFactors", out var f) ? f.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetDouble(), StringComparer.Ordinal) : qb.LensGroupFactors;
            qb = new QualityBarRules(offsets, factors, D(q, "exemplaryCeiling", qb.ExemplaryCeiling), D(q, "poorFloor", qb.PoorFloor));
        }
        return new ScoringParameters(D(s, "withinLensQ", d.WithinLensQ), D(s, "acrossLensQ", d.AcrossLensQ), D(s, "criticalGate", d.CriticalGate), arch, bands, qb);
    }
}

public sealed record ArchitectureSurface(int MinProjects, int MinProductionLoc, double LowSurfaceCap);

/// <summary>Cutlines in the catalog's own vocabulary (exemplary/healthy/fair/poor = Exemplary/Strong/Adequate/Weak).</summary>
public sealed record BandCutlines(double Exemplary, double Healthy, double Fair, double Poor);

public sealed record QualityBarRules(
    IReadOnlyDictionary<string, double> Offsets,
    IReadOnlyDictionary<string, double> LensGroupFactors,
    double ExemplaryCeiling, double PoorFloor);
