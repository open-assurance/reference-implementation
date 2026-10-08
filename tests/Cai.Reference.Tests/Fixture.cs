using Cai.Reference.Engine;
using Cai.Reference.Scoring;

namespace Cai.Reference.Tests;

/// <summary>Builds a throwaway repository on disk (no git) from in-memory files and runs the detectors over it.</summary>
public sealed class Fixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "cai-ref-test-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);

    public Fixture Add(string path, string content) { _files[path] = content; return this; }

    /// <summary>A production project with one or more source files (web SDK when asked).</summary>
    public Fixture Project(string name, bool web = false, bool nullable = true, bool test = false, string? extraProps = null, params (string file, string code)[] sources)
    {
        var sdk = web ? "Microsoft.NET.Sdk.Web" : "Microsoft.NET.Sdk";
        var dir = (test ? "tests/" : "src/") + name;
        _files[$"{dir}/{name}.csproj"] = $"""
            <Project Sdk="{sdk}">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>{(nullable ? "enable" : "disable")}</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                {(test ? "<IsTestProject>true</IsTestProject>" : "")}
                {extraProps}
              </PropertyGroup>
              {(test ? "<ItemGroup><PackageReference Include=\"xunit\" Version=\"2.9.3\" /></ItemGroup>" : "")}
            </Project>
            """;
        foreach (var (file, code) in sources) _files[$"{dir}/{file}"] = code;
        return this;
    }

    public ScanContext Run(params Action<ScanContext>[] detectors)
    {
        Directory.CreateDirectory(Root);
        foreach (var (path, content) in _files)
        {
            var full = Path.Combine(Root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
        var repo = Repository.Open(Root);
        var ws = Workspace.Build(repo, null);
        var ctx = new ScanContext
        {
            Repo = repo, Catalog = RubricCatalog.LoadFromArchive(ScoringVectorTests.RubricsDir, "rubric-2026.10.2"), Workspace = ws,
            History = GitHistory.Load(repo), Data = DataSnapshots.Load(FindData()),
        };
        foreach (var d in detectors.Length == 0 ? Detectors.All.ToArray() : detectors) d(ctx);
        return ctx;
    }

    public static string FindData()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "data", "osv-nuget.json"))) dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir!, "data");
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, true); } catch (IOException) { }
        foreach (var d in Directory.GetDirectories(Path.GetDirectoryName(Root)!, Path.GetFileName(Root) + ".out*")) { try { Directory.Delete(d, true); } catch (IOException) { } }
    }
}

public static class ContextAssertions
{
    public static IEnumerable<Finding> Rule(this ScanContext ctx, string concept) => ctx.Findings.Where(f => f.RuleId == concept);
    public static void Fires(this ScanContext ctx, string concept, int times = -1)
    {
        var n = ctx.Rule(concept).Count();
        if (times < 0) Assert.True(n > 0, $"expected '{concept}' to fire; findings: {string.Join("; ", ctx.Findings.Select(f => f.RuleId + "@" + f.File + ":" + f.Line))}");
        else Assert.True(n == times, $"expected '{concept}' ×{times}, got {n}: {string.Join("; ", ctx.Rule(concept).Select(f => f.Message))}");
    }
    public static void Silent(this ScanContext ctx, string concept) =>
        Assert.True(!ctx.Rule(concept).Any(), $"expected '{concept}' to stay silent; got: {string.Join("; ", ctx.Rule(concept).Select(f => f.File + ":" + f.Line + " " + f.Message))}");
}
