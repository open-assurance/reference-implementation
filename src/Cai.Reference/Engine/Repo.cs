using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Cai.Reference.Engine;

public enum ProjectRole { Domain, Application, Infrastructure, Web, Worker, Tool, Library, Test, Benchmark, Unknown }

/// <summary>One C# project as its csproj and the shared build props describe it. Parsed, never built.</summary>
public sealed class ProjectInfo
{
    public required string Path { get; init; }            // repo-relative csproj path
    public required string Name { get; init; }
    public required string Directory { get; init; }       // repo-relative, "" for root
    public string Sdk { get; init; } = "Microsoft.NET.Sdk";
    public List<string> TargetFrameworks { get; init; } = new();
    public string? OutputType { get; init; }
    public bool Nullable { get; init; }
    public bool ImplicitUsings { get; init; }
    public List<string> ExtraUsings { get; init; } = new();
    public bool IsTestProject { get; init; }
    public bool IsPackable { get; init; }
    public string? Version { get; init; }
    public bool LockFile { get; init; }
    public bool RestoreLockedMode { get; init; }
    public bool TreatWarningsAsErrors { get; init; }
    public List<string> NoWarn { get; init; } = new();
    public List<string> WarningsNotAsErrors { get; init; } = new();
    public List<string> DefineConstants { get; init; } = new();
    public List<(string id, string? version)> Packages { get; init; } = new();
    public List<string> ProjectReferences { get; init; } = new();   // repo-relative csproj paths
    public List<string> SourceFiles { get; init; } = new();         // repo-relative .cs
    public List<Regex> CompileRemove { get; init; } = new();        // <Compile Remove="..."> globs, relative to the project directory
    public List<string> MarkupFiles { get; init; } = new();         // repo-relative .razor/.cshtml
    public ProjectRole Role { get; set; }
    public bool IsProduction => Role is not (ProjectRole.Test or ProjectRole.Benchmark);
    public bool HasPackage(string prefix) => Packages.Any(p => p.id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    public bool HasExactPackage(string id) => Packages.Any(p => string.Equals(p.id, id, StringComparison.OrdinalIgnoreCase));
    public string? PackageVersion(string id) => Packages.FirstOrDefault(p => string.Equals(p.id, id, StringComparison.OrdinalIgnoreCase)).version;
}

/// <summary>The repository under measurement: tracked files, projects, head commit. Everything is repo-relative with '/' separators.</summary>
public sealed class Repository
{
    public required string Root { get; init; }
    public required string HeadCommit { get; init; }
    public required DateTimeOffset HeadDate { get; init; }
    public required IReadOnlyList<string> Files { get; init; }
    public required IReadOnlyList<ProjectInfo> Projects { get; init; }
    public IReadOnlyDictionary<string, string> CentralPackageVersions { get; init; } = new Dictionary<string, string>();
    public string? GlobalJsonSdk { get; init; }
    public bool HasGit { get; init; }

    private readonly Dictionary<string, string> _text = new(StringComparer.Ordinal);

    public string Abs(string rel) => System.IO.Path.Combine(Root, rel.Replace('/', System.IO.Path.DirectorySeparatorChar));
    public bool Exists(string rel) => Files.Contains(rel, StringComparer.Ordinal);

    /// <summary>File text, cached; binary files come back empty.</summary>
    public string Text(string rel)
    {
        if (_text.TryGetValue(rel, out var t)) return t;
        var bytes = File.ReadAllBytes(Abs(rel));
        var isBinary = bytes.Length > 0 && bytes.Take(Math.Min(bytes.Length, 8000)).Any(b => b == 0);
        t = isBinary ? "" : System.Text.Encoding.UTF8.GetString(bytes);
        _text[rel] = t;
        return t;
    }

    public bool IsBinary(string rel) { var b = File.ReadAllBytes(Abs(rel)); return b.Length > 0 && b.Take(Math.Min(b.Length, 8000)).Any(x => x == 0); }

    public IEnumerable<string> FilesNamed(params string[] names) => Files.Where(f => names.Any(n => string.Equals(System.IO.Path.GetFileName(f), n, StringComparison.OrdinalIgnoreCase)));
    public IEnumerable<string> FilesWithExtension(params string[] exts) => Files.Where(f => exts.Any(e => f.EndsWith(e, StringComparison.OrdinalIgnoreCase)));
    public IEnumerable<string> FilesUnder(string dir) => Files.Where(f => f.StartsWith(dir.TrimEnd('/') + "/", StringComparison.Ordinal));

    public static bool IsTestPath(string rel)
    {
        var lower = rel.ToLowerInvariant();
        return Regex.IsMatch(lower, @"(^|/)(tests?|test-?projects?|__tests__|spec|specs|benchmarks?)(/|$)") || Regex.IsMatch(lower, @"\.(tests?|unittests?|integrationtests?|specs?|benchmarks?)(/|\.)");
    }

    public static bool IsGeneratedPath(string rel)
    {
        var name = System.IO.Path.GetFileName(rel);
        return name.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".AssemblyInfo.cs", StringComparison.OrdinalIgnoreCase) || name.EndsWith("GlobalUsings.g.cs", StringComparison.OrdinalIgnoreCase)
            || rel.Contains("/obj/", StringComparison.Ordinal) || rel.StartsWith("obj/", StringComparison.Ordinal)
            || rel.Contains("/bin/", StringComparison.Ordinal) || rel.StartsWith("bin/", StringComparison.Ordinal)
            || rel.Contains("/Migrations/", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(name, @"^\d{14}_") ;
    }

    public static bool IsGeneratedText(string text) =>
        text.Length > 0 && (text.AsSpan(0, Math.Min(text.Length, 600)).Contains("<auto-generated", StringComparison.OrdinalIgnoreCase)
                            || text.AsSpan(0, Math.Min(text.Length, 600)).Contains("<autogenerated", StringComparison.OrdinalIgnoreCase));

    public static Repository Open(string root)
    {
        root = System.IO.Path.GetFullPath(root);
        var hasGit = Git.Run(root, "rev-parse --is-inside-work-tree", out var inside) == 0 && inside.Trim() == "true";
        List<string> files;
        string head = ""; DateTimeOffset headDate = default;
        if (hasGit)
        {
            Git.Run(root, "ls-files -z --cached --exclude-standard", out var ls);
            files = ls.Split('\0', StringSplitOptions.RemoveEmptyEntries).Where(f => File.Exists(System.IO.Path.Combine(root, f))).OrderBy(f => f, StringComparer.Ordinal).ToList();
            Git.Run(root, "rev-parse HEAD", out var h); head = h.Trim();
            Git.Run(root, "log -1 --format=%cI HEAD", out var d); DateTimeOffset.TryParse(d.Trim(), null, System.Globalization.DateTimeStyles.RoundtripKind, out headDate);
        }
        else
        {
            files = System.IO.Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Select(f => System.IO.Path.GetRelativePath(root, f).Replace('\\', '/'))
                // without git to say what is tracked: skip build output, package caches, IDE state and this engine's own output directory
                .Where(f => !Regex.IsMatch(f, @"(^|/)(\.git|\.cai-ref|\.vs|\.idea|bin|obj|node_modules|TestResults)/"))
                .OrderBy(f => f, StringComparer.Ordinal).ToList();
        }
        var repo = new RepositoryBuilder(root, files, head, headDate, hasGit);
        return repo.Build();
    }

    private sealed class RepositoryBuilder
    {
        private readonly string _root; private readonly List<string> _files; private readonly string _head; private readonly DateTimeOffset _date; private readonly bool _git;
        public RepositoryBuilder(string root, List<string> files, string head, DateTimeOffset date, bool git) { _root = root; _files = files; _head = head; _date = date; _git = git; }

        private string Read(string rel) => File.ReadAllText(System.IO.Path.Combine(_root, rel));

        public Repository Build()
        {
            var central = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var cpm in _files.Where(f => System.IO.Path.GetFileName(f).Equals("Directory.Packages.props", StringComparison.OrdinalIgnoreCase)))
                foreach (var pv in SafeXml(Read(cpm))?.Descendants().Where(e => e.Name.LocalName == "PackageVersion") ?? Enumerable.Empty<XElement>())
                    if (pv.Attribute("Include")?.Value is { } id && pv.Attribute("Version")?.Value is { } v) central[id] = v;
            string? sdk = null;
            if (_files.Contains("global.json"))
                try { using var gj = System.Text.Json.JsonDocument.Parse(Read("global.json")); if (gj.RootElement.TryGetProperty("sdk", out var s) && s.TryGetProperty("version", out var v)) sdk = v.GetString(); } catch (System.Text.Json.JsonException) { }

            var csprojs = _files.Where(f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)).ToList();
            var projects = new List<ProjectInfo>();
            foreach (var p in csprojs)
            {
                var info = Parse(p, central);
                if (info is not null) projects.Add(info);
            }
            // assign sources: each .cs/.razor belongs to the nearest enclosing project directory
            var byDir = projects.OrderByDescending(p => p.Directory.Length).ToList();
            foreach (var f in _files)
            {
                var isCs = f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
                var isMarkup = f.EndsWith(".razor", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase);
                if (!isCs && !isMarkup) continue;
                if (f.Contains("/obj/") || f.Contains("/bin/") || f.StartsWith("obj/") || f.StartsWith("bin/")) continue;
                var owner = byDir.FirstOrDefault(p => p.Directory.Length == 0 || f.StartsWith(p.Directory + "/", StringComparison.Ordinal));
                if (owner is null) continue;
                var inProject = owner.Directory.Length == 0 ? f : f[(owner.Directory.Length + 1)..];
                if (owner.CompileRemove.Any(rx => rx.IsMatch(inProject))) continue;   // <Compile Remove>: templates and vendored sources the compiler never sees
                if (isCs) owner.SourceFiles.Add(f); else owner.MarkupFiles.Add(f);
            }
            foreach (var p in projects) p.Role = Classify(p, projects);
            return new Repository
            {
                Root = _root, HeadCommit = _head, HeadDate = _date, Files = _files, Projects = projects.OrderBy(p => p.Path, StringComparer.Ordinal).ToList(),
                CentralPackageVersions = central, GlobalJsonSdk = sdk, HasGit = _git,
            };
        }

        private static XDocument? SafeXml(string text) { try { return XDocument.Parse(text); } catch (System.Xml.XmlException) { return null; } }

        private ProjectInfo? Parse(string rel, Dictionary<string, string> central)
        {
            var doc = SafeXml(Read(rel));
            if (doc?.Root is null) return null;
            var dir = System.IO.Path.GetDirectoryName(rel)?.Replace('\\', '/') ?? "";
            // Directory.Build.props chain (root-most first)
            var props = new List<XDocument>();
            var parts = dir.Length == 0 ? Array.Empty<string>() : dir.Split('/');
            for (var i = parts.Length; i >= 0; i--)
            {
                var d = string.Join('/', parts.Take(i));
                var candidate = d.Length == 0 ? "Directory.Build.props" : d + "/Directory.Build.props";
                if (_files.Contains(candidate) && SafeXml(Read(candidate)) is { } x) props.Insert(0, x);
            }
            var all = props.Append(doc).ToList();
            var projectName = System.IO.Path.GetFileNameWithoutExtension(rel);
            // only groups whose Condition holds for THIS project contribute (a shared Directory.Build.props scopes test defaults by project name)
            IEnumerable<XElement> Elems(string localName) => all.SelectMany(x => x.Descendants().Where(e => e.Name.LocalName == localName && MsBuildCondition.Applies(e, projectName)));
            string? Prop(string name) => Elems(name).Where(e => !e.HasElements).Select(e => e.Value.Trim()).LastOrDefault(v => v.Length > 0);
            var tfms = (Prop("TargetFrameworks") ?? Prop("TargetFramework") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            var packages = Elems("PackageReference")
                .Select(e => (id: e.Attribute("Include")?.Value ?? e.Attribute("Update")?.Value ?? "", version: e.Attribute("Version")?.Value ?? e.Element(e.Name.Namespace + "Version")?.Value))
                .Where(p => p.id.Length > 0)
                .Select(p => (p.id, p.version ?? central.GetValueOrDefault(p.id)))
                .GroupBy(p => p.id, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
            var projRefs = doc.Descendants().Where(e => e.Name.LocalName == "ProjectReference").Select(e => e.Attribute("Include")?.Value).Where(v => v is not null)
                .Select(v => NormalizePath(dir, v!)).ToList();
            var name = System.IO.Path.GetFileNameWithoutExtension(rel);
            var isTest = string.Equals(Prop("IsTestProject"), "true", StringComparison.OrdinalIgnoreCase)
                || packages.Any(p => p.Item1.StartsWith("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase) || p.Item1.StartsWith("xunit", StringComparison.OrdinalIgnoreCase)
                                     || p.Item1.StartsWith("NUnit", StringComparison.OrdinalIgnoreCase) || p.Item1.StartsWith("MSTest", StringComparison.OrdinalIgnoreCase)
                                     || p.Item1.StartsWith("TUnit", StringComparison.OrdinalIgnoreCase));
            return new ProjectInfo
            {
                Path = rel, Name = name, Directory = dir,
                Sdk = doc.Root.Attribute("Sdk")?.Value ?? "Microsoft.NET.Sdk",
                TargetFrameworks = tfms, OutputType = Prop("OutputType"),
                Nullable = string.Equals(Prop("Nullable"), "enable", StringComparison.OrdinalIgnoreCase),
                ImplicitUsings = Prop("ImplicitUsings") is { } iu && (iu.Equals("enable", StringComparison.OrdinalIgnoreCase) || iu.Equals("true", StringComparison.OrdinalIgnoreCase)),
                ExtraUsings = Elems("Using").Where(e => e.Attribute("Include") is not null && e.Attribute("Remove") is null).Select(e => e.Attribute("Include")!.Value.Trim()).Where(u => u.Length > 0 && !u.StartsWith("$")).ToList(),
                CompileRemove = Elems("Compile").Select(e => e.Attribute("Remove")?.Value).Where(v => !string.IsNullOrWhiteSpace(v) && !v!.Contains("$(")).Select(v => GlobToRegex(v!)).ToList(),
                IsTestProject = isTest,
                IsPackable = string.Equals(Prop("IsPackable"), "true", StringComparison.OrdinalIgnoreCase) || string.Equals(Prop("GeneratePackageOnBuild"), "true", StringComparison.OrdinalIgnoreCase) || Prop("PackageId") is not null,
                Version = Prop("Version") ?? Prop("VersionPrefix"),
                LockFile = _files.Contains((dir.Length == 0 ? "" : dir + "/") + "packages.lock.json") || string.Equals(Prop("RestorePackagesWithLockFile"), "true", StringComparison.OrdinalIgnoreCase),
                RestoreLockedMode = string.Equals(Prop("RestoreLockedMode"), "true", StringComparison.OrdinalIgnoreCase),
                TreatWarningsAsErrors = string.Equals(Prop("TreatWarningsAsErrors"), "true", StringComparison.OrdinalIgnoreCase),
                NoWarn = (Prop("NoWarn") ?? "").Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(w => !w.StartsWith("$")).ToList(),
                WarningsNotAsErrors = (Prop("WarningsNotAsErrors") ?? "").Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(w => !w.StartsWith("$")).ToList(),
                DefineConstants = Elems("DefineConstants").SelectMany(e => e.Value.Split(';')).Select(s => s.Trim()).Where(s => s.Length > 0 && !s.StartsWith("$")).Distinct().ToList(),
                Packages = packages, ProjectReferences = projRefs,
            };
        }

        /// <summary>An MSBuild item glob (`**` any depth, `*` within a segment, `\` or `/`) as a regex over the project-relative path.</summary>
        private static Regex GlobToRegex(string glob)
        {
            var g = glob.Replace('\\', '/').Trim().TrimStart('.', '/');
            var sb = new System.Text.StringBuilder("^");
            for (var i = 0; i < g.Length; i++)
            {
                if (g[i] == '*' && i + 1 < g.Length && g[i + 1] == '*') { sb.Append(".*"); i++; if (i + 1 < g.Length && g[i + 1] == '/') i++; }
                else if (g[i] == '*') sb.Append("[^/]*");
                else if (g[i] == '?') sb.Append("[^/]");
                else sb.Append(Regex.Escape(g[i].ToString()));
            }
            return new Regex(sb.Append(g.EndsWith("**") ? "" : "$").ToString(), RegexOptions.IgnoreCase | RegexOptions.Compiled);
        }

        private static string NormalizePath(string dir, string include)
        {
            var combined = dir.Length == 0 ? include : dir + "/" + include;
            var segs = new List<string>();
            foreach (var s in combined.Replace('\\', '/').Split('/'))
            {
                if (s == "." || s.Length == 0) continue;
                if (s == "..") { if (segs.Count > 0) segs.RemoveAt(segs.Count - 1); continue; }
                segs.Add(s);
            }
            return string.Join('/', segs);
        }

        private static ProjectRole Classify(ProjectInfo p, List<ProjectInfo> all)
        {
            var n = p.Name.ToLowerInvariant();
            var dir = p.Directory.ToLowerInvariant();
            if (p.IsTestProject || IsTestPath(p.Path)) return p.HasPackage("BenchmarkDotNet") ? ProjectRole.Benchmark : ProjectRole.Test;
            if (p.HasPackage("BenchmarkDotNet") || n.EndsWith(".benchmarks") || n.EndsWith(".benchmark")) return ProjectRole.Benchmark;
            if (p.Sdk.Contains("Web", StringComparison.OrdinalIgnoreCase) || p.Sdk.Contains("BlazorWebAssembly", StringComparison.OrdinalIgnoreCase) || p.Sdk.Contains("Razor", StringComparison.OrdinalIgnoreCase)) return ProjectRole.Web;
            // the LAST name segment is the most specific statement of a project's role (Company.Kernel.Runtime is a runtime, not a kernel);
            // a project that carries a database or broker driver is infrastructure whatever it is called
            var last = "." + n.Split('.').Last();
            if (Regex.IsMatch(last, @"^\.(runtime|infrastructure|infra|persistence|data|dataaccess|messaging|storage|adapters?|external|integrations?|efcore|marten|dapper|mongo|redis)$")) return ProjectRole.Infrastructure;
            if (Regex.IsMatch(last, @"^\.(contracts|abstractions|shared|common|messages|events|dto|dtos|client|sdk|generators?|analyzers?)$")) return ProjectRole.Library;
            if (Regex.IsMatch(n, @"\.(domain|core|model|models|entities|kernel|sharedkernel)(\.|$)")) return ProjectRole.Domain;
            if (Regex.IsMatch(n, @"\.(application|app|usecases|services|handlers|features)(\.|$)")) return ProjectRole.Application;
            if (p.Packages.Any(x => Regex.IsMatch(x.id, @"(?i)^(Marten|Microsoft\.EntityFrameworkCore(\..*)?|Npgsql(\..*)?|Dapper|MongoDB\.Driver|RabbitMQ\.Client|MassTransit(\..*)?|StackExchange\.Redis|Confluent\.Kafka|Azure\.Messaging\..*|AWSSDK\..*)$")) && !Regex.IsMatch(n, @"\.(api|web|server|host|worker)(\.|$)")) return ProjectRole.Infrastructure;
            if (Regex.IsMatch(n, @"\.(infrastructure|infra|persistence|data|dataaccess|messaging|storage|adapters?|external|integrations?)(\.|$)")) return ProjectRole.Infrastructure;
            if (Regex.IsMatch(n, @"\.(worker|workers|jobs|host|hosting|service|functions)(\.|$)") || p.HasPackage("Microsoft.Extensions.Hosting") && p.OutputType?.Equals("Exe", StringComparison.OrdinalIgnoreCase) == true) return ProjectRole.Worker;
            if (Regex.IsMatch(n, @"\.(cli|tool|tools|console|migrator|importer|exporter)(\.|$)") || dir.StartsWith("tools/")) return ProjectRole.Tool;
            if (Regex.IsMatch(n, @"\.(api|web|webapi|server|gateway|bff|ui|blazor|pages|mvc)(\.|$)")) return ProjectRole.Web;
            if (p.OutputType?.Equals("Exe", StringComparison.OrdinalIgnoreCase) == true) return ProjectRole.Tool;
            if (Regex.IsMatch(n, @"\.(contracts|abstractions|shared|common|messages|events|dto|dtos|client|sdk)(\.|$)")) return ProjectRole.Library;
            return ProjectRole.Library;
        }
    }
}

public static class Git
{
    public static int Run(string cwd, string args, out string stdout, int timeoutMs = 600_000)
    {
        var psi = new ProcessStartInfo("git", args) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = System.Text.Encoding.UTF8 };
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0"; psi.Environment["LC_ALL"] = "C";
        using var p = Process.Start(psi)!;
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } stdout = ""; return -1; }
        stdout = outTask.Result; _ = errTask.Result;
        return p.ExitCode;
    }
}


/// <summary>
/// The subset of MSBuild conditions a props chain uses to scope groups to projects: string (in)equality, And/Or/!, parentheses,
/// and $(MSBuildProjectName) with .Contains/.StartsWith/.EndsWith. Every other property is undefined, hence empty, as in MSBuild.
/// A condition this evaluator cannot parse is treated as not holding.
/// </summary>
public static class MsBuildCondition
{
    public static bool Applies(XElement e, string projectName)
    {
        for (var x = e; x is not null; x = x.Parent)
        {
            var c = x.Attribute("Condition")?.Value;
            if (c is not null && !Evaluate(c, projectName)) return false;
        }
        return true;
    }

    public static bool Evaluate(string condition, string projectName)
    {
        try { var p = new Parser(Substitute(condition, projectName)); var v = p.ParseOr(); p.ExpectEnd(); return IsTrue(v); }
        catch (FormatException) { return false; }
    }

    private static string Substitute(string c, string name)
    {
        c = Regex.Replace(c, @"\$\(MSBuildProjectName\.Contains\('([^']*)'\)\)", m => name.Contains(m.Groups[1].Value, StringComparison.OrdinalIgnoreCase) ? "true" : "false", RegexOptions.IgnoreCase);
        c = Regex.Replace(c, @"\$\(MSBuildProjectName\.StartsWith\('([^']*)'\)\)", m => name.StartsWith(m.Groups[1].Value, StringComparison.OrdinalIgnoreCase) ? "true" : "false", RegexOptions.IgnoreCase);
        c = Regex.Replace(c, @"\$\(MSBuildProjectName\.EndsWith\('([^']*)'\)\)", m => name.EndsWith(m.Groups[1].Value, StringComparison.OrdinalIgnoreCase) ? "true" : "false", RegexOptions.IgnoreCase);
        c = Regex.Replace(c, @"\$\(MSBuildProjectName\)", name, RegexOptions.IgnoreCase);
        c = Regex.Replace(c, @"\$\([^()]*(\([^()]*\)[^()]*)*\)", "");   // any other property: undefined → empty
        return c;
    }

    private static bool IsTrue(string v) => v.Equals("true", StringComparison.OrdinalIgnoreCase);

    private sealed class Parser(string s)
    {
        private int _i;
        private void Ws() { while (_i < s.Length && char.IsWhiteSpace(s[_i])) _i++; }
        private bool Word(string w) { Ws(); if (string.Compare(s, _i, w, 0, w.Length, StringComparison.OrdinalIgnoreCase) == 0 && (_i + w.Length == s.Length || !char.IsLetterOrDigit(s[_i + w.Length]))) { _i += w.Length; return true; } return false; }
        private bool Sym(string w) { Ws(); if (string.CompareOrdinal(s, _i, w, 0, w.Length) == 0) { _i += w.Length; return true; } return false; }
        public void ExpectEnd() { Ws(); if (_i != s.Length) throw new FormatException(); }
        public string ParseOr() { var v = ParseAnd(); while (Word("or")) { var r = ParseAnd(); v = IsTrue(v) || IsTrue(r) ? "true" : "false"; } return v; }
        private string ParseAnd() { var v = ParseNot(); while (Word("and")) { var r = ParseNot(); v = IsTrue(v) && IsTrue(r) ? "true" : "false"; } return v; }
        private string ParseNot() { if (Sym("!") && !(_i < s.Length && s[_i] == '=')) return IsTrue(ParseNot()) ? "false" : "true"; return ParseCmp(); }
        private string ParseCmp()
        {
            var l = Primary();
            if (Sym("==")) return l.Equals(Primary(), StringComparison.OrdinalIgnoreCase) ? "true" : "false";
            if (Sym("!=")) return l.Equals(Primary(), StringComparison.OrdinalIgnoreCase) ? "false" : "true";
            return l;
        }
        private string Primary()
        {
            Ws();
            if (Sym("(")) { var v = ParseOr(); if (!Sym(")")) throw new FormatException(); return v; }
            if (_i < s.Length && s[_i] == '\'') { var end = s.IndexOf('\'', _i + 1); if (end < 0) throw new FormatException(); var v = s[(_i + 1)..end]; _i = end + 1; return v; }
            var start = _i; while (_i < s.Length && (char.IsLetterOrDigit(s[_i]) || s[_i] is '.' or '_' or '-')) _i++;
            if (_i == start) throw new FormatException();
            return s[start.._i];
        }
    }
}
