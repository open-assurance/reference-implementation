namespace Cai.Reference.Scoring;

public enum Band { Critical = 0, Weak = 1, Adequate = 2, Strong = 3, Exemplary = 4 }

public sealed record LensResult(
    string Lens, double Score, Band Band, double OwaWeight, double Contribution,
    bool CriticalGated, IReadOnlyList<string> CriticalContributors, int ItemCount, bool SurfaceCapped);

public sealed record CategoryResult(string Category, string Lens, double Score, int DimensionCount);

public sealed record CaiResult(
    double Cai, Band Band, Band UncappedBand, IReadOnlyList<LensResult> Lenses, IReadOnlyList<CategoryResult> Categories,
    string? CoherenceNote, string RubricVersion)
{
    public double Rounded(int decimals = 2) => Math.Round(Cai, decimals, MidpointRounding.AwayFromZero);
}

/// <summary>
/// The open, deterministic fold from an evidence bundle to a CAI under a named rubric. Implemented from the
/// published text (codeassuranceindex.info/spec, the founding explainer, ADR-0002/0004); every place the text
/// left open and a black-box probe of the published scorer decided is marked "oracle:" and listed in SPEC-GAPS.md.
/// </summary>
public static class CaiScorer
{
    public const string ArchitectureLens = "architecture";

    /// <summary>oracle: the lens → quality-bar group map is published in no catalog; derived from band flips at known offsets.</summary>
    public static string LensGroup(string lens) => lens switch
    {
        "codeHealth" or "architecture" => "foundational",
        "securityCompliance" => "safety",
        "maturity" or "productionReadiness" => "operational",
        _ => "default",
    };

    /// <summary>Worst-first Yager OWA weights for n inputs: ∝ q^rank over ascending scores, renormalised.</summary>
    public static double[] OwaWeights(int n, double q)
    {
        var w = new double[n];
        double sum = 0;
        for (var i = 0; i < n; i++) { w[i] = Math.Pow(q, i); sum += w[i]; }
        for (var i = 0; i < n; i++) w[i] /= sum;
        return w;
    }

    public static Band BandOf(double score, BandCutlines c) =>
        score >= c.Exemplary ? Band.Exemplary : score >= c.Healthy ? Band.Strong : score >= c.Fair ? Band.Adequate : score >= c.Poor ? Band.Weak : Band.Critical;

    /// <summary>Lens cutlines under a declared quality bar: offset × lens-group factor, exemplary line capped, poor line floored.</summary>
    public static BandCutlines CutlinesFor(string lens, string? qualityBar, ScoringParameters p)
    {
        var offset = qualityBar is not null && p.QualityBar.Offsets.TryGetValue(qualityBar, out var o) ? o : 0; // oracle: unknown bar ⇒ production
        var factor = p.QualityBar.LensGroupFactors.TryGetValue(LensGroup(lens), out var f) ? f : p.QualityBar.LensGroupFactors.GetValueOrDefault("default", 1);
        var shift = offset * factor;
        var b = p.Bands;
        return new BandCutlines(
            Math.Min(b.Exemplary + shift, p.QualityBar.ExemplaryCeiling), b.Healthy + shift, b.Fair + shift,
            Math.Max(b.Poor + shift, p.QualityBar.PoorFloor));
    }

    public static CaiResult Score(EvidenceBundle bundle, RubricCatalog catalog)
    {
        if (!string.Equals(bundle.RubricVersion, catalog.RubricVersion, StringComparison.Ordinal))
            throw new ArgumentException($"evidence names rubric '{bundle.RubricVersion}' but the catalog is '{catalog.RubricVersion}'.");
        var p = catalog.Scoring;

        // 1. eligible dimensions → categories (confidence-weighted mean of coverage-scaled scores)
        var catAcc = new Dictionary<(string lens, string category), (double num, double den, int n)>();
        var gateHits = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var d in bundle.Dimensions)
        {
            if (d.Advisory == true) continue;                    // advisory never enters (bundle flag decides — oracle)
            var conf = d.Confidence ?? 0;
            if (conf <= 0) continue;                              // confidence 0 ⇒ absent, never a raw zero
            if (d.Score is < 0 or > 10) throw new ArgumentException($"Dimension '{d.Id}' score {d.Score} is outside 0–10.");
            var cov = d.Coverage ?? 1;
            if (cov is < 0 or > 1) throw new ArgumentException($"Dimension '{d.Id}' coverage {cov} is outside 0–1.");
            var catDim = catalog.Find(d.Id);
            if (catDim?.Category is not null && !string.Equals(catDim.Category, d.Category, StringComparison.Ordinal))
                throw new ArgumentException($"Dimension '{d.Id}' is declared in category '{d.Category}', but rubric '{catalog.RubricVersion}' publishes it in '{catDim.Category}'. A dimension's category decides which other dimensions it averages with (ADR-0004).");
            var lens = catalog.LensOfCategory(d.Category) ?? catDim?.Lens
                ?? throw new ArgumentException($"Unknown dimension category '{d.Category}'.");
            var effective = d.Score * cov;                        // coverage changes the value (coverage 0 ⇒ a measured zero — oracle)
            var key = (lens, d.Category);
            var acc = catAcc.GetValueOrDefault(key);
            catAcc[key] = (acc.num + effective * conf, acc.den + conf, acc.n + 1);
            if (effective < p.CriticalGate) Gate(gateHits, lens, d.Id);
        }
        var categories = catAcc.OrderBy(k => k.Key.lens, StringComparer.Ordinal).ThenBy(k => k.Key.category, StringComparer.Ordinal)
            .Select(k => new CategoryResult(k.Key.category, k.Key.lens, 10 * k.Value.num / k.Value.den, k.Value.n)).ToList();

        // 2. meta-dimensions enter their declared lens directly (oracle: the bundle's lens wins over the catalog's)
        var lensInputs = new Dictionary<string, List<double>>(StringComparer.Ordinal);
        foreach (var c in categories) Add(lensInputs, c.Lens, c.Score);
        foreach (var m in bundle.MetaDimensions)
        {
            if (m.Advisory == true || m.Score is null) continue;
            if (m.Score is < 0 or > 10) throw new ArgumentException($"Meta-dimension '{m.Id}' score {m.Score} is outside 0–10.");
            Add(lensInputs, m.Lens, m.Score.Value * 10);
            if (m.Score.Value < p.CriticalGate) Gate(gateHits, m.Lens, m.Id);
        }

        // 3. lens scores (within-lens OWA), or the thin lens-only fallback
        var lensScores = new List<(string lens, double score, int items, bool capped)>();
        Dictionary<string, double>? givenWeights = null;
        if (lensInputs.Count > 0)
        {
            foreach (var (lens, inputs) in lensInputs.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                var score = Owa(inputs, p.WithinLensQ);
                var capped = false;
                if (lens == ArchitectureLens)
                {
                    if (bundle.AnalyzableProjects == 0) continue;  // no project to analyse ⇒ left out
                    if (bundle.AnalyzableProjects < p.ArchitectureSurface.MinProjects && bundle.ProductionLoc < p.ArchitectureSurface.MinProductionLoc)
                    { score = Math.Min(score, p.ArchitectureSurface.LowSurfaceCap); capped = true; } // oracle: AND, strict <
                }
                lensScores.Add((lens, score, inputs.Count, capped));
            }
        }
        else if (bundle.Lenses is { Count: > 0 })
        {
            // oracle: the thin fallback keeps the bundle's own owaWeights when they sum to 1 (±0.01); otherwise they are
            // recomputed. The architecture surface rules are not applied on this path.
            foreach (var l in bundle.Lenses) lensScores.Add((l.Lens, l.Score, 0, false));
            var sum = bundle.Lenses.Sum(l => l.OwaWeight);
            if (Math.Abs(sum - 1) <= 0.01) givenWeights = bundle.Lenses.ToDictionary(l => l.Lens, l => l.OwaWeight, StringComparer.Ordinal);
        }
        else throw new ArgumentException("Evidence bundle carries no dimensions, meta-dimensions or lens scores.");
        if (lensScores.Count == 0) throw new ArgumentException("No measured lenses to score.");

        // 4. headline: across-lens OWA over the measured lenses only
        // oracle: equal scores keep the catalog's lens order (unknown lenses after it, in bundle order)
        var lensIndex = catalog.Lenses.Select((l, i) => (l.Key, i)).ToDictionary(x => x.Key, x => x.i, StringComparer.Ordinal);
        var ordered = lensScores.Select((l, i) => (l, i)).OrderBy(x => x.l.score)
            .ThenBy(x => lensIndex.TryGetValue(x.l.lens, out var ix) ? ix : int.MaxValue).ThenBy(x => x.i).Select(x => x.l).ToList();
        var weights = OwaWeights(ordered.Count, p.AcrossLensQ);
        if (givenWeights is not null) weights = ordered.Select(l => givenWeights[l.lens]).ToArray();
        double cai = 0;
        var lensResults = new List<LensResult>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var (lens, score, items, capped) = ordered[i];
            var contribution = score * weights[i];
            cai += contribution;
            var gated = gateHits.TryGetValue(lens, out var hits) && hits.Count > 0;
            var band = BandOf(score, CutlinesFor(lens, bundle.QualityBar, p));
            if (gated && band > Band.Adequate) band = Band.Adequate;  // the gate caps the band, never the number
            lensResults.Add(new LensResult(lens, score, band, weights[i], contribution, gated, hits ?? new List<string>(), items, capped));
        }

        // 5. headline band: baseline cutlines, never more than one band above the weakest category (oracle: categories only, baseline cutlines)
        var uncapped = BandOf(cai, p.Bands);
        var band2 = uncapped;
        string? note = null;
        if (categories.Count > 0)
        {
            var weakest = categories.Min(c => BandOf(c.Score, p.Bands));
            var ceiling = (Band)Math.Min((int)weakest + 1, (int)Band.Exemplary);
            if (band2 > ceiling)
            {
                var w = categories.OrderBy(c => c.Score).First();
                note = $"headline band held at {ceiling}: weakest category '{w.Category}' reads {weakest}";
                band2 = ceiling;
            }
        }
        lensResults = lensResults.OrderByDescending(l => l.Contribution).ThenBy(l => lensIndex.TryGetValue(l.Lens, out var ix) ? ix : int.MaxValue).ToList();
        return new CaiResult(cai, band2, uncapped, lensResults, categories, note, catalog.RubricVersion);
    }

    /// <summary>Reproduce a claimed headline: |fold − claim| ≤ tolerance (the published verifier uses 0.5).</summary>
    public static (bool ok, double folded, double delta) Verify(EvidenceBundle bundle, RubricCatalog catalog, double claimed, double tolerance = 0.5)
    {
        var r = Score(bundle, catalog);
        var delta = Math.Abs(r.Cai - claimed);
        return (delta <= tolerance, r.Cai, delta);
    }

    private static double Owa(List<double> inputs, double q)
    {
        var sorted = inputs.OrderBy(x => x).ToArray();
        var w = OwaWeights(sorted.Length, q);
        double s = 0;
        for (var i = 0; i < sorted.Length; i++) s += sorted[i] * w[i];
        return s;
    }

    private static void Add(Dictionary<string, List<double>> d, string lens, double v)
    {
        if (!d.TryGetValue(lens, out var l)) d[lens] = l = new List<double>();
        l.Add(v);
    }

    private static void Gate(Dictionary<string, List<string>> d, string lens, string id)
    {
        if (!d.TryGetValue(lens, out var l)) d[lens] = l = new List<string>();
        l.Add(id);
    }
}
