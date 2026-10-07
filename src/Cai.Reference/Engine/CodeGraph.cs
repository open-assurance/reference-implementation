using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cai.Reference.Engine;

/// <summary>File-level dependency graph by declared type names: which production files reference types declared in which others.</summary>
public sealed class CodeGraph
{
    public IReadOnlyDictionary<string, HashSet<string>> DeclaredTypes { get; }     // file → type names declared there
    public IReadOnlyDictionary<string, HashSet<string>> References { get; }        // file → files it depends on (by type name)
    public IReadOnlyDictionary<string, HashSet<string>> Dependents { get; }        // file → files that depend on it
    public IReadOnlyDictionary<string, string> FileProject { get; }                // file → project path
    public IReadOnlyDictionary<string, string> FileNamespace { get; }              // file → first declared namespace

    private CodeGraph(Dictionary<string, HashSet<string>> declared, Dictionary<string, HashSet<string>> refs, Dictionary<string, HashSet<string>> deps, Dictionary<string, string> proj, Dictionary<string, string> ns)
    { DeclaredTypes = declared; References = refs; Dependents = deps; FileProject = proj; FileNamespace = ns; }

    public int FanIn(string file) => Dependents.TryGetValue(file, out var d) ? d.Count : 0;
    public bool DependsOn(string a, string b) => References.TryGetValue(a, out var r) && r.Contains(b);

    public static CodeGraph Build(Workspace ws, bool includeTests = false)
    {
        var declared = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var identifiers = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var proj = new Dictionary<string, string>(StringComparer.Ordinal);
        var ns = new Dictionary<string, string>(StringComparer.Ordinal);
        var trees = includeTests ? ws.AllTrees : ws.ProductionTrees;
        foreach (var (p, tree) in trees)
        {
            var file = ws.RelPath(tree.FilePath); var root = tree.GetRoot();
            proj[file] = p.Project.Path;
            ns[file] = root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString() ?? "";
            declared[file] = root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>().Select(t => t.Identifier.Text).Concat(root.DescendantNodes().OfType<DelegateDeclarationSyntax>().Select(d => d.Identifier.Text)).ToHashSet(StringComparer.Ordinal);
            identifiers[file] = root.DescendantTokens().Where(t => t.IsKind(SyntaxKind.IdentifierToken) && !(t.Parent is BaseTypeDeclarationSyntax)).Select(t => t.Text).ToHashSet(StringComparer.Ordinal);
        }
        var byType = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (file, types) in declared) foreach (var t in types) { if (!byType.TryGetValue(t, out var l)) byType[t] = l = new(); l.Add(file); }
        var refs = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal); var deps = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var (file, ids) in identifiers)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in ids) if (byType.TryGetValue(id, out var files)) foreach (var f in files) if (f != file) set.Add(f);
            refs[file] = set;
            foreach (var f in set) { if (!deps.TryGetValue(f, out var d)) deps[f] = d = new(StringComparer.Ordinal); d.Add(file); }
        }
        return new CodeGraph(declared, refs, deps, proj, ns);
    }
}
