using System.Diagnostics;
using Cai.Reference.Scoring;

namespace Cai.Reference.Engine;

public static class ScanCommand
{
    public static int Run(Args a)
    {
        if (a.Positionals.Count < 1) throw new ArgumentException("missing repository path");
        var root = Path.GetFullPath(a.Positionals[0]);
        if (!Directory.Exists(root)) throw new ArgumentException($"no such directory: {root}");
        var rubrics = a.RubricsDir;
        var version = a.Opt("rubric") ?? RubricCatalog.LatestVersion(rubrics);
        var catalog = RubricCatalog.LoadFromArchive(rubrics, version);
        var dataDir = a.Opt("data") ?? DataSnapshots.FindDir() ?? throw new ArgumentException("no data/ snapshots found; pass --data DIR");
        var outDir = a.Opt("out") ?? Path.Combine(root, ".cai-ref");
        var quiet = a.Has("quiet");

        var sw = Stopwatch.StartNew();
        void Log(string s) { if (!quiet) Console.Error.WriteLine($"[{sw.Elapsed.TotalSeconds,6:F1}s] {s}"); }

        Log($"opening {root}");
        var repo = Repository.Open(root);
        Log($"{repo.Files.Count} tracked files, {repo.Projects.Count} projects, head {repo.HeadCommit[..Math.Min(8, repo.HeadCommit.Length)]}");
        var ws = Workspace.Build(repo, a.Opt("nuget-packages"));
        Log($"compiled {ws.Projects.Count} projects: {ws.ProductionLoc} production LOC, {ws.TestLoc} test LOC");
        var history = GitHistory.Load(repo);
        Log($"{history.Commits.Count} commits on HEAD");
        var data = DataSnapshots.Load(dataDir);
        var ctx = new ScanContext { Repo = repo, Catalog = catalog, Workspace = ws, History = history, Data = data, NuGetPackagesDir = a.Opt("nuget-packages") };

        foreach (var detector in Detectors.All)
        {
            detector(ctx);
            Log($"{detector.Method.DeclaringType!.Name}.{detector.Method.Name}: {ctx.Findings.Count} findings, {ctx.Measurements.Count} measurements so far");
        }
        Coverage.FillNotMeasured(ctx);
        // a posture dimension's "missing <item>" findings are only worth a reader's time when the posture is poor;
        // otherwise the score carries the information and a located plant would have its own concrete finding
        var scores = ctx.Measurements.ToDictionary(m => m.Id, m => m.Score, StringComparer.Ordinal);
        ctx.Findings.RemoveAll(f => f.File is null && Rules.PostureConcepts.Contains(f.RuleId) && scores.TryGetValue(f.Dimension, out var sc) && sc >= 5);

        Directory.CreateDirectory(outDir);
        var evidence = Output.BuildEvidence(ctx, a.Opt("quality-bar"));
        File.WriteAllText(Path.Combine(outDir, "evidence.json"), evidence.ToJson());
        File.WriteAllText(Path.Combine(outDir, "findings.sarif"), Output.Sarif(ctx));
        File.WriteAllText(Path.Combine(outDir, "scores.json"), Output.Scores(ctx));
        var result = CaiScorer.Score(evidence, catalog);
        Log($"wrote {outDir}");
        Console.WriteLine($"CAI {result.Cai:F1} ({result.Band})  ·  rubric {catalog.RubricVersion}  ·  {ctx.Findings.Count} findings  ·  {ctx.Measurements.Count} measured, {evidence.Extra!["notMeasured"].GetArrayLength()} not measured");
        foreach (var l in result.Lenses) Console.WriteLine($"  {l.Lens,-22} {l.Score,6:F1}  {l.Band + (l.CriticalGated ? "*" : ""),-12} {l.ItemCount,4} {l.OwaWeight,8:F3}");
        return 0;
    }
}

public static class MappingCommand
{
    public static int Run(Args a)
    {
        var rubrics = a.RubricsDir;
        var catalog = RubricCatalog.LoadFromArchive(rubrics, a.Opt("rubric") ?? RubricCatalog.LatestVersion(rubrics));
        IEnumerable<string> taxonomy = Enumerable.Empty<string>();
        if (a.Opt("taxonomy") is { } t)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(t));
            taxonomy = doc.RootElement.GetProperty("concepts").EnumerateArray().Select(c => c.GetProperty("id").GetString()!).ToList();
        }
        var json = Output.Mapping(catalog, taxonomy, Coverage.UnmappedConceptReasons);
        if (a.Opt("out") is { } o) File.WriteAllText(o, json); else Console.Write(json);
        return 0;
    }
}
