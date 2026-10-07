using System.Globalization;
using System.Text.Json;
using Cai.Reference.Scoring;

namespace Cai.Reference;

public static class Program
{
    public static int Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
        try
        {
            var a = new Args(args);
            return a.Command switch
            {
                "score" => ScoreCommand.Run(a, verify: false),
                "verify" => ScoreCommand.Run(a, verify: true),
                "scan" => Cai.Reference.Engine.ScanCommand.Run(a),
                "mapping" => Cai.Reference.Engine.MappingCommand.Run(a),
                _ => Usage(),
            };
        }
        catch (Exception ex) when (ex is ArgumentException or FileNotFoundException or InvalidOperationException or JsonException or IOException)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            return 2;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("""
            cai-ref — CAI reference implementation (clean room)

              cai-ref scan   <repo> [--rubrics DIR] [--rubric VERSION] [--out DIR] [--quality-bar BAR] [--nuget-packages DIR]
                                                 measure a C#/.NET repository → evidence.json, findings.sarif, scores.json
              cai-ref score  <evidence.json> [--rubrics DIR] [--json]      fold an evidence bundle to a CAI
              cai-ref verify <evidence.json> [--rubrics DIR] [--expect N]  reproduce a claimed headline (exit 1 on mismatch)
              cai-ref mapping [--out FILE]                                scanner-benchmark mapping for this engine

              --rubrics defaults to the catalogs shipped with this tool (rubrics/).
            """);
        return 2;
    }
}

/// <summary>Minimal argument parser: first positional is the command, then positionals and --name value options.</summary>
public sealed class Args
{
    public string Command { get; }
    public List<string> Positionals { get; } = new();
    public Dictionary<string, string?> Options { get; } = new(StringComparer.Ordinal);

    public Args(string[] args)
    {
        Command = args.Length > 0 ? args[0] : "";
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                var name = args[i][2..];
                var eq = name.IndexOf('=');
                if (eq >= 0) { Options[name[..eq]] = name[(eq + 1)..]; continue; }
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) Options[name] = args[++i];
                else Options[name] = null;
            }
            else Positionals.Add(args[i]);
        }
    }

    public string? Opt(string name) => Options.GetValueOrDefault(name);
    public bool Has(string name) => Options.ContainsKey(name);

    public string RubricsDir => Opt("rubrics") ?? DefaultRubricsDir();

    private static string DefaultRubricsDir()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "rubrics");
            if (Directory.Exists(candidate) && Directory.GetDirectories(candidate, "rubric-*").Length > 0) return candidate;
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        throw new ArgumentException("no rubric archive found; pass --rubrics DIR");
    }
}

public static class ScoreCommand
{
    public static int Run(Args a, bool verify)
    {
        if (a.Positionals.Count < 1) throw new ArgumentException("missing evidence file");
        var bundle = EvidenceBundle.Load(a.Positionals[0]);
        var catalog = RubricCatalog.LoadFromArchive(a.RubricsDir, bundle.RubricVersion);
        var result = CaiScorer.Score(bundle, catalog);
        if (verify)
        {
            var claimed = a.Opt("expect") is { } e ? double.Parse(e, CultureInfo.InvariantCulture) : bundle.HeadlineScore
                ?? throw new ArgumentException("bundle carries no headlineScore; pass --expect N");
            var delta = Math.Abs(result.Cai - claimed);
            if (delta <= 0.5)
            {
                Console.WriteLine($"✓ reproduced: CAI {result.Cai:F2} ({result.Band}) under rubric {catalog.RubricVersion} (claimed {claimed:F2}, Δ{delta:F2})");
                return 0;
            }
            Console.WriteLine($"✗ MISMATCH: evidence folds to {result.Cai:F2} but the claim is {claimed:F2} (Δ{delta:F2} > 0.50)");
            return 1;
        }
        if (a.Has("json")) { Console.WriteLine(ToJson(result)); return 0; }
        Console.WriteLine($"CAI {result.Cai:F1} ({result.Band})  ·  rubric {catalog.RubricVersion}");
        Console.WriteLine();
        Console.WriteLine("  lens                    score  band          dims   weight   contrib");
        foreach (var l in result.Lenses)
            Console.WriteLine($"  {l.Lens,-22} {l.Score,6:F1}  {l.Band + (l.CriticalGated ? "*" : ""),-12} {l.ItemCount,4} {l.OwaWeight,8:F3} {l.Contribution,9:F2}");
        if (result.Lenses.Any(l => l.CriticalGated))
            Console.WriteLine($"\n  * band capped at Adequate — a contributor below {catalog.Scoring.CriticalGate}/10 critical-gates the lens: " +
                string.Join("; ", result.Lenses.Where(l => l.CriticalGated).Select(l => $"{l.Lens} ({string.Join(", ", l.CriticalContributors)})")));
        if (result.CoherenceNote is not null) Console.WriteLine($"\n  {result.CoherenceNote}");
        return 0;
    }

    public static string ToJson(CaiResult r)
    {
        var o = new
        {
            cai = Math.Round(r.Cai, 2, MidpointRounding.AwayFromZero),
            caiExact = r.Cai,
            band = r.Band.ToString(),
            rubricVersion = r.RubricVersion,
            coherenceNote = r.CoherenceNote,
            lenses = r.Lenses.Select(l => new
            {
                lens = l.Lens, score = Math.Round(l.Score, 2, MidpointRounding.AwayFromZero), band = l.Band.ToString(),
                weight = Math.Round(l.OwaWeight, 4, MidpointRounding.AwayFromZero), contribution = Math.Round(l.Contribution, 2, MidpointRounding.AwayFromZero),
                criticalGated = l.CriticalGated, criticalContributors = l.CriticalContributors, itemCount = l.ItemCount,
            }),
            categories = r.Categories.Select(c => new { category = c.Category, lens = c.Lens, score = Math.Round(c.Score, 2, MidpointRounding.AwayFromZero), dimensionCount = c.DimensionCount }),
        };
        return JsonSerializer.Serialize(o, new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
    }
}
