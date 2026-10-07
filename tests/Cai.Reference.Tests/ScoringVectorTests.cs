using System.Text.Json;
using Cai.Reference.Scoring;

namespace Cai.Reference.Tests;

/// <summary>
/// Conformance vectors: evidence bundles with the dimension/lens/headline/band the published algorithm produces.
/// Expected values were recorded from the published `cai` CLI used as a black-box oracle (tools/make-vectors.py), or
/// typed from the worked examples in the founding explainer. Scores compare at 1 dp, weights at 3 dp — the oracle's
/// display precision — and bands exactly.
/// </summary>
public class ScoringVectorTests
{
    public static readonly string VectorsDir = Path.Combine(AppContext.BaseDirectory, "Vectors");
    public static readonly string RubricsDir = FindRubrics();

    private static string FindRubrics()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "rubrics"))) dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir!, "rubrics");
    }

    public static IEnumerable<object[]> Vectors() =>
        Directory.GetFiles(VectorsDir, "*.json").OrderBy(f => f, StringComparer.Ordinal).Select(f => new object[] { Path.GetFileName(f) });

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Reproduces_the_published_algorithm(string file)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(VectorsDir, file)));
        var root = doc.RootElement;
        var evidence = EvidenceBundle.Parse(root.GetProperty("evidence").GetRawText());
        if (root.TryGetProperty("expectedError", out var e) && e.GetBoolean())
        {
            Assert.ThrowsAny<Exception>(() => CaiScorer.Score(evidence, RubricCatalog.LoadFromArchive(RubricsDir, evidence.RubricVersion)));
            return;
        }
        var catalog = RubricCatalog.LoadFromArchive(RubricsDir, evidence.RubricVersion);
        var result = CaiScorer.Score(evidence, catalog);
        var expected = root.GetProperty("expected");
        Assert.Equal(expected.GetProperty("cai").GetDouble(), result.Cai, 1);
        Assert.Equal(expected.GetProperty("band").GetString(), result.Band.ToString());
        var lenses = expected.GetProperty("lenses");
        Assert.Equal(lenses.EnumerateObject().Select(p => p.Name).OrderBy(x => x), result.Lenses.Select(l => l.Lens).OrderBy(x => x));
        foreach (var l in result.Lenses)
        {
            var exp = lenses.GetProperty(l.Lens);
            Assert.Equal(exp.GetProperty("score").GetDouble(), l.Score, 1);
            if (exp.GetProperty("band").ValueKind == JsonValueKind.String) Assert.Equal(exp.GetProperty("band").GetString(), l.Band.ToString());
            Assert.Equal(exp.GetProperty("gated").GetBoolean(), l.CriticalGated);
            Assert.Equal(exp.GetProperty("items").GetInt32(), l.ItemCount);
            if (exp.GetProperty("weight").ValueKind == JsonValueKind.Number) Assert.Equal(exp.GetProperty("weight").GetDouble(), l.OwaWeight, 3);
        }
    }

    [Fact]
    public void Owa_weights_match_the_explainer()
    {
        var w = CaiScorer.OwaWeights(3, 0.55);
        Assert.Equal(0.5398, w[0], 4); Assert.Equal(0.2969, w[1], 4); Assert.Equal(0.1633, w[2], 4);
        var w2 = CaiScorer.OwaWeights(2, 0.55);
        Assert.Equal(0.6452, w2[0], 4); Assert.Equal(0.3548, w2[1], 4);
    }

    [Fact]
    public void Bands_are_cut_on_the_unrounded_number()
    {
        var c = ScoringParameters.Default.Bands;
        Assert.Equal(Band.Strong, CaiScorer.BandOf(70.0, c));
        Assert.Equal(Band.Adequate, CaiScorer.BandOf(69.999, c));
        Assert.Equal(Band.Weak, CaiScorer.BandOf(49.995, c));
        Assert.Equal(Band.Critical, CaiScorer.BandOf(24.9, c));
        Assert.Equal(Band.Exemplary, CaiScorer.BandOf(90, c));
    }

    [Fact]
    public void Quality_bar_shifts_lens_cutlines_by_group()
    {
        var p = ScoringParameters.Default;
        Assert.Equal(70 - 18 * 0.4, CaiScorer.CutlinesFor("codeHealth", "prototype", p).Healthy, 6);
        Assert.Equal(70 - 18 * 0.25, CaiScorer.CutlinesFor("securityCompliance", "prototype", p).Healthy, 6);
        Assert.Equal(70 - 18 * 1.0, CaiScorer.CutlinesFor("maturity", "prototype", p).Healthy, 6);
        Assert.Equal(70 - 18 * 0.7, CaiScorer.CutlinesFor("accessibility", "prototype", p).Healthy, 6);
        Assert.Equal(70, CaiScorer.CutlinesFor("codeHealth", "bogus", p).Healthy, 6);
        Assert.Equal(70, CaiScorer.CutlinesFor("codeHealth", null, p).Healthy, 6);
    }
}
