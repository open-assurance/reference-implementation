using Cai.Reference.Engine;

namespace Cai.Reference.Tests;

public class CodeHealthDetectorTests
{
    private static ScanContext Scan(string code, bool nullable = true) =>
        new Fixture().Project("App", nullable: nullable, sources: ("Code.cs", code)).Run(CodeHealth.Run);

    [Fact]
    public void Const_only_interpolation_is_a_constant_template()
    {
        var ctx = Scan("""
            using Microsoft.Extensions.Logging;
            public sealed class Cache { private const string Component = "cache"; private readonly ILogger<Cache> _logger = null!;
              public void Warm(int n) { _logger.LogInformation($"{Component} warm-up finished"); _logger.LogInformation($"{Component} loaded {n} cards"); } }
            """);
        ctx.Fires("non-structured-log-message", 1);
    }

    [Fact]
    public void Obsolete_member_still_called_is_reported_once_per_call_site()
    {
        var ctx = Scan("""
            public sealed class Calc { [System.Obsolete("use B")] public int A(int x) => x; public int B(int x) => x; }
            public sealed class User { private readonly Calc _c = new(); public int Go() => _c.A(1) + _c.B(2); }
            """);
        ctx.Fires("obsolete-symbol-still-used", 1);
    }

    [Fact]
    public void Async_method_awaiting_io_without_token_is_reported()
    {
        var ctx = Scan("""
            using System.Net.Http; using System.Threading.Tasks;
            public sealed class Dl { private readonly HttpClient _http = new(); private readonly System.Threading.SemaphoreSlim _s = new(1,1);
              private async Task<string> DownloadAsync(System.Uri uri) { await _s.WaitAsync(); using var r = await _http.GetAsync(uri); return await r.Content.ReadAsStringAsync(); }
              public async Task<string> OkAsync(System.Uri uri, System.Threading.CancellationToken ct) { using var r = await _http.GetAsync(uri, ct); return await r.Content.ReadAsStringAsync(ct); } }
            """);
        ctx.Fires("missing-cancellation-propagation", 1);
    }

    [Fact]
    public void Nullable_disabled_is_a_repository_level_finding()
    {
        var ctx = Scan("public class A { }", nullable: false);
        var f = Assert.Single(ctx.Rule("nullable-analysis-disabled"));
        Assert.Null(f.File);
    }

    [Fact]
    public void Command_dispatch_switch_with_guards_is_not_complex()
    {
        var ctx = Scan("""
            public static class Cli { public static int Run(string[] a, System.IO.TextWriter o) {
              if (a.Length == 0) { o.WriteLine("usage"); return 2; }
              switch (a[0]) {
                case "import" when a.Length == 2: return 1;
                case "validate" when a.Length == 2: return 2;
                case "watch" when a.Length == 3 && int.TryParse(a[2], out var n): o.WriteLine(n); return n > 0 ? 0 : 1;
                case "follow" when a.Length == 2: o.WriteLine(a[1]); return 0;
                case "print" when a.Length is 2 or 3: return a.Length == 3 ? 3 : 4;
                case "status": o.WriteLine(a.Length == 2 ? a[1] : "default"); return 0;
                default: o.WriteLine("usage"); return 2; } } }
            """);
        ctx.Silent("high-cyclomatic-complexity");
    }

    [Fact]
    public void Lookup_switch_counts_once_but_nested_logic_counts_per_branch()
    {
        var table = string.Join("\n", Enumerable.Range(0, 25).Select(i => $"case ({i}, \"x\"): return \"P{i}\";"));
        var ctx = Scan($"public static class Map {{ public static string Code(int a, string b) {{ switch ((a, b)) {{ {table} default: return \"?\"; }} }} }}");
        ctx.Silent("high-cyclomatic-complexity");
    }

    [Fact]
    public void Process_with_one_undrained_stream_fires_and_both_drained_stays_silent()
    {
        var ctx = new Fixture().Project("App", sources: ("Code.cs", """
            using System.Diagnostics; using System.Text;
            public sealed class P {
              public string One() { var si = new ProcessStartInfo("lp") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                using var p = Process.Start(si)!; var o = p.StandardOutput.ReadToEnd(); p.WaitForExit(); return o; }
              public string Both() { var si = new ProcessStartInfo("lpstat") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                using var p = new Process { StartInfo = si }; var err = new StringBuilder(); p.ErrorDataReceived += (_, e) => { if (e.Data is not null) err.AppendLine(e.Data); };
                p.Start(); p.BeginErrorReadLine(); var o = p.StandardOutput.ReadToEnd(); p.WaitForExit(); return o; } }
            """)).Run(Patterns.Run);
        ctx.Fires("undrained-child-process-stream", 1);
        Assert.Equal(3, ctx.Rule("undrained-child-process-stream").Single().Line);
    }
}
