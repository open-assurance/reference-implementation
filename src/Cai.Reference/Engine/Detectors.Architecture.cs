using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cai.Reference.Engine;

/// <summary>Architecture: D5 D6 D7 D23 D26 D27 D35 AX1 AX2 AX3 AX4 AX5 AX6 AX7 AX8 AX9 AX10.</summary>
public static class Architecture
{
    public static void Run(ScanContext ctx)
    {
        var prod = ctx.Repo.Projects.Where(p => p.IsProduction && p.SourceFiles.Count > 0).ToList();
        if (prod.Count == 0) { foreach (var d in new[] { "D5", "D6", "D7", "D23", "D26", "D27", "D35", "AX1", "AX2", "AX3", "AX4", "AX5", "AX6", "AX7", "AX8", "AX9", "AX10" }) ctx.Skip(d, "no production C# projects"); return; }
        ProjectGraph(ctx, prod); Cohesion(ctx); Boundaries(ctx, prod); Size(ctx, prod); Navigability(ctx); ChangeCoupling(ctx); Di(ctx); Interfaces(ctx); Slices(ctx); Cqs(ctx); Composition(ctx, prod);
    }

    private static int Rank(ProjectRole r) => r switch { ProjectRole.Domain => 0, ProjectRole.Library => 0, ProjectRole.Application => 1, _ => 2 };

    // ---------- AX3 cycles, AX4 direction, AX8 test isolation, D5 coupling, D7 integrity ----------
    private static void ProjectGraph(ScanContext ctx, List<ProjectInfo> prod)
    {
        var all = ctx.Repo.Projects.ToDictionary(p => p.Path, StringComparer.Ordinal);
        // AX3: strongly connected components of size > 1
        var index = 0; var stack = new Stack<string>(); var idx = new Dictionary<string, int>(); var low = new Dictionary<string, int>(); var onStack = new HashSet<string>(); var sccs = new List<List<string>>();
        void Strong(string v)
        {
            idx[v] = low[v] = index++; stack.Push(v); onStack.Add(v);
            foreach (var w in all[v].ProjectReferences.Where(all.ContainsKey).OrderBy(x => x, StringComparer.Ordinal))
            {
                if (!idx.ContainsKey(w)) { Strong(w); low[v] = Math.Min(low[v], low[w]); }
                else if (onStack.Contains(w)) low[v] = Math.Min(low[v], idx[w]);
            }
            if (low[v] == idx[v]) { var scc = new List<string>(); string w; do { w = stack.Pop(); onStack.Remove(w); scc.Add(w); } while (w != v); if (scc.Count > 1) sccs.Add(scc); }
        }
        foreach (var p in all.Keys.OrderBy(x => x, StringComparer.Ordinal)) if (!idx.ContainsKey(p)) Strong(p);
        foreach (var scc in sccs) foreach (var p in scc.OrderBy(x => x, StringComparer.Ordinal))
            ctx.Add(new Finding("module-dependency-cycle", "AX3", $"{all[p].Name} is part of a project-reference cycle: {string.Join(" ↔ ", scc.Select(x => all[x].Name))}", p, null, null, 2));
        // a solution that builds cannot hold a project cycle; namespace cycles inside a project are where boundaries erode
        var nsCycles = NamespaceCycles(ctx);
        foreach (var f in nsCycles) ctx.Add(f);
        ctx.Measure("AX3", Math.Max(0, (sccs.Count == 0 ? 10 : Math.Max(0, 10 - 4 * sccs.Sum(s => s.Count))) * Shape.FromFindings(nsCycles, Math.Max(1.0, prod.Count / 2.0)) / 10), note: $"{sccs.Count} project cycle(s) among {ctx.Repo.Projects.Count} projects; {nsCycles.Count} namespace cycle(s)");

        // AX4 / D7: inward dependency rule across the project graph
        var violations = new List<Finding>();
        foreach (var p in prod)
            foreach (var r in p.ProjectReferences.Where(all.ContainsKey))
            {
                var dep = all[r];
                if (dep.Role == ProjectRole.Test) { ctx.Add(new Finding("production-depends-on-test-code", "AX8", $"{p.Name} references the test project {dep.Name}", p.Path, null, null, 2)); continue; }
                if (!dep.IsProduction) continue;
                if (Rank(p.Role) < Rank(dep.Role) && p.Role != ProjectRole.Library)
                    violations.Add(new Finding("layer-dependency-violation", "AX4", $"{p.Name} ({p.Role}) references {dep.Name} ({dep.Role}): dependencies must point inward, towards the domain", p.Path, null, null, p.Role == ProjectRole.Domain ? 2 : 1));
            }
        // namespace-level: a Domain project reaching for infrastructure or web frameworks (also DM6's concern)
        foreach (var pc in ctx.Workspace.Production.Where(p => p.Project.Role == ProjectRole.Domain))
            foreach (var tree in pc.Trees.Where(t => !pc.IsGenerated(t)))
                foreach (var u in tree.GetRoot().DescendantNodes().OfType<UsingDirectiveSyntax>())
                {
                    var name = u.Name?.ToString() ?? "";
                    if (Regex.IsMatch(name, @"^(Microsoft\.EntityFrameworkCore|Microsoft\.AspNetCore|Marten|Dapper|System\.Net\.Http|Npgsql|MongoDB|StackExchange\.Redis|RabbitMQ|MassTransit|Azure\.|Amazon\.|Microsoft\.Data\.)") || Regex.IsMatch(name, @"\.(Infrastructure|Persistence|Data|Web|Api|Messaging)(\.|$)"))
                        violations.Add(new Finding("layer-dependency-violation", "AX4", $"domain code imports {name}: the domain must not know about infrastructure or the web", ctx.Workspace.RelPath(tree.FilePath), Cs.Line(u), null, 1));
                }
        if (prod.Any(p => p.Role == ProjectRole.Application))
            foreach (var pc in ctx.Workspace.Production.Where(p => p.Project.Role == ProjectRole.Web))
                foreach (var tree in pc.Trees.Where(t => !pc.IsGenerated(t)))
                {
                    var rel2 = ctx.Workspace.RelPath(tree.FilePath); var root = tree.GetRoot();
                    if (!Regex.IsMatch(rel2, @"(?i)/(Controllers|Endpoints|Pages|Components|Views)/")) continue;   // the composition root may wire infrastructure; request handling may not
                    foreach (var prm in root.DescendantNodes().OfType<ParameterSyntax>().Where(pp => Regex.IsMatch(Cs.Simple(pp.Type), @"^(\w*DbContext|IDbConnection|NpgsqlConnection|SqlConnection|IDocumentSession|IQuerySession)$") && pp.Parent?.Parent is ClassDeclarationSyntax or ConstructorDeclarationSyntax))
                        violations.Add(new Finding("layer-dependency-violation", "AX4", $"{Cs.TypeName(prm)} in the web layer takes {Cs.Simple(prm.Type)} and talks to the database directly, bypassing the application layer", rel2, Cs.Line(prm), null, 1));
                }
        foreach (var v in violations) ctx.Add(v);
        ctx.Measure("AX4", Shape.FromFindings(violations, Math.Max(1.0, prod.Count / 4.0)), note: $"{violations.Count} inward-rule violation(s)");
        ctx.Measure("AX8", ctx.FindingsFor("AX8").Any() ? Math.Max(0, 10 - 5 * ctx.FindingsFor("AX8").Count()) : 10, note: $"{ctx.FindingsFor("AX8").Count()} production→test reference(s)");

        // D5: instability / abstractness / main-sequence distance, and volatile projects underneath stable ones
        var ca = prod.ToDictionary(p => p.Path, p => prod.Count(o => o.ProjectReferences.Contains(p.Path)), StringComparer.Ordinal);
        var ce = prod.ToDictionary(p => p.Path, p => p.ProjectReferences.Count(all.ContainsKey), StringComparer.Ordinal);
        var instability = prod.ToDictionary(p => p.Path, p => ca[p.Path] + ce[p.Path] == 0 ? 1.0 : (double)ce[p.Path] / (ca[p.Path] + ce[p.Path]), StringComparer.Ordinal);
        var abstractness = new Dictionary<string, double>(StringComparer.Ordinal); var typeCount = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var pc in ctx.Workspace.Production)
        {
            var types = pc.Trees.Where(t => !pc.IsGenerated(t)).SelectMany(t => t.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>()).ToList();
            typeCount[pc.Project.Path] = types.Count;
            abstractness[pc.Project.Path] = types.Count == 0 ? 0 : (double)types.Count(t => t is InterfaceDeclarationSyntax || t.Modifiers.Any(SyntaxKind.AbstractKeyword)) / types.Count;
        }
        var d5 = new List<Finding>();
        var dataShare = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var pc in ctx.Workspace.Production)
        {
            var types = pc.Trees.Where(t => !pc.IsGenerated(t)).SelectMany(t => t.GetRoot().DescendantNodes().OfType<BaseTypeDeclarationSyntax>()).ToList();
            dataShare[pc.Project.Path] = types.Count == 0 ? 0 : (double)types.Count(t => t is RecordDeclarationSyntax or EnumDeclarationSyntax or InterfaceDeclarationSyntax || t is ClassDeclarationSyntax c && c.Members.All(m => m is PropertyDeclarationSyntax)) / types.Count;
        }
        foreach (var p in prod)
        {
            var i = instability[p.Path]; var a = abstractness[p.Path]; var d = Math.Abs(a + i - 1);
            var isContracts = Regex.IsMatch(p.Name, @"(?i)\.(Contracts|Abstractions|Messages|Events|Shared|SharedKernel|Common)(\.|$)") || dataShare[p.Path] >= 0.8;
            // the main-sequence reading needs a graph to read (≥ 5 projects); a domain core is MEANT to be stable and concrete, and wire DTOs cannot be abstract
            if (typeCount[p.Path] >= 8 && d > 0.7 && prod.Count >= 5 && !isContracts && p.Role is ProjectRole.Infrastructure or ProjectRole.Web or ProjectRole.Library or ProjectRole.Application)
                d5.Add(new Finding("module-off-main-sequence", "D5", $"{p.Name}: abstractness {a:F2}, instability {i:F2}, distance {d:F2} from the main sequence ({(i < 0.5 ? "a concrete project many depend on: rigid" : "an abstract project nothing depends on: useless")})", p.Path, null, null, 1));
            // Stable Dependencies Principle: a project should depend only on projects at least as stable as itself
            foreach (var r in p.ProjectReferences.Where(all.ContainsKey).Where(r => prod.Any(x => x.Path == r)))
            {
                var dep = all[r];
                if (instability[r] > i + 0.3 && abstractness[r] < 0.5 && ca[p.Path] >= 1)
                {
                    var line = LineOf(ctx.Repo.Text(p.Path), System.IO.Path.GetFileName(r));
                    d5.Add(new Finding("unstable-dependency", "D5", $"{p.Name} (instability {i:F2}, {ca[p.Path]} dependents) depends on {dep.Name} (instability {instability[r]:F2}): a volatile project sits underneath a stable one, so its churn ripples upward", p.Path, line, line, 1));
                }
            }
        }
        foreach (var f in d5) ctx.Add(f);
        var cycleLoad = sccs.Sum(s => s.Count);
        ctx.Measure("D5", Shape.FromFindings(d5.Concat(ctx.FindingsFor("AX3")), Math.Max(1.0, prod.Count / 3.0)), note: $"{prod.Count} production projects; {d5.Count} coupling finding(s), {cycleLoad} in cycles");
        ctx.Facts["projectInstability"] = string.Join("; ", prod.Select(p => $"{p.Name} I={instability[p.Path]:F2} A={abstractness[p.Path]:F2}"));

        // D7: recorded architecture rules nobody enforces
        // a checkable ARCHITECTURE rule talks about references, dependencies, layers or boundaries — not about any "must" in prose
        var ruleDocs = ctx.Repo.Files.Where(f => f.EndsWith(".md", StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(f, @"(?i)(^|/)(docs?|adrs?|decisions|architecture)/|(?i)architecture")).Where(f => Regex.IsMatch(ctx.Repo.Text(f), @"(?i)\b(must|may|shall|should)\s+(not\s+|only\s+)?(reference|depend|import|call into|use types from|know about)|\bdepend(s|ency)?\s+(only\s+)?on\b.*\b(layer|project|slice|module|context)|dependency rule|layer(ing)? rule|(do|does) not reference|no (project|slice|layer|module) .* may|enforcement:|\bnever (take|reference|depend|call|use|reach)\b|only (through|via) (the )?(repositor|application|handler|query)")).ToList();
        var archTests = ctx.Workspace.Tests.Any(t => t.Trees.Any(tr => Regex.IsMatch(tr.GetRoot().ToString(), @"NetArchTest|ArchUnitNET|ArchRuleDefinition|Types\.InAssembly|HaveDependencyOn|NotHaveDependencyOn|GetReferencedAssemblies|ProjectReference"))) || ctx.Repo.Projects.Any(p => p.HasPackage("NetArchTest") || p.HasPackage("ArchUnitNET") || p.HasPackage("Microsoft.CodeAnalysis.BannedApiAnalyzers"));
        var d7 = new List<Finding>();
        foreach (var doc in ruleDocs)
        {
            var text = ctx.Repo.Text(doc);
            var fm = Regex.Match(text, @"\A---\s*\n(.*?)\n---", RegexOptions.Singleline);
            var enforcement = fm.Success ? Regex.Match(fm.Groups[1].Value, @"(?im)^enforcement:\s*(\S+)").Groups[1].Value.ToLowerInvariant() : "";
            var link = fm.Success ? Regex.Match(fm.Groups[1].Value, @"(?im)^enforcement_link:\s*(\S+)").Groups[1].Value : "";
            if (enforcement is "prose" or "review" or "manual" or "none")
                d7.Add(new Finding("architecture-rules-unenforced", "D7", $"{Path.GetFileName(doc)} records a checkable rule and declares `enforcement: {enforcement}`: nothing in the build or the test suite checks it", doc, null, null, 1));
            else if (enforcement is "test" or "analyzer" or "ci" && link.Length > 0 && !ctx.Repo.Exists(link.TrimStart('.', '/')))
                d7.Add(new Finding("architecture-rules-unenforced", "D7", $"{Path.GetFileName(doc)} claims `enforcement: {enforcement}` through {link}, which does not exist: the rule is enforced by nothing", doc, null, null, 1));
            else if (enforcement.Length == 0 && !archTests && violations.Count > 0)   // a layering rule the project graph already honours is enforced by the compiler
                d7.Add(new Finding("architecture-rules-unenforced", "D7", $"{Path.GetFileName(doc)} records a dependency or layering rule, no architecture test or analyzer enforces it, and the code already breaks the layering", doc, null, null, 1));
            else if (enforcement.Length == 0 && !archTests && Regex.IsMatch(text, @"(?i)\b(never|must not|may not|shall not|do(es)? not)\s+(take|inject|reference|use|call|reach|depend on|touch)\s+(the\s+)?`?(\w*DbContext|repositor(y|ies)|infrastructure|\w+Context)\b|\bonly (through|via) (the\s+)?(repositor(y|ies)|application handlers?|handlers?|query (service|interface)s?|`?IUnitOfWork)"))   // a prohibition on which TYPES may be taken or called is invisible to the compiler: only a test or an analyzer can hold it
                d7.Add(new Finding("architecture-rules-unenforced", "D7", $"{Path.GetFileName(doc)} records a type-level rule (which types may call or inject which) and no architecture test or analyzer checks it: review is the only enforcement", doc, null, null, 3));
        }
        var enforced = archTests;
        foreach (var f in d7) ctx.Add(f);
        ctx.Measure("D7", Shape.FromFindings(d7.Concat(violations).Concat(ctx.FindingsFor("AX3")), Math.Max(1.0, prod.Count / 3.0)), note: $"{ruleDocs.Count} rule document(s), enforced: {enforced}; {violations.Count} layer violation(s)");
    }

    /// <summary>Two-way `using` dependencies between sibling namespaces of one project, reported once per pair at the using that closes the cycle.</summary>
    private static List<Finding> NamespaceCycles(ScanContext ctx)
    {
        var findings = new List<Finding>();
        foreach (var pc in ctx.Workspace.Production.Where(p => p.Project.Role is ProjectRole.Infrastructure))   // adapters should stand alone; a model's namespaces legitimately interrelate
        {
            var edges = new Dictionary<(string from, string to), (string file, int line)>();
            var declared = new HashSet<string>(StringComparer.Ordinal);
            var trees = pc.Trees.Where(t => !pc.IsGenerated(t)).ToList();
            foreach (var tree in trees) foreach (var ns in tree.GetRoot().DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>()) declared.Add(ns.Name.ToString());
            string Sub(string ns) { var root = pc.Project.Name; return ns.StartsWith(root + ".", StringComparison.Ordinal) ? ns[(root.Length + 1)..].Split('.')[0] : ns == root ? "" : ns.Split('.').Last(); }
            // a persistence hub (DbContext, repositories, mappings, migrations, queries) is reached by everything that stores and reads, and reaches every stored type: it is plumbing, not a module with a boundary
            var hubTypes = new Dictionary<string, (int persistence, int all)>(StringComparer.Ordinal);
            foreach (var tree in trees)
            {
                var ns0 = tree.GetRoot().DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString(); if (ns0 is null) continue;
                var sub0 = Sub(ns0); if (sub0.Length == 0) continue;
                var relPath = ctx.Workspace.RelPath(tree.FilePath);
                foreach (var td in tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>())
                {
                    var c = hubTypes.GetValueOrDefault(sub0);
                    var persistence = Regex.IsMatch(td.Identifier.Text, @"(Repository|DbContext|Store|Configuration|Migration|ModelSnapshot|UnitOfWork|Queries|Documents?|Mapper|Mapping)$") || relPath.Contains("/Migrations/", StringComparison.Ordinal);
                    hubTypes[sub0] = (c.persistence + (persistence ? 1 : 0), c.all + 1);
                }
            }
            bool Hub(string sub) => hubTypes.TryGetValue(sub, out var c) && c.all > 0 && c.persistence * 2 >= c.all;
            // a file that only declares data (records, enums, property-only classes, interfaces) is a shared contract: depending on it is not a cycle in behaviour
            var byRel = trees.ToDictionary(t => ctx.Workspace.RelPath(t.FilePath), t => t, StringComparer.Ordinal);
            bool DataOnly(string rel) => byRel.TryGetValue(rel, out var t) && t.GetRoot().DescendantNodes().OfType<BaseTypeDeclarationSyntax>().All(td => td is EnumDeclarationSyntax or InterfaceDeclarationSyntax || td is RecordDeclarationSyntax r && !r.Members.OfType<MethodDeclarationSyntax>().Any() || td is TypeDeclarationSyntax c && !c.Members.OfType<MethodDeclarationSyntax>().Any() && !c.Members.OfType<ConstructorDeclarationSyntax>().Any(k => k.Body?.Statements.Count > 2));
            var dataEdges = new HashSet<(string from, string to)>();
            var graph = ctx.Graph;
            foreach (var tree in trees)
            {
                var root = tree.GetRoot(); var rel = ctx.Workspace.RelPath(tree.FilePath);
                var mine = root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString(); if (mine is null) continue;
                var from = Sub(mine); if (from.Length == 0) continue;
                if (Regex.IsMatch(root.ToString(), @"IEntityTypeConfiguration<|: DbContext\b|ModelBuilder|EntityTypeBuilder")) continue;   // mapping an entity is not a dependency of the model on its mapper
                // an edge needs a real type reference, not merely a `using` line
                foreach (var dep in graph.References.GetValueOrDefault(rel) ?? new HashSet<string>())
                {
                    if (!graph.FileNamespace.TryGetValue(dep, out var depNs) || graph.FileProject.GetValueOrDefault(dep) != pc.Project.Path) continue;
                    var to = Sub(depNs); if (to.Length == 0 || to == from) continue;
                    var u = root.DescendantNodes().OfType<UsingDirectiveSyntax>().FirstOrDefault(x => x.Name?.ToString() == depNs);
                    var site = u is not null ? Cs.Line(u) : Cs.Line(root.DescendantTokens().FirstOrDefault(t => t.IsKind(SyntaxKind.IdentifierToken) && graph.DeclaredTypes[dep].Contains(t.Text)));
                    if (edges.TryAdd((from, to), (rel, site)) && DataOnly(dep)) dataEdges.Add((from, to)); else if (!DataOnly(dep)) dataEdges.Remove((from, to));
                }
            }
            foreach (var ((from, to), site) in edges.OrderBy(e => e.Key.from, StringComparer.Ordinal).ThenBy(e => e.Key.to, StringComparer.Ordinal))
                if (string.CompareOrdinal(from, to) < 0 && edges.ContainsKey((to, from)) && !Hub(from) && !Hub(to) && !dataEdges.Contains((from, to)) && !dataEdges.Contains((to, from)))
                    findings.Add(new Finding("module-dependency-cycle", "AX3", $"{pc.Project.Name}: namespaces {from} and {to} use each other ({Path.GetFileName(site.file)} ↔ {Path.GetFileName(edges[(to, from)].file)}): a boundary that only exists on paper", site.file, site.line, site.line, 1));
        }
        return findings;
    }

    private static int? LineOf(string text, string needle) { var i = text.IndexOf(needle, StringComparison.OrdinalIgnoreCase); return i < 0 ? null : text.Take(i).Count(ch => ch == '\n') + 1; }

    private static Dictionary<string, int> ProjectChurn(ScanContext ctx, List<ProjectInfo> prod)
    {
        var head = ctx.Repo.HeadDate == default ? (ctx.History.Commits.FirstOrDefault()?.Date ?? default) : ctx.Repo.HeadDate;
        var recent = ctx.History.Commits.Where(c => c.Date >= head.AddDays(-180)).ToList();
        return prod.ToDictionary(p => p.Path, p => recent.Count(c => c.Files.Any(f => f.path.StartsWith(p.Directory.Length == 0 ? "" : p.Directory + "/", StringComparison.Ordinal) && f.path.EndsWith(".cs"))), StringComparer.Ordinal);
    }

    // ---------- D6 LCOM4 ----------
    private static void Cohesion(ScanContext ctx)
    {
        var findings = new List<Finding>(); int classes = 0;
        foreach (var (p, tree) in ctx.Workspace.ProductionTrees)
        {
            var rel = ctx.Workspace.RelPath(tree.FilePath);
            foreach (var cls in tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                var fields = cls.Members.OfType<FieldDeclarationSyntax>().SelectMany(f => f.Declaration.Variables.Select(v => v.Identifier.Text))
                    .Concat(cls.Members.OfType<PropertyDeclarationSyntax>().Where(pr => pr.AccessorList is not null && pr.AccessorList.Accessors.All(a => a.Body is null && a.ExpressionBody is null)).Select(pr => pr.Identifier.Text)).ToHashSet(StringComparer.Ordinal);
                var methods = cls.Members.Where(m => m is MethodDeclarationSyntax md && !md.Modifiers.Any(SyntaxKind.StaticKeyword) && md.Identifier.Text is not ("ToString" or "Equals" or "GetHashCode" or "Dispose") || m is PropertyDeclarationSyntax pd && (pd.ExpressionBody is not null || pd.AccessorList?.Accessors.Any(a => a.Body is not null || a.ExpressionBody is not null) == true)).ToList();
                if (methods.Count < 6 || fields.Count < 2) continue;
                classes++;
                var names = methods.Select(m => m is MethodDeclarationSyntax md ? md.Identifier.Text : ((PropertyDeclarationSyntax)m).Identifier.Text).ToList();
                var uses = methods.Select(m => m.DescendantNodes().OfType<IdentifierNameSyntax>().Select(i => i.Identifier.Text).ToHashSet(StringComparer.Ordinal)).ToList();
                var parent = Enumerable.Range(0, methods.Count).ToArray();
                int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
                void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[a] = b; }
                for (var i = 0; i < methods.Count; i++) for (var j = i + 1; j < methods.Count; j++)
                {
                    if (uses[i].Intersect(uses[j]).Any(fields.Contains) || uses[i].Contains(names[j]) || uses[j].Contains(names[i])) Union(i, j);
                }
                var components = Enumerable.Range(0, methods.Count).Select(Find).Distinct().Count();
                if (components >= 3)
                {
                    var loc = ctx.Loc(cls.Identifier);
                    findings.Add(new Finding("low-class-cohesion", "D6", $"{cls.Identifier.Text} has LCOM4 = {components}: its {methods.Count} methods and {fields.Count} fields form {components} disconnected groups that share no state", loc.File, loc.Line, loc.Line, components >= 4 ? 2 : 1));
                }
            }
        }
        foreach (var f in findings) ctx.Add(f);
        if (classes == 0) ctx.Skip("D6", "no classes large enough (≥ 6 methods, ≥ 2 fields) to measure cohesion");
        else ctx.Measure("D6", Shape.FromFindings(findings, ctx.ProductionKloc), note: $"{findings.Count} of {classes} measurable classes have LCOM4 ≥ 3");
    }

    // ---------- D23 boundary type leakage ----------
    private static void Boundaries(ScanContext ctx, List<ProjectInfo> prod)
    {
        // a bounded context = a group of projects sharing the first two name segments (Company.Context.*) when ≥ 2 such groups exist
        // a bounded context is Company.Context.Layer: three or more name segments whose second segment is not itself a layer word
        var layerWords = new Regex(@"(?i)^(Domain|Application|Infrastructure|Persistence|Api|Web|Worker|Host|Contracts|Shared|SharedKernel|Common|Tests?|Core|Messaging|Abstractions|Client|Cli|Tools?|Diagnostics|ServiceDefaults|Notifications|Gateway|Bff|Ui|Server)$");
        string? ContextOf(ProjectInfo p) { var seg = p.Name.Split('.'); return seg.Length >= 3 && !layerWords.IsMatch(seg[1]) ? seg[0] + "." + seg[1] : null; }
        var isShared = (ProjectInfo p) => Regex.IsMatch(p.Name, @"(?i)\.(Contracts|Shared|SharedKernel|Kernel|Common|Abstractions|Messages|Events|IntegrationEvents|BuildingBlocks|Messaging)(\.|$)") || ContextOf(p) is null;
        var contexts = prod.Where(p => !isShared(p)).GroupBy(p => ContextOf(p)!).ToList();
        if (contexts.Count < 2) { ctx.Skip("D23", $"one bounded context ({contexts.Count} named context group(s)): layers of one context are not contexts"); return; }
        var ctxOf = prod.ToDictionary(p => p.Path, p => ContextOf(p) ?? "", StringComparer.Ordinal);
        var ownedTypes = new Dictionary<string, (string ctx, string project)>(StringComparer.Ordinal);  // type → owning context (domain and application types alike)
        foreach (var pc in ctx.Workspace.Production.Where(p => !isShared(p.Project) && p.Project.Role is ProjectRole.Domain or ProjectRole.Application or ProjectRole.Library))
            foreach (var t in pc.Trees.Where(t => !pc.IsGenerated(t)).SelectMany(t => t.GetRoot().DescendantNodes().OfType<BaseTypeDeclarationSyntax>()))
                ownedTypes.TryAdd(t.Identifier.Text, (ctxOf[pc.Project.Path], pc.Project.Name));
        var findings = new List<Finding>();
        foreach (var pc in ctx.Workspace.Production.Where(p => !isShared(p.Project)))
            foreach (var tree in pc.Trees.Where(t => !pc.IsGenerated(t)))
            {
                var mine = ctxOf[pc.Project.Path];
                var seen = new HashSet<string>(StringComparer.Ordinal);
                // the PUBLIC SURFACE of this context's types: signatures, base lists and positional parameters — never method bodies
                foreach (var type in tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>().Where(t => t.Modifiers.Any(SyntaxKind.PublicKeyword)))
                {
                    var surface = new List<SyntaxNode>();
                    if (type.BaseList is not null) surface.Add(type.BaseList);
                    if (type.ParameterList is not null) surface.Add(type.ParameterList);
                    foreach (var mem in type.Members)
                        switch (mem)
                        {
                            case MethodDeclarationSyntax md when md.Modifiers.Any(SyntaxKind.PublicKeyword): surface.Add(md.ReturnType); surface.Add(md.ParameterList); break;
                            case PropertyDeclarationSyntax pd when pd.Modifiers.Any(SyntaxKind.PublicKeyword): surface.Add(pd.Type); break;
                            case FieldDeclarationSyntax fd when fd.Modifiers.Any(SyntaxKind.PublicKeyword): surface.Add(fd.Declaration.Type); break;
                            case ConstructorDeclarationSyntax cd when cd.Modifiers.Any(SyntaxKind.PublicKeyword): surface.Add(cd.ParameterList); break;
                        }
                    foreach (var id in surface.SelectMany(s => s.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()))
                    {
                        if (!SyntaxFacts.IsInNamespaceOrTypeContext(id)) continue;
                        if (!ownedTypes.TryGetValue(id.Identifier.Text, out var owner) || owner.ctx == mine || !seen.Add(id.Identifier.Text)) continue;
                        findings.Add(new Finding("boundary-type-leakage", "D23", $"{type.Identifier.Text}'s public surface is typed by {id.Identifier.Text}, owned by {owner.ctx} ({owner.project}): every caller of {mine} now compiles against another context's model; contexts share contracts, not domain types", ctx.Workspace.RelPath(tree.FilePath), Cs.Line(id), null, 1));
                    }
                }
            }
        foreach (var f in findings) ctx.Add(f);
        ctx.Measure("D23", Shape.FromFindings(findings, Math.Max(1.0, prod.Count / 3.0)), note: $"{contexts.Count} contexts: {string.Join(", ", contexts.Select(c => c.Key))}; {findings.Count} leak(s)");
    }

    // ---------- D26 oversized projects ----------
    private static void Size(ScanContext ctx, List<ProjectInfo> prod)
    {
        var findings = new List<Finding>();
        foreach (var pc in ctx.Workspace.Production)
        {
            var trees = pc.Trees.Where(t => !pc.IsGenerated(t)).ToList();
            var types = trees.SelectMany(t => t.GetRoot().DescendantNodes().OfType<BaseTypeDeclarationSyntax>()).Count();
            var loc = trees.Sum(Workspace.CountLoc);
            var namespaces = trees.SelectMany(t => t.GetRoot().DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().Select(n => n.Name.ToString())).Distinct().Count();
            if (types >= 120 || loc >= 15000 || types >= 60 && namespaces >= 12)
                findings.Add(new Finding("oversized-module", "D26", $"{pc.Project.Name}: {types} types, {loc} LOC, {namespaces} namespaces — a grab-bag rather than a focused unit", pc.Project.Path, null, null, types >= 250 || loc >= 30000 ? 2 : 1));
        }
        foreach (var f in findings) ctx.Add(f);
        ctx.Measure("D26", Shape.FromFindings(findings, Math.Max(1.0, prod.Count / 3.0)), note: $"{findings.Count} of {prod.Count} projects oversized");
    }

    // ---------- D27 navigability ----------
    private static void Navigability(ScanContext ctx)
    {
        int methods = 0; var wrappers = new Dictionary<string, (string file, int line, string target)>(StringComparer.Ordinal);
        foreach (var (p, tree) in ctx.Workspace.ProductionTrees)
        {
            var rel = ctx.Workspace.RelPath(tree.FilePath);
            foreach (var md in tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                var body = Cs.BodyOf(md); if (body is null) continue; methods++;
                var expr = body switch { ArrowExpressionClauseSyntax a => a.Expression, BlockSyntax { Statements.Count: 1 } b => (b.Statements[0] as ReturnStatementSyntax)?.Expression ?? (b.Statements[0] as ExpressionStatementSyntax)?.Expression, _ => null };
                if (expr is AwaitExpressionSyntax aw) expr = aw.Expression;
                if (expr is InvocationExpressionSyntax ca && ca.Expression is MemberAccessExpressionSyntax cma && cma.Name.Identifier.Text == "ConfigureAwait") expr = cma.Expression;
                var paramNames = md.ParameterList.Parameters.Select(pp => pp.Identifier.Text).ToHashSet(StringComparer.Ordinal);
                bool Forwarded(ExpressionSyntax a) => a is IdentifierNameSyntax id && paramNames.Contains(id.Identifier.Text) || a is ObjectCreationExpressionSyntax oc && oc.ArgumentList is not null && oc.ArgumentList.Arguments.All(x => x.Expression is IdentifierNameSyntax i2 && paramNames.Contains(i2.Identifier.Text)) || a is ImplicitObjectCreationExpressionSyntax ioc && ioc.ArgumentList.Arguments.All(x => x.Expression is IdentifierNameSyntax i3 && paramNames.Contains(i3.Identifier.Text));
                if (expr is InvocationExpressionSyntax inv && inv.ArgumentList.Arguments.Count >= 1 && inv.ArgumentList.Arguments.All(a => Forwarded(a.Expression)) && Cs.MemberName(inv.Expression) != md.Identifier.Text && inv.ArgumentList.Arguments.SelectMany(a => a.Expression.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()).Select(i => i.Identifier.Text).Count(paramNames.Contains) >= Math.Max(1, paramNames.Count - 1))
                    wrappers[$"{Cs.TypeName(md)}.{md.Identifier.Text}"] = (rel, Cs.Line(md.Identifier), Cs.MemberName(inv.Expression));
            }
        }
        var chains = wrappers.Where(w => wrappers.Keys.Any(k => k.EndsWith("." + w.Value.target, StringComparison.Ordinal))).ToList();
        var findings = chains.Select(c => new Finding("call-indirection", "D27", $"{c.Key} only forwards to {c.Value.target}, which itself only forwards on: a pass-through chain a reader must trace through", c.Value.file, c.Value.line, c.Value.line, 1)).ToList();
        // a whole pass-through layer: a class most of whose public methods only forward their arguments
        foreach (var (p, tree) in ctx.Workspace.ProductionTrees)
            foreach (var cls in tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                var publicMethods = cls.Members.OfType<MethodDeclarationSyntax>().Where(md => md.Modifiers.Any(SyntaxKind.PublicKeyword)).ToList();
                if (publicMethods.Count < 3) continue;
                var forwarding = publicMethods.Count(md => wrappers.ContainsKey($"{cls.Identifier.Text}.{md.Identifier.Text}"));
                if (forwarding * 4 >= publicMethods.Count * 3) { var loc = ctx.Loc(cls); findings.Add(new Finding("call-indirection", "D27", $"{cls.Identifier.Text} is a pass-through layer: {forwarding} of its {publicMethods.Count} public methods forward their arguments unchanged and return the result", loc.File, loc.Line, loc.EndLine, 1)); }
            }
        foreach (var f in findings) ctx.Add(f);
        if (methods == 0) { ctx.Skip("D27", "no methods"); return; }
        var share = (double)wrappers.Count / methods;
        ctx.Measure("D27", Math.Max(0, 10 * (1 - share * 3)) * (Shape.FromFindings(findings, ctx.ProductionKloc) / 10), note: $"{wrappers.Count} of {methods} methods are pure pass-throughs ({share:P0}); {findings.Count} chain(s)");
    }

    // ---------- D35 change coupling ----------
    private static void ChangeCoupling(ScanContext ctx)
    {
        var h = ctx.History;
        if (!h.Available || h.Commits.Count < 10) { ctx.Skip("D35", h.Available ? $"only {h.Commits.Count} commits: too little history for co-change pairs" : "no git history"); return; }
        var graph = ctx.Graph;
        var prodFiles = graph.FileProject.Keys.ToHashSet(StringComparer.Ordinal);
        var changes = new Dictionary<string, int>(StringComparer.Ordinal); var pairs = new Dictionary<(string, string), int>();
        foreach (var c in h.Commits)
        {
            var files = c.Files.Select(f => f.path).Where(prodFiles.Contains).Distinct().OrderBy(f => f, StringComparer.Ordinal).ToList();
            if (files.Count > 12) continue;   // sweeping commits (renames, formatting) say nothing about coupling
            foreach (var f in files) changes[f] = changes.GetValueOrDefault(f) + 1;
            for (var i = 0; i < files.Count; i++) for (var j = i + 1; j < files.Count; j++) pairs[(files[i], files[j])] = pairs.GetValueOrDefault((files[i], files[j])) + 1;
        }
        var findings = new List<Finding>();
        foreach (var ((a, b), n) in pairs.OrderBy(p => p.Key.Item1, StringComparer.Ordinal).ThenBy(p => p.Key.Item2, StringComparer.Ordinal))
        {
            if (n < 5) continue;                                                                 // a handful of shared feature commits is not coupling
            var conf = (double)n / Math.Min(changes[a], changes[b]);
            if (conf < 0.7) continue;
            if (graph.DependsOn(a, b) || graph.DependsOn(b, a)) continue;                       // declared: one references the other's types
            if (Path.GetDirectoryName(a) == Path.GetDirectoryName(b)) continue;                 // siblings in one folder are expected to move together
            if (graph.FileProject[a] == graph.FileProject[b] && graph.FileNamespace[a] == graph.FileNamespace[b]) continue;
            if (Regex.IsMatch(a + " " + b, @"(ServiceCollectionExtensions|ApplicationBuilderExtensions|Program\.cs|Startup\.cs|DependencyInjection|Composition|Module\.cs)")) continue;   // composition roots change with everything by design
            var shared = graph.References.GetValueOrDefault(a)?.Intersect(graph.References.GetValueOrDefault(b) ?? new HashSet<string>()).Any() == true;
            findings.Add(new Finding("change-coupling", "D35", $"{Path.GetFileName(a)} and {Path.GetFileName(b)} changed together in {n} commits ({conf:P0} of the time) with no code dependency between them{(shared ? ", only a shared dependency" : "")}: a hidden coupling or a boundary in the wrong place", a, null, null, 1));
            findings.Add(new Finding("change-coupling", "D35", $"{Path.GetFileName(b)} and {Path.GetFileName(a)} changed together in {n} commits ({conf:P0} of the time) with no code dependency between them", b, null, null, 0.5));
        }
        foreach (var f in findings) ctx.Add(f);
        ctx.Measure("D35", Shape.FromFindings(findings, Math.Max(1.0, prodFiles.Count / 25.0)), note: $"{findings.Count / 2} hidden-coupled pair(s) among {prodFiles.Count} files over {h.Commits.Count} commits");
    }

    // ---------- AX1 captive dependencies, AX2 stateful singletons ----------
    private static readonly Regex Lifetime = new(@"^Add(Singleton|Scoped|Transient|HostedService)$", RegexOptions.Compiled);

    private static void Di(ScanContext ctx)
    {
        var registrations = new List<(string service, string impl, string lifetime, string file, int line)>();
        var factoryDeps = new Dictionary<(string impl, string file, int line), List<string>>();
        foreach (var (p, tree) in ctx.Workspace.ProductionTrees)
        {
            var rel = ctx.Workspace.RelPath(tree.FilePath);
            foreach (var inv in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var name = Cs.MemberName(inv.Expression);
                var m = Lifetime.Match(name); if (!m.Success) continue;
                var lifetime = m.Groups[1].Value == "HostedService" ? "Singleton" : m.Groups[1].Value;
                var generic = inv.Expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax g } ? g : inv.Expression as GenericNameSyntax;
                string service, impl;
                if (generic is not null) { var args = generic.TypeArgumentList.Arguments.Select(Cs.Simple).ToList(); service = args[0]; impl = args.Count > 1 ? args[1] : args[0]; }
                else if (inv.ArgumentList.Arguments.Count >= 1 && inv.ArgumentList.Arguments[0].Expression is TypeOfExpressionSyntax t0) { service = Cs.Simple(t0.Type); impl = inv.ArgumentList.Arguments.Count > 1 && inv.ArgumentList.Arguments[1].Expression is TypeOfExpressionSyntax t1 ? Cs.Simple(t1.Type) : service; }
                else continue;
                var factory = inv.ArgumentList.Arguments.Select(a => a.Expression).OfType<LambdaExpressionSyntax>().FirstOrDefault();
                if (factory is not null)
                {
                    // a factory hides the dependency from the container's scope validation: read the `new X(...)` it builds and the services it resolves
                    var created = factory.DescendantNodesAndSelf().OfType<ObjectCreationExpressionSyntax>().FirstOrDefault();
                    impl = created is not null ? Cs.Simple(created.Type) : service;
                    if (generic is null && created is not null) service = impl;
                    factoryDeps[(impl, rel, Cs.Line(inv))] = factory.DescendantNodesAndSelf().OfType<GenericNameSyntax>().Where(g => g.Identifier.Text is "GetRequiredService" or "GetService").Select(g => Cs.Simple(g.TypeArgumentList.Arguments[0])).ToList();
                }
                registrations.Add((service, impl, lifetime, rel, Cs.Line(inv)));
            }
        }
        if (registrations.Count == 0) { ctx.Skip("AX1", "no dependency-injection registrations found"); ctx.Skip("AX2", "no dependency-injection registrations found"); return; }
        var lifetimeOf = registrations.GroupBy(r => r.service).ToDictionary(g => g.Key, g => g.Select(r => r.lifetime).Distinct().ToList(), StringComparer.Ordinal);
        var classes = ctx.Workspace.ProductionTrees.SelectMany(t => t.tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>().Select(c => (c, t.tree))).GroupBy(x => x.c.Identifier.Text).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var captive = new List<Finding>(); var stateful = new List<Finding>();
        foreach (var reg in registrations.Where(r => r.lifetime == "Singleton"))
        {
            if (!classes.TryGetValue(reg.impl, out var cls)) continue;
            var rel = ctx.Workspace.RelPath(cls.tree.FilePath);
            var ctorParams = cls.c.Members.OfType<ConstructorDeclarationSyntax>().SelectMany(c => c.ParameterList.Parameters).Concat(cls.c.ParameterList?.Parameters ?? default).ToList();
            var resolved = factoryDeps.TryGetValue((reg.impl, reg.file, reg.line), out var fd) ? fd : new List<string>();
            foreach (var (t, node) in ctorParams.Select(pp => (Cs.Simple(pp.Type), (SyntaxNode)pp)).Concat(resolved.Select(r => (r, (SyntaxNode)cls.c))))
            {
                if (t is "IServiceScopeFactory" or "IServiceProvider" or "Func" or "Lazy" or "IOptionsMonitor" or "IOptions" or "IHttpClientFactory" or "ILogger" or "TimeProvider") continue;
                // only a SCOPED capture is the lifetime bug (the DbContext in a singleton); a stateless transient captured once is a style choice
                if (lifetimeOf.TryGetValue(t, out var lts) && lts.Contains("Scoped") && !lts.Contains("Singleton"))
                    captive.Add(new Finding("captive-dependency", "AX1", $"singleton {reg.impl} captures {t}, registered as scoped{(resolved.Contains(t) ? " (through a factory, hidden from scope validation)" : "")}: the scoped instance lives as long as the singleton", rel, node == cls.c ? Cs.Line(cls.c.Identifier) : Cs.Line(node), null, 2));
                else if (Regex.IsMatch(t, @"^(DbContext|\w+DbContext)$") && !lifetimeOf.ContainsKey(t) && ctx.Workspace.ProductionTrees.Any(x => x.tree.GetRoot().ToString().Contains($"AddDbContext<{t}>")))
                    captive.Add(new Finding("captive-dependency", "AX1", $"singleton {reg.impl} captures the scoped DbContext {t}", rel, node == cls.c ? Cs.Line(cls.c.Identifier) : Cs.Line(node), null, 2));
            }
            // AX2: mutable instance state in a singleton without synchronisation
            foreach (var field in cls.c.Members.OfType<FieldDeclarationSyntax>())
            {
                var type = Cs.Simple(field.Declaration.Type);
                var readOnly = field.Modifiers.Any(SyntaxKind.ReadOnlyKeyword); var isConst = field.Modifiers.Any(SyntaxKind.ConstKeyword) || field.Modifiers.Any(SyntaxKind.StaticKeyword) && readOnly;
                if (isConst) continue;
                var threadSafe = Regex.IsMatch(type, @"^(Concurrent\w+|Immutable\w+|Lock|SemaphoreSlim|Channel|BlockingCollection|ReaderWriterLockSlim|CancellationTokenSource|Lazy|ILogger|HttpClient|TimeProvider|Random|Meter|Counter|Histogram|ActivitySource|I[A-Z]\w+|Func|Action|Timer|Task|TaskCompletionSource)$") || type.StartsWith("Concurrent") || type.StartsWith("Immutable") || type.StartsWith("Frozen");
                var mutableCollection = Regex.IsMatch(type, @"^(List|Dictionary|HashSet|Queue|Stack|SortedDictionary|SortedList|LinkedList|StringBuilder)$");
                var body = cls.c.ToString();
                var locked = Regex.IsMatch(body, @"\block\s*\(") || Regex.IsMatch(body, @"Interlocked\.|Volatile\.|\.EnterWriteLock|\.Wait\(\)|\.WaitAsync\(");
                var name = field.Declaration.Variables.First().Identifier.Text;
                if (!readOnly && !threadSafe && !type.StartsWith("I") && !locked || readOnly && mutableCollection && !locked)
                    stateful.Add(new Finding("unsynchronized-shared-state", "AX2", $"singleton {reg.impl} keeps mutable state {type} {name} with no lock or concurrent type: concurrent callers race on it", rel, Cs.Line(field), null, 1));
            }
        }
        foreach (var f in captive.DistinctBy(f => (f.File, f.Line))) ctx.Add(f);
        foreach (var f in stateful.DistinctBy(f => (f.File, f.Line))) ctx.Add(f);
        var singletons = registrations.Count(r => r.lifetime == "Singleton");
        ctx.Measure("AX1", Shape.FromFindings(ctx.FindingsFor("AX1"), Math.Max(1.0, registrations.Count / 10.0)), note: $"{ctx.FindingsFor("AX1").Count()} captive dependency site(s) across {singletons} singleton registrations");
        ctx.Measure("AX2", Shape.FromFindings(ctx.FindingsFor("AX2"), Math.Max(1.0, singletons / 5.0)), note: $"{ctx.FindingsFor("AX2").Count()} racy field(s) in {singletons} singleton(s)");
    }

    // ---------- AX6 interface segregation ----------
    private static void Interfaces(ScanContext ctx)
    {
        var findings = new List<Finding>(); int count = 0;
        var all = ctx.Workspace.ProductionTrees.Select(t => t.tree.GetRoot()).ToList();
        foreach (var (p, tree) in ctx.Workspace.ProductionTrees)
        {
            var rel = ctx.Workspace.RelPath(tree.FilePath);
            foreach (var iface in tree.GetRoot().DescendantNodes().OfType<InterfaceDeclarationSyntax>())
            {
                count++;
                var members = iface.Members.Count;
                var implementers = all.SelectMany(r => r.DescendantNodes().OfType<TypeDeclarationSyntax>()).Where(t => t.BaseList?.Types.Any(b => Cs.Simple(b.Type) == iface.Identifier.Text) == true).ToList();
                var holes = implementers.Sum(t => t.Members.Count(m => m.ToString().Contains("NotSupportedException") || m.ToString().Contains("NotImplementedException")));
                if (members >= 12 || members >= 6 && holes >= 2)
                {
                    var loc = ctx.Loc(iface.Identifier);
                    findings.Add(new Finding("fat-interface", "AX6", $"{iface.Identifier.Text} has {members} members{(holes > 0 ? $" and its implementers leave {holes} of them as not-supported holes" : "")}: clients depend on more than they use", loc.File, loc.Line, loc.Line, members >= 20 ? 2 : 1));
                }
            }
        }
        foreach (var f in findings) ctx.Add(f);
        if (count == 0) ctx.Skip("AX6", "no interfaces"); else ctx.Measure("AX6", Shape.FromFindings(findings, Math.Max(1.0, count / 10.0)), note: $"{findings.Count} of {count} interfaces fat");
    }

    // ---------- AX7 slice cohesion ----------
    private static void Slices(ScanContext ctx)
    {
        var graph = ctx.Graph;
        string? SliceOf(string file) { var m = Regex.Match(file, @"(?i)/(Features|Slices|Modules|UseCases)/([^/]+)/"); return m.Success ? m.Groups[2].Value : null; }
        var slices = graph.FileProject.Keys.Select(SliceOf).Where(s => s is not null).Distinct().ToList();
        if (slices.Count < 2) { ctx.Skip("AX7", "no vertical-slice layout (Features/<slice>/) with two or more slices"); return; }
        var findings = new List<Finding>();
        // a slice's Contracts / Abstractions / Messages project is its published surface: using it is how slices are MEANT to talk
        bool PublishedSurface(string dep) => graph.FileProject.TryGetValue(dep, out var proj) && Regex.IsMatch(System.IO.Path.GetFileNameWithoutExtension(proj), @"(?i)\.(Contracts|Abstractions|Public|Messages|Events|Shared|Api|Client)$");
        // type names collide across slices (every slice may have a Game); a reference is real only when the file's project references the other file's project
        var reach = ctx.Repo.Projects.ToDictionary(p => p.Path, p => Workspace.TransitiveReferences(p, ctx.Repo).Append(p.Path).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        bool ReallyReferences(string file, string dep) => graph.FileProject.TryGetValue(file, out var fp) && graph.FileProject.TryGetValue(dep, out var dp) && reach.TryGetValue(fp, out var set) && set.Contains(dp);
        foreach (var file in graph.FileProject.Keys.OrderBy(f => f, StringComparer.Ordinal))
        {
            var mine = SliceOf(file); if (mine is null || Regex.IsMatch(mine, "(?i)^(shared|common|core|infrastructure)$")) continue;
            var seen = new HashSet<string>(StringComparer.Ordinal);   // one finding per (file, foreign slice)
            foreach (var dep in graph.References[file].OrderBy(f => f, StringComparer.Ordinal))
            {
                var theirs = SliceOf(dep);
                if (theirs is null || theirs == mine || Regex.IsMatch(theirs, "(?i)^(shared|common|core|infrastructure)$")) continue;
                if (PublishedSurface(dep) || !ReallyReferences(file, dep) || !seen.Add(theirs)) continue;
                var tree = ctx.Workspace.ProductionTrees.First(t => ctx.Workspace.RelPath(t.tree.FilePath) == file).tree;
                var theirTypes = graph.DeclaredTypes[dep];
                var site = tree.GetRoot().DescendantTokens().FirstOrDefault(t => t.IsKind(SyntaxKind.IdentifierToken) && theirTypes.Contains(t.Text) && t.Parent is not UsingDirectiveSyntax && !t.Parent!.Ancestors().Any(a => a is UsingDirectiveSyntax));
                findings.Add(new Finding("cross-slice-coupling", "AX7", $"slice {mine} ({Path.GetFileName(file)}) reaches directly into slice {theirs} ({Path.GetFileName(dep)}: {string.Join(", ", theirTypes.Take(2))})", file, site == default ? null : Cs.Line(site), null, 1));
            }
        }
        foreach (var f in findings) ctx.Add(f);
        ctx.Measure("AX7", Shape.FromFindings(findings, Math.Max(1.0, slices.Count / 4.0)), note: $"{slices.Count} slices, {findings.Count} cross-slice reference(s)");
    }

    // ---------- AX9 CQS ----------
    private static void Cqs(ScanContext ctx)
    {
        var findings = new List<Finding>(); int handlers = 0;
        foreach (var (p, tree) in ctx.Workspace.ProductionTrees)
        {
            var rel = ctx.Workspace.RelPath(tree.FilePath);
            foreach (var cls in tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                var isQueryHandler = Regex.IsMatch(cls.Identifier.Text, @"(Query|Get\w+|Find\w+|List\w+|Search\w+)Handler$") || cls.BaseList?.Types.Any(b => b.Type is GenericNameSyntax g && g.Identifier.Text is "IRequestHandler" or "IQueryHandler" && g.TypeArgumentList.Arguments.Count >= 1 && Regex.IsMatch(g.TypeArgumentList.Arguments[0].ToString(), @"Query$|^Get|^Find|^List|^Search")) == true;
                if (!isQueryHandler) continue;
                handlers++;
                foreach (var inv in cls.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    var name = Cs.MemberName(inv.Expression);
                    if (name is "SaveChanges" or "SaveChangesAsync" or "Add" or "AddAsync" or "Update" or "Remove" or "Publish" or "PublishAsync" or "Send" or "SendAsync" or "Raise" or "AddDomainEvent" or "Store" or "StoreAsync" or "Insert" or "InsertAsync" or "Delete" or "DeleteAsync" or "ExecuteUpdate" or "ExecuteDelete" or "ExecuteUpdateAsync" or "ExecuteDeleteAsync" or "Commit" or "CommitAsync")
                    {
                        if (name is "Add" or "Remove" && inv.Expression is MemberAccessExpressionSyntax ma && (Regex.IsMatch(ma.Expression.ToString(), @"^(result|results|items|list|dto|dtos|response|output|\w+List|\w+s)$") || Regex.IsMatch(ma.Expression.ToString(), @"(?i)metric|counter|histogram|telemetry|stats") || inv.ArgumentList.Arguments.Count == 1 && inv.ArgumentList.Arguments[0].Expression is LiteralExpressionSyntax)) continue;   // result lists and metric counters are not side effects
                        findings.Add(new Finding("query-with-side-effects", "AX9", $"query handler {cls.Identifier.Text} calls {name}(…): a read that writes cannot be retried, cached or served from a replica", rel, Cs.Line(inv), null, 1));
                        break;
                    }
                }
            }
        }
        foreach (var f in findings) ctx.Add(f);
        if (handlers == 0) ctx.Skip("AX9", "no query handlers (CQRS shape not detected)"); else ctx.Measure("AX9", Shape.FromFindings(findings, Math.Max(1.0, handlers / 10.0)), note: $"{findings.Count} of {handlers} query handlers have side effects");
    }

    // ---------- AX5 style fit, AX10 composition ----------
    private static void Composition(ScanContext ctx, List<ProjectInfo> prod)
    {
        var byRole = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var pc in ctx.Workspace.Projects)
        {
            var role = pc.Project.Role switch { ProjectRole.Domain => "domain", ProjectRole.Application => "application", ProjectRole.Infrastructure => "infrastructure", ProjectRole.Web => "web", ProjectRole.Worker => "worker", ProjectRole.Tool => "tool", ProjectRole.Test => "test", ProjectRole.Benchmark => "benchmark", _ => "library" };
            foreach (var t in pc.Trees) { var k = pc.IsGenerated(t) ? "generated" : role; byRole[k] = byRole.GetValueOrDefault(k) + Workspace.CountLoc(t); }
        }
        var production = byRole.Where(k => k.Key is not ("test" or "benchmark" or "generated")).Sum(k => k.Value);
        var business = byRole.GetValueOrDefault("domain") + byRole.GetValueOrDefault("application");
        // when no project is a Domain/Application project, business logic lives inside web/library projects: fall back to a folder read
        if (business == 0)
            foreach (var (p, tree) in ctx.Workspace.ProductionTrees)
                if (Regex.IsMatch(ctx.Workspace.RelPath(tree.FilePath), @"(?i)/(Domain|Model|Models|Entities|Services|Application|UseCases|Features|Policies|Rules|Pricing|Billing|Core)/")) business += Workspace.CountLoc(tree);
        var share = production == 0 ? 0 : (double)business / production;
        ctx.Facts["codeComposition"] = string.Join(", ", byRole.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{k.Key} {k.Value}"));
        // business logic in controllers: endpoint methods that compute instead of delegating
        var blic = new List<Finding>();
        foreach (var (p, tree) in ctx.Workspace.ProductionTrees)
        {
            var rel = ctx.Workspace.RelPath(tree.FilePath);
            foreach (var cls in tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>().Where(c => c.Identifier.Text.EndsWith("Controller") || c.BaseList?.Types.Any(b => Cs.Simple(b.Type) is "ControllerBase" or "Controller") == true))
                foreach (var md in cls.Members.OfType<MethodDeclarationSyntax>().Where(m => m.Modifiers.Any(SyntaxKind.PublicKeyword) && m.Body is not null))
                {
                    var stmts = md.Body!.DescendantNodes().OfType<StatementSyntax>().Count(s => s is not BlockSyntax);
                    var logic = md.Body.DescendantNodes().Count(n => n is IfStatementSyntax or SwitchStatementSyntax or CommonForEachStatementSyntax or ForStatementSyntax || n is BinaryExpressionSyntax b && (b.IsKind(SyntaxKind.MultiplyExpression) || b.IsKind(SyntaxKind.DivideExpression) || b.IsKind(SyntaxKind.AddExpression) && !(b.Left is LiteralExpressionSyntax { Token.Value: string }) || b.IsKind(SyntaxKind.SubtractExpression)));
                    var validationOnly = md.Body.DescendantNodes().OfType<IfStatementSyntax>().All(i => i.Statement.DescendantNodesAndSelf().Any(s => s is ReturnStatementSyntax r && r.Expression?.ToString().Contains("BadRequest") == true || s.ToString().Contains("NotFound") || s.ToString().Contains("Problem(") || s.ToString().Contains("Unauthorized") || s.ToString().Contains("Forbid")));
                    if (stmts >= 8 && logic >= 4 && !validationOnly)
                        blic.Add(new Finding("business-logic-in-controller", "AX10", $"{cls.Identifier.Text}.{md.Identifier.Text} holds {logic} decisions/calculations across {stmts} statements: business rules living in the HTTP layer", rel, Cs.Line(md.Identifier), null, 1));
                }
        }
        foreach (var f in blic) ctx.Add(f);
        ctx.Measure("AX10", Math.Min(10, 10 * share / 0.45) * (Shape.FromFindings(blic, ctx.ProductionKloc) / 10), note: $"business-logic share {share:P0} of {production} production LOC; {blic.Count} controller(s) carrying rules");
        // AX5: a recognisable, scale-appropriate structure
        var roles = prod.Select(p => p.Role).Distinct().ToList();
        var layered = roles.Contains(ProjectRole.Domain) && (roles.Contains(ProjectRole.Application) || roles.Contains(ProjectRole.Infrastructure));
        var slices = ctx.Repo.Files.Any(f => Regex.IsMatch(f, @"(?i)/(Features|Slices|UseCases)/[^/]+/"));
        var hexagonal = ctx.Repo.Files.Any(f => Regex.IsMatch(f, @"(?i)/(Ports|Adapters)/"));
        var modular = prod.Count >= 3 && roles.Count >= 2;
        var big = ctx.Workspace.ProductionLoc;
        var checks = new (string name, bool ok)[]
        {
            ("a named style (layered / slices / ports-and-adapters) or a modular split", layered || slices || hexagonal || modular),
            ("modularity matching the size", big < 5000 || prod.Count >= 3 || slices),
            ("a domain or core the rest depends on", roles.Contains(ProjectRole.Domain) || roles.Contains(ProjectRole.Library) || prod.Count == 1 || slices),
            ("no project with everything in it", !prod.Any(p => ctx.Workspace.Projects.First(x => x.Project.Path == p.Path).Trees.Count > 150 && prod.Count > 1)),
        };
        foreach (var (name, ok) in checks) if (!ok) ctx.Add(new Finding("architecture-style-fit", "AX5", $"structure: missing {name}", null, null, null, 1));
        ctx.Measure("AX5", Shape.FromChecklist(checks.Count(c => c.ok), checks.Length), note: $"style: {(layered ? "layered " : "")}{(slices ? "vertical-slices " : "")}{(hexagonal ? "ports-adapters " : "")}{(modular ? "modular" : "")}; {prod.Count} production projects, {big} LOC");
    }
}
