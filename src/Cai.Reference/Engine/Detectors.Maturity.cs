using System.Text.RegularExpressions;

namespace Cai.Reference.Engine;

/// <summary>Maturity: D15 hotspots, D16 bus factor, D34 knowledge freshness (git); M1 README, M2 architecture docs, M3 structure; D18 solution shape.</summary>
public static class Maturity
{
    public static void Run(ScanContext ctx) { History(ctx); Docs(ctx); Structure(ctx); }

    private static void History(ScanContext ctx)
    {
        var h = ctx.History;
        if (!h.Available || h.Commits.Count < 5) { foreach (var d in new[] { "D15", "D16", "D34" }) ctx.Skip(d, h.Available ? $"only {h.Commits.Count} commits on HEAD: too little history to read behaviour from" : "no git history available"); return; }
        var head = ctx.Repo.HeadDate == default ? h.Commits[0].Date : ctx.Repo.HeadDate;
        var prod = ctx.Workspace.ProductionTrees.Select(t => (file: ctx.Workspace.RelPath(t.tree.FilePath), t.tree)).ToList();
        if (prod.Count == 0) { foreach (var d in new[] { "D15", "D16", "D34" }) ctx.Skip(d, "no production source files"); return; }
        var prodFiles = prod.Select(p => p.file).ToHashSet(StringComparer.Ordinal);
        var complexity = prod.ToDictionary(p => p.file, p => Cs.MethodLike(p.tree.GetRoot()).Select(Cs.BodyOf).Where(b => b is not null).Select(b => CodeHealth.Cyclomatic(b!)).DefaultIfEmpty(1).Max(), StringComparer.Ordinal);
        var loc = prod.ToDictionary(p => p.file, p => Workspace.CountLoc(p.tree), StringComparer.Ordinal);

        // D15: churn in the 180 days before HEAD × the most complex method in the file
        var window = head.AddDays(-180);
        var recent = h.Commits.Where(c => c.Date >= window).ToList();
        var churn = prodFiles.ToDictionary(f => f, f => recent.Count(c => c.Files.Any(x => x.path == f)), StringComparer.Ordinal);
        var touches = churn.Values.Sum();
        var hotspots = new List<Finding>();
        foreach (var f in prodFiles.OrderBy(f => f, StringComparer.Ordinal))
        {
            var cx = complexity[f]; var ch = churn[f];
            if (ch >= 4 && cx >= 10 && touches > 0 && (double)ch / touches >= 0.08 || ch >= 6 && cx >= 15)
                hotspots.Add(new Finding("churn-complexity-hotspot", "D15", $"{Path.GetFileName(f)} changed in {ch} of {recent.Count} commits over the last 180 days and holds a method of complexity {cx}: where the next defect lands", f, null, null, cx >= 20 ? 2 : 1));
        }
        foreach (var f in hotspots) ctx.Add(f);
        ctx.Measure("D15", Shape.FromFindings(hotspots, Math.Max(1.0, prodFiles.Count / 25.0)), note: $"{hotspots.Count} hotspot(s) among {prodFiles.Count} production files; {recent.Count} commits in the window");

        // D16: knowledge concentration — share of production LOC in files ≥ 80 % one author
        var authorsPerFile = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        foreach (var c in h.Commits) foreach (var (path, added, _) in c.Files)
        {
            if (!prodFiles.Contains(path)) continue;
            if (!authorsPerFile.TryGetValue(path, out var d)) authorsPerFile[path] = d = new(StringComparer.Ordinal);
            d[c.Author] = d.GetValueOrDefault(c.Author) + Math.Max(1, added);
        }
        var contributors = h.Commits.Select(c => c.Author).Distinct().Count();
        var lastActive = h.Commits.GroupBy(c => c.Author).ToDictionary(g => g.Key, g => g.Max(c => c.Date), StringComparer.Ordinal);
        var activeCutoff = head.AddDays(-90);   // the same silence that makes knowledge stale (D34) makes its owner "gone"
        var topShare = h.Commits.GroupBy(c => c.Author).Max(g => g.Count()) / (double)h.Commits.Count;
        if (contributors < 2 || topShare >= 0.9) ctx.Skip("D16", $"one author wrote {topShare:P0} of the history: concentration is total by construction and says nothing about a team");
        else
        {
            var silos = new List<Finding>(); double soloLoc = 0, allLoc = 0;
            var graph = ctx.Graph;
            foreach (var (f, d) in authorsPerFile.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                var size = loc.GetValueOrDefault(f, 1);
                // significant = code others build on (fan-in ≥ 2) or code with real logic in it; a leaf utility pinned by its tests is not a silo
                var significant = size >= 40 && (graph.FanIn(f) >= 2 || complexity.GetValueOrDefault(f) >= 8);
                if (!significant) continue;
                allLoc += size;
                var top = d.MaxBy(kv => kv.Value);
                // a silo is knowledge held by ONE PERSON WHO IS STILL HERE: every commit by one author, and that author active;
                // a departed author's file is orphaned (D34), not concentrated
                if (d.Count == 1 && lastActive[top.Key] >= activeCutoff)
                {
                    soloLoc += size;
                    silos.Add(new Finding("knowledge-concentration", "D16", $"{Path.GetFileName(f)} ({size} LOC, used by {graph.FanIn(f)} other files) has been written and changed by one contributor only", f, null, null, size >= 200 ? 2 : 1));
                }
            }
            foreach (var f in silos) ctx.Add(f);
            var share = allLoc == 0 ? 0 : soloLoc / allLoc;
            ctx.Measure("D16", Shape.FromShare(Math.Max(0, 1 - 2 * share)), note: $"{share:P0} of significant production code is single-owner; {silos.Count} silo(s); {contributors} contributors");
        }

        // D34: files last changed > 365 days before HEAD whose every author has been silent for 180 days
        // thresholds scale with the history: a file is orphaned when it has sat untouched for the longer of 180 days and half
        // the history, and every author who ever touched it has been silent for 90 days
        var span = (head - h.Commits.Min(c => c.Date)).TotalDays;
        if (span < 180) { ctx.Skip("D34", $"history spans only {span:F0} days: too short for knowledge to have gone stale"); return; }
        var staleAfter = Math.Max(180, span / 2); var silentAfter = 90;
        var stale = new List<Finding>(); double staleLoc = 0, total = 0;
        foreach (var f in prodFiles.OrderBy(f => f, StringComparer.Ordinal))
        {
            var touching = h.Commits.Where(c => c.Files.Any(x => x.path == f)).ToList();
            if (touching.Count == 0) continue;
            var size = loc.GetValueOrDefault(f, 1); total += size;
            var last = touching.Max(c => c.Date);
            if (last < head.AddDays(-staleAfter) && touching.Select(c => c.Author).Distinct().All(a => lastActive[a] < head.AddDays(-silentAfter)))
            {
                staleLoc += size;
                if (size >= 40) stale.Add(new Finding("knowledge-freshness", "D34", $"{Path.GetFileName(f)} was last changed {(head - last).Days} days before HEAD and everyone who touched it has been silent for {silentAfter}+ days", f, null, null, size >= 200 ? 2 : 1));
            }
        }
        foreach (var f in stale) ctx.Add(f);
        ctx.Measure("D34", Shape.FromShare(Math.Max(0, 1 - (total == 0 ? 0 : staleLoc / total) * 2)), note: $"{(total == 0 ? 0 : staleLoc / total):P0} of production code is orphaned ({stale.Count} file(s))");
    }

    private static void Docs(ScanContext ctx)
    {
        var repo = ctx.Repo;
        var readme = repo.Files.FirstOrDefault(f => Regex.IsMatch(f, @"^README(\.md|\.rst|\.txt)?$", RegexOptions.IgnoreCase));
        if (readme is null) { ctx.Add(new Finding("readme-quality", "M1", "no README at the repository root", null, null, null, 2)); ctx.Measure("M1", 0, note: "no README"); }
        else
        {
            var text = repo.Text(readme);
            var words = Regex.Matches(text, @"\b\w+\b").Count;
            var headings = Regex.Matches(text, @"(?m)^#{1,6}\s+(.+)$").Select(m => m.Groups[1].Value.ToLowerInvariant()).ToList();
            bool Has(string re) => headings.Any(h => Regex.IsMatch(h, re));
            var checks = new (string name, bool ok)[]
            {
                ("substantive (≥ 120 words)", words >= 120),
                ("build / run instructions", Has("build|run|getting started|install|setup|usage|quick ?start|develop") || Regex.IsMatch(text, @"(?i)dotnet (build|run)")),
                ("testing section", Has("test") || Regex.IsMatch(text, @"(?i)dotnet test")),
                ("architecture / design pointer", Has("architecture|design|structure|how it works|overview") || Regex.IsMatch(text, @"(?i)docs/architecture|\bADR")),
                ("configuration or deployment notes", Has("config|deploy|environment|settings|docker|kubernetes|operations") || Regex.IsMatch(text, @"(?i)appsettings|environment variable")),
            };
            foreach (var (name, ok) in checks) if (!ok) ctx.Add(new Finding("readme-quality", "M1", $"README lacks {name}", readme, 1, 1, 0.5));
            ctx.Measure("M1", Shape.FromChecklist(checks.Count(c => c.ok), checks.Length), note: $"{words} words; {checks.Count(c => c.ok)}/{checks.Length}: {string.Join(", ", checks.Where(c => c.ok).Select(c => c.name))}");
        }
        var adrs = repo.Files.Where(f => Regex.IsMatch(f, @"(?i)(^|/)(docs?/)?(adrs?|decisions|architecture/decisions|design/decisions)/[^/]+\.md$") || Regex.IsMatch(Path.GetFileName(f), @"(?i)^adr[-_]?\d+.*\.md$")).ToList();
        var structured = adrs.Count(a => { var t = repo.Text(a); return Regex.IsMatch(t, @"(?im)^#+\s*(context|decision|consequences)") && Regex.IsMatch(t, @"(?im)^#+\s*decision") && Regex.IsMatch(t, @"(?im)^#+\s*consequences"); });
        var archDoc = repo.Files.FirstOrDefault(f => Regex.IsMatch(f, @"(?i)(^|/)(docs?/)?(architecture|design|overview|system)[\w-]*\.md$"));
        var diagram = repo.Files.Any(f => Regex.IsMatch(f, @"(?i)\.(puml|plantuml|drawio|mmd|mermaid)$") || Regex.IsMatch(f, @"(?i)\.(svg|png)$") && Regex.IsMatch(f, @"(?i)docs?/|diagram|architecture|c4")) || archDoc is not null && Regex.IsMatch(repo.Text(archDoc), @"```\s*mermaid|@startuml|C4Context|C4Container|!\[[^\]]*\]\([^)]*\.(png|svg)\)");
        var m2 = new (string name, bool ok)[] { ("architecture decision records", adrs.Count > 0), ("ADRs with context / decision / consequences", adrs.Count > 0 && structured * 2 >= adrs.Count), ("an architecture overview document", archDoc is not null), ("a diagram of the shape", diagram) };
        foreach (var (name, ok) in m2) if (!ok) ctx.Add(new Finding("architecture-documentation", "M2", $"missing {name}", null, null, null, 0.5));
        ctx.Measure("M2", Shape.FromChecklist(m2.Count(c => c.ok), m2.Length), note: $"{adrs.Count} ADR(s), {structured} structured; overview: {archDoc ?? "none"}");
        ctx.Facts["adrCount"] = adrs.Count.ToString();
    }

    private static readonly Regex SrcDir = new(@"(^|/)(src|source|lib|libs|app|apps|services|packages|modules)(/|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static void Structure(ScanContext ctx)
    {
        var repo = ctx.Repo; var projects = repo.Projects;
        if (projects.Count == 0) { ctx.Skip("M3", "no C# projects"); ctx.Skip("D18", "no C# projects"); return; }
        var prod = projects.Where(p => p.IsProduction).ToList(); var tests = projects.Where(p => p.Role == ProjectRole.Test).ToList();
        var srcSeparated = prod.All(p => SrcDir.IsMatch(p.Directory) || Regex.IsMatch(p.Directory, @"(?i)^(tools?|samples?|examples?)(/|$)")) || projects.Count == 1;
        var testsSeparated = tests.Count == 0 || tests.All(p => Repository.IsTestPath(p.Path));
        var prefixes = projects.Select(p => p.Name.Split('.')[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var consistentNaming = prefixes.Count == 1 || projects.Count >= 6 && prefixes.Count <= 2;
        var solution = repo.Files.Any(f => f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase));
        var m3 = new (string name, bool ok, string? where)[] { ("production code under src/ (or equivalent)", srcSeparated, prod.FirstOrDefault(p => !SrcDir.IsMatch(p.Directory))?.Path), ("tests separated from production code", testsSeparated, tests.FirstOrDefault(p => !Repository.IsTestPath(p.Path))?.Path), ("one root namespace / name prefix", consistentNaming, null) };
        foreach (var (name, ok, where) in m3) if (!ok) ctx.Add(new Finding("folder-structure", "M3", $"structure convention not met: {name}", where, null, null, 1));
        ctx.Measure("M3", Shape.FromChecklist(m3.Count(c => c.ok), m3.Length), note: $"{prod.Count} production + {tests.Count} test projects; prefixes: {string.Join(", ", prefixes)}");
        var d18 = new (string name, bool ok)[]
        {
            ("a solution file", solution), ("shared build properties (Directory.Build.props)", repo.Files.Contains("Directory.Build.props") || projects.Count == 1), (".editorconfig", repo.Files.Contains(".editorconfig")),
            ("src / tests separation", srcSeparated && testsSeparated), ("consistent project naming", consistentNaming),
            ("no stray projects outside src/tests/tools/samples", projects.All(p => p.Directory.Length == 0 && projects.Count == 1 || Regex.IsMatch(p.Directory, @"(?i)^(src|source|tests?|tools?|samples?|examples?|benchmarks?|build|eng|lib|libs|apps?|services|packages|modules)(/|$)"))),
        };
        foreach (var (name, ok) in d18) if (!ok) ctx.Add(new Finding("solution-structure", "D18", $"solution shape: missing {name}", null, null, null, 0.5));
        var shells = projects.Where(p => p.SourceFiles.Count == 0 && p.MarkupFiles.Count == 0 && !repo.FilesUnder(p.Directory).Any(f => f.EndsWith(".cs") || f.EndsWith(".razor") || f.EndsWith(".fs"))).ToList();
        foreach (var p in shells) ctx.Add(new Finding("solution-structure", "D18", $"{p.Name} is an empty shell: a project with no source file, structural noise left behind", p.Path, null, null, 1));
        ctx.Measure("D18", Shape.FromChecklist(d18.Count(c => c.ok), d18.Length) * Math.Exp(-0.25 * shells.Count), note: string.Join(", ", d18.Where(c => c.ok).Select(c => c.name)) + (shells.Count > 0 ? $"; {shells.Count} empty shell project(s)" : ""));
    }
}
