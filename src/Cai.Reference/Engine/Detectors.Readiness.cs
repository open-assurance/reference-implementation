using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cai.Reference.Engine;

/// <summary>Readiness: D9 D10 D11 R4 P9 (tests), D12 D14 (dependencies), P1–P8 P10–P12 (pipeline, operations), PF1 PF2 (performance), X8 (JS interop).</summary>
public static class Readiness
{
    public static void Run(ScanContext ctx)
    {
        Tests(ctx); Dependencies(ctx); Pipeline(ctx); Operations(ctx); Performance(ctx); JsInterop(ctx);
    }

    // ---------------- tests: D9 D10 D11 R4 P9 ----------------
    private static readonly Regex TestAttr = new(@"^(Fact|Theory|Test|TestMethod|TestCase|TestCaseSource|DataTestMethod|Scenario|Property|InlineData|SkippableFact|SkippableTheory|ParameterizedTest|Benchmark)(Attribute)?$", RegexOptions.Compiled);
    private static readonly Regex Assertion = new(@"\b(Assert|Should\w*|Shall|Verify|Expect|Received|DidNotReceive|Snapshot|Approvals|Check\.|Throws|ThrowsAsync|DoesNotThrow|ExpectedException|Matches|Satisf|MustHave|IsEqual|AreEqual|AreNotEqual|IsTrue|IsFalse|IsNull|IsNotNull|Contains|StartsWith|EndsWith|Equal|NotEqual|MustBe|Be\(|BeEquivalentTo|HaveCount|NotBeNull|BeNull|BeTrue|BeFalse|Ensure|ValidateAsync|Accept\(|VerifyAll|VerifyNoOtherCalls|Sample\(|Prop\.ForAll|QuickCheck|Arb\.|Gen\.|ForAll)\b", RegexOptions.Compiled);
    private static readonly Regex TrivialAssert = new(@"Assert\.(True|IsTrue|That)\(\s*true\s*[,)]|Assert\.(False|IsFalse)\(\s*false\s*[,)]|Assert\.Pass\(|Assert\.Inconclusive\(|\.Should\(\)\.NotBeNull\(\)\s*;\s*\}", RegexOptions.Compiled);
    private static readonly Regex MockSetup = new(@"\.Setup\(|\.SetupGet\(|\.SetupSequence\(|Substitute\.For<|new Mock<|A\.Fake<|A\.CallTo\(|\.Returns\(|\.ReturnsAsync\(|\.Callback\(|\.Raises\(|Mock\.Of<", RegexOptions.Compiled);

    private static void Tests(ScanContext ctx)
    {
        var testProjects = ctx.Workspace.Tests.ToList();
        var dims = new[] { "D9", "D10", "D11", "R4", "P9" };
        if (testProjects.Count == 0)
        {
            ctx.Add(new Finding("test-coverage", "R4", "no test project in the repository", null, null, null, 2));
            ctx.Measure("R4", 0, note: "no tests"); ctx.Measure("D9", 0, note: "no tests");
            foreach (var d in new[] { "D10", "D11", "P9" }) ctx.Skip(d, "no test projects");
            return;
        }
        int unit = 0, integration = 0, e2e = 0, bdd = 0, total = 0, noAssert = 0, skipped = 0, flaky = 0, swallowed = 0, mocky = 0;
        foreach (var tp in testProjects)
        {
            var p = tp.Project;
            var level = p.HasPackage("Microsoft.Playwright") || p.HasPackage("Selenium") || p.HasPackage("PuppeteerSharp") || Regex.IsMatch(p.Name, @"(?i)e2e|endtoend|end-to-end|ui\.?tests|acceptance|system\.?tests") ? "e2e"
                : p.HasPackage("Microsoft.AspNetCore.Mvc.Testing") || p.HasPackage("Testcontainers") || p.HasPackage("Microsoft.AspNetCore.TestHost") || p.HasPackage("Respawn") || p.HasPackage("Aspire.Hosting.Testing") || Regex.IsMatch(p.Name, @"(?i)integration|contract|component\.?tests") ? "integration" : "unit";
            var isBdd = p.HasPackage("SpecFlow") || p.HasPackage("Reqnroll") || p.HasPackage("Xunit.Gherkin") || p.HasPackage("LightBDD") || p.HasPackage("TickSpec");
            foreach (var tree in tp.Trees.Where(t => !tp.IsGenerated(t)))
            {
                var rel = ctx.Workspace.RelPath(tree.FilePath); var root = tree.GetRoot();
                foreach (var cls in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
                {
                    var classText = cls.ToString();
                    var methods = cls.Members.OfType<MethodDeclarationSyntax>().Where(md => md.AttributeLists.SelectMany(a => a.Attributes).Any(a => TestAttr.IsMatch(Cs.Simple(a.Name)))).ToList();
                    if (methods.Count == 0 && isBdd && Regex.IsMatch(classText, @"\[(Binding|Given|When|Then)\b")) { bdd += cls.Members.OfType<MethodDeclarationSyntax>().Count(); continue; }
                    var classMocks = MockSetup.Matches(classText).Count; var classAsserts = Assertion.Matches(classText).Count;
                    if (methods.Count >= 2 && classMocks >= 6 && classMocks > 2 * Math.Max(1, classAsserts))
                    { mocky++; var loc = ctx.Loc(cls.Identifier); ctx.Add(new Finding("excessive-mocking", "D10", $"{cls.Identifier.Text}: {classMocks} mock setups against {classAsserts} assertions — the tests verify the mocks' choreography, not behaviour", loc.File, loc.Line, loc.Line, 1)); }
                    foreach (var md in methods)
                    {
                        total++;
                        if (level == "e2e") e2e++; else if (level == "integration") integration++; else unit++;
                        var body = Cs.BodyOf(md)?.ToString() ?? ""; var loc = ctx.Loc(md.Identifier);
                        var skipArg = md.AttributeLists.SelectMany(a => a.Attributes).SelectMany(a => a.ArgumentList?.Arguments ?? default).FirstOrDefault(a => a.NameEquals?.Name.Identifier.Text is "Skip" or "Ignore" or "Reason" || a.NameEquals is null && Cs.Simple(((AttributeSyntax)a.Parent!.Parent!).Name) is "Ignore" or "Skip");
                        var ignoreAttr = md.AttributeLists.SelectMany(a => a.Attributes).FirstOrDefault(a => Cs.Simple(a.Name) is "Ignore" or "Skip" or "Explicit");
                        if (skipArg is not null || ignoreAttr is not null)
                        {
                            var reason = (skipArg?.Expression as LiteralExpressionSyntax)?.Token.ValueText ?? (ignoreAttr?.ArgumentList?.Arguments.FirstOrDefault()?.Expression as LiteralExpressionSyntax)?.Token.ValueText ?? "";
                            var structured = reason.Length >= 15 && Regex.IsMatch(reason, @"#\d+|[A-Z]{2,}-\d+|https?://|issue|ticket|bug|until|blocked by|see ", RegexOptions.IgnoreCase);
                            if (!structured) { skipped++; ctx.Add(new Finding("skipped-test-without-reason", "D10", $"{md.Identifier.Text} is skipped{(reason.Length > 0 ? $" with the reason \"{reason}\"" : " with no reason")}: nobody can tell whether it is broken, unfinished or obsolete", loc.File, loc.Line, loc.Line, 1)); }
                            continue;
                        }
                        var mocksHere = Regex.Matches(body, @"Substitute\.For<|new Mock<|A\.Fake<|Mock\.Of<").Count;
                        var interactionOnly = mocksHere >= 4 && Regex.IsMatch(body, @"Received\(|\.Verify\(|A\.CallTo\(|MustHaveHappened") && !Regex.IsMatch(Regex.Replace(body, @"Received\([^;]*;|\.Verify\([^;]*;|MustHaveHappened[^;]*;", ""), @"Assert\.|ShouldBe|Should\(\)|Equal\(|\.Be\(");
                        if (interactionOnly) { mocky++; ctx.Add(new Finding("excessive-mocking", "D10", $"{md.Identifier.Text} substitutes {mocksHere} collaborators and asserts only that they were called: it pins the wiring, not the behaviour", loc.File, loc.Line, loc.Line, 1)); }
                        var hasAssertion = Assertion.IsMatch(body) && !Regex.IsMatch(body, @"^\s*\{?\s*$");
                        var expectsThrow = md.AttributeLists.ToString().Contains("ExpectedException") || Regex.IsMatch(body, @"Throws|ThrowAsync|ThrowExactly|Should\(\)\.Throw");
                        if (TrivialAssert.IsMatch(body) && Assertion.Matches(body).Count <= 1 || !hasAssertion && !expectsThrow && body.Length > 2)
                        { noAssert++; ctx.Add(new Finding("test-without-assertion", "D10", $"{md.Identifier.Text} {(TrivialAssert.IsMatch(body) ? "asserts something true by construction" : "asserts nothing")}: it passes as long as nothing throws", loc.File, loc.Line, loc.Line, 1)); }
                        if (Regex.IsMatch(body, @"catch\s*(\([^)]*\))?\s*\{[^}]*\}") && !Regex.IsMatch(body, @"catch\s*(\([^)]*\))?\s*\{[^}]*(throw|Assert\.Fail|Fail\(|Should\(\)\.Throw)[^}]*\}") && Regex.IsMatch(body, @"try\s*\{[^}]*(Assert|Should|Verify)"))
                        { swallowed++; ctx.Add(new Finding("test-failure-swallowed", "D10", $"{md.Identifier.Text} wraps its assertions in a try/catch that swallows the failure", loc.File, loc.Line, loc.Line, 1)); }
                        var flakyReason = body switch
                        {
                            _ when Regex.IsMatch(body, @"\bThread\.Sleep\(") => "sleeps to wait for a result",
                            _ when Regex.IsMatch(body, @"await\s+Task\.Delay\(") && Regex.IsMatch(body, @"Assert|Should") && !Regex.IsMatch(body, @"while\s*\(|for\s*\(|do\s*\{|SpinWait|WaitUntil|Poll") => "waits with Task.Delay instead of synchronising",
                            _ when Regex.IsMatch(Regex.Replace(body, @"new\s+FakeTimeProvider\([^)]*\)", ""), @"\bDateTime\.(Now|Today)\b|\bDateTimeOffset\.Now\b") => "reads the LOCAL wall clock (time-zone and DST dependent)",
                            _ when Regex.IsMatch(body, @"(?m)^.*(Assert\.|ShouldBe|Should\(\)|\.Be\().*(DateTime|DateTimeOffset)\.(UtcNow|Now|Today)") => "asserts against the wall clock",
                            _ when Regex.IsMatch(body, @"new Random\(\s*\)|Random\.Shared") && !RandomOnlyShapesData(body) => "uses unseeded randomness",
                            _ when Regex.IsMatch(body, @"\bStopwatch\b") && Regex.IsMatch(body, @"Elapsed\w*\s*(<|>|<=|>=)") && !Regex.IsMatch(classText, @"FakeTimeProvider|ITimeProvider|TestTimeProvider") => "asserts on elapsed time",
                            _ when Regex.IsMatch(body, @"https?://(?!localhost|127\.0\.0\.1)[\w.-]+") && Regex.IsMatch(body, @"new HttpClient\s*(\(\s*\))?\s*\{|new HttpClient\(\s*\)|new HttpClient\(new (Http|Sockets)Http(Client)?Handler") && !Regex.IsMatch(body, @"MockHttp|FakeHandler|StubHandler|TestHandler|HttpMessageHandler|WebApplicationFactory|TestServer|RichardSzalay|handler") => "calls a real network host",
                            _ when Regex.IsMatch(body, @"Path\.GetTempPath\(\)|""/tmp/|@""C:\\") && !Regex.IsMatch(body, @"Guid\.NewGuid|Random|GetRandomFileName|GetTempFileName") => "writes to a fixed shared path",
                            _ when Regex.IsMatch(body, @"Task\.Run\(|Parallel\.") && Regex.IsMatch(classText, @"static\s+(?!readonly)(?!class)[\w<>]+\s+_?\w+\s*(=|;)") => "mutates static state from parallel work",
                            _ when Regex.IsMatch(body, @"\.First\(\)|\[0\]") && Regex.IsMatch(body, @"HashSet<|Dictionary<|ToHashSet\(\)") && !Regex.IsMatch(body, @"OrderBy|Sort") => "relies on the ordering of an unordered collection",
                            _ when Regex.IsMatch(body, @"Environment\.(MachineName|UserName|GetEnvironmentVariable)") => "depends on the machine or environment",
                            _ => null,
                        };
                        if (flakyReason is not null) { flaky++; ctx.Add(new Finding("flaky-test", "D11", $"{md.Identifier.Text} {flakyReason}: its result can change from run to run", loc.File, loc.Line, loc.Line, 1)); }
                    }
                }
            }
        }
        ctx.Facts["tests"] = $"unit {unit}, integration {integration}, e2e {e2e}, bdd {bdd}";
        if (total == 0) { ctx.Measure("D9", 0, note: "test projects exist but no test methods were found"); foreach (var d in new[] { "D10", "D11" }) ctx.Skip(d, "no test methods found"); }
        else
        {
            var unitShare = (double)unit / total;
            var pyramid = new (string, bool)[] { ("a unit-test base (≥ 50 % of tests)", unitShare >= 0.5), ("some integration tests", integration > 0 || total < 15), ("more unit than integration tests", unit >= integration), ("no inverted pyramid (e2e fewer than integration)", e2e <= Math.Max(integration, 1)) };
            foreach (var (name, ok) in pyramid) if (!ok) ctx.Add(new Finding("test-pyramid-distribution", "D9", $"test distribution: missing {name} ({unit} unit / {integration} integration / {e2e} e2e)", null, null, null, 1));
            ctx.Measure("D9", Shape.FromChecklist(pyramid.Count(c => c.Item2), pyramid.Length), note: $"{unit} unit / {integration} integration / {e2e} e2e / {bdd} BDD steps");
            ctx.Measure("D10", Shape.FromShare(1 - Math.Min(1.0, (noAssert + skipped + swallowed + mocky * 2) / (double)total * 2)), note: $"{noAssert} assertion-free, {skipped} skipped without reason, {swallowed} swallowed, {mocky} mock-dominated classes of {total} tests");
            ctx.Measure("D11", Shape.FromShare(1 - Math.Min(1.0, flaky / (double)total * 3)), note: $"{flaky} of {total} tests carry a non-determinism pattern");
        }
        // R4 / P9: static reachability of production files from the tests
        var graph = CodeGraph.Build(ctx.Workspace, includeTests: true);
        var testFiles = graph.FileProject.Where(kv => ctx.Repo.Projects.First(p => p.Path == kv.Value).Role == ProjectRole.Test).Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);
        var prodFiles = graph.FileProject.Keys.Where(f => !testFiles.Contains(f)).ToList();
        var reached = new HashSet<string>(StringComparer.Ordinal); var frontier = new Queue<string>();
        foreach (var t in testFiles) foreach (var d in graph.References.GetValueOrDefault(t) ?? new()) if (!testFiles.Contains(d) && reached.Add(d)) frontier.Enqueue(d);
        while (frontier.Count > 0) foreach (var d in graph.References.GetValueOrDefault(frontier.Dequeue()) ?? new()) if (!testFiles.Contains(d) && reached.Add(d)) frontier.Enqueue(d);
        var direct = testFiles.SelectMany(t => graph.References.GetValueOrDefault(t) ?? new()).Where(d => !testFiles.Contains(d)).ToHashSet(StringComparer.Ordinal);
        // tests that HOST the application (WebApplicationFactory, TestServer) run its composition root and everything it wires up over HTTP,
        // which the import graph cannot see: infrastructure in the hosted graph counts as reached; domain and application logic still needs a test that names it
        var hostedProjects = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tp in ctx.Workspace.Tests.Where(t => t.Trees.Any(tr => Regex.IsMatch(tr.GetRoot().ToString(), @"WebApplicationFactory<|\bTestServer\b|DistributedApplicationTestingBuilder"))))
            foreach (var r in tp.Project.ProjectReferences.Where(r => ctx.Repo.Projects.Any(p => p.Path == r && p.Role is ProjectRole.Web or ProjectRole.Tool)))
            { hostedProjects.Add(r); foreach (var tr in Workspace.TransitiveReferences(ctx.Repo.Projects.First(p => p.Path == r), ctx.Repo)) hostedProjects.Add(tr); }
        bool HostedInfrastructure(string f) { var pr = ctx.Repo.Projects.First(p => p.Path == graph.FileProject[f]); return hostedProjects.Contains(pr.Path) && pr.Role == ProjectRole.Infrastructure; }
        // only files with logic in them count: contracts, options and middleware are exercised over HTTP, which static reachability cannot see
        var complexity = ctx.Workspace.ProductionTrees.ToDictionary(t => ctx.Workspace.RelPath(t.tree.FilePath), t => Cs.MethodLike(t.tree.GetRoot()).Select(Cs.BodyOf).Where(b => b is not null).Select(b => CodeHealth.Cyclomatic(b!)).DefaultIfEmpty(0).Max(), StringComparer.Ordinal);
        var methodCounts = ctx.Workspace.ProductionTrees.ToDictionary(t => ctx.Workspace.RelPath(t.tree.FilePath), t => Cs.MethodLike(t.tree.GetRoot()).Count(m => Cs.BodyOf(m) is not null), StringComparer.Ordinal);
        var logicFiles = prodFiles.Where(f => (complexity.GetValueOrDefault(f) >= 3 || methodCounts.GetValueOrDefault(f) >= 3 && complexity.GetValueOrDefault(f) >= 2) && !Regex.IsMatch(f, @"(?i)/(Contracts|Dtos?|Requests?|Responses?|Options|Settings|Migrations|Hosting|Program\.cs|Startup\.cs|Middleware)")).ToList();
        // web projects are exercised over HTTP, which the import graph cannot see: only library-side logic is flagged, and only when NO test names it
        var flaggable = logicFiles.Where(f => ctx.Repo.Projects.First(p => p.Path == graph.FileProject[f]).Role is not (ProjectRole.Web or ProjectRole.Tool) && !HostedInfrastructure(f)).ToList();
        var unreached = flaggable.Where(f => !direct.Contains(f)).ToList();
        foreach (var f in unreached.OrderBy(f => f, StringComparer.Ordinal))
            ctx.Add(new Finding("test-coverage", "R4", $"{Path.GetFileName(f)} holds logic (complexity {complexity[f]}) and no test references it{(reached.Contains(f) ? " directly (only through a tested caller)" : ", even transitively")}", f, null, null, reached.Contains(f) ? 0.5 : 1));
        var reach = logicFiles.Count == 0 ? 1 : (double)logicFiles.Count(f => reached.Contains(f) || HostedInfrastructure(f)) / logicFiles.Count;
        ctx.Measure("R4", Shape.FromShare(reach), note: $"{logicFiles.Count(f => reached.Contains(f) || HostedInfrastructure(f))} of {logicFiles.Count} logic-bearing production files reachable from tests ({direct.Count} files directly, {hostedProjects.Count} project(s) hosted by integration tests)");
        string RoleOf(string f) { var p = ctx.Repo.Projects.First(x => x.Path == graph.FileProject[f]); return p.Role is ProjectRole.Domain or ProjectRole.Application ? "domain" : p.Role == ProjectRole.Web && Regex.IsMatch(f, @"(?i)/(Controllers|Endpoints|Pages|Components)/") ? "web" : Regex.IsMatch(f, @"(?i)/(Domain|Application|Services|Pricing|Rules|Policies|Features)/") ? "domain" : "other"; }
        var domainFiles = prodFiles.Where(f => RoleOf(f) == "domain").ToList(); var webFiles = prodFiles.Where(f => RoleOf(f) == "web").ToList();
        if (domainFiles.Count == 0) ctx.Skip("P9", "no domain or application layer to compare controller coverage against");
        else
        {
            var domainReach = (double)domainFiles.Count(reached.Contains) / domainFiles.Count; var webReach = webFiles.Count == 0 ? 0 : (double)webFiles.Count(reached.Contains) / webFiles.Count;
            var domainDirect = (double)domainFiles.Count(direct.Contains) / domainFiles.Count;
            if (domainDirect < 0.5) ctx.Add(new Finding("domain-vs-controller-coverage", "P9", $"only {domainDirect:P0} of domain/application files are tested directly ({domainReach:P0} reachable); controllers reach {webReach:P0}", null, null, null, 1));
            ctx.Measure("P9", Shape.FromShare(Math.Min(1, 0.5 * domainReach + 0.5 * Math.Min(1, domainDirect / 0.6))), note: $"domain reach {domainReach:P0} ({domainDirect:P0} direct) vs web reach {webReach:P0}");
        }
    }

    // ---------------- D12 D14 ----------------
    private static void Dependencies(ScanContext ctx)
    {
        var prod = ctx.Repo.Projects.Where(p => p.IsProduction).ToList();
        var packages = ctx.Repo.Projects.SelectMany(p => p.Packages.Select(pk => (p, pk.id, pk.version))).Where(x => x.version is not null).ToList();
        if (packages.Count == 0) { ctx.Skip("D12", "no NuGet package references"); ctx.Skip("D14", "no NuGet package references"); ctx.Skip("SC1", "no NuGet package references"); return; }
        var pre = 0;
        foreach (var (p, id, version) in packages.Where(x => x.p.IsProduction))
            if (NuGetVersion.TryParse(version, out var v) && v.IsPrerelease)
            { pre++; var line = LineOf(ctx.Repo.Text(p.Path), id) ?? LineOf(ctx.Repo.Text("Directory.Packages.props"), id); ctx.Add(new Finding("prerelease-dependency", "D12", $"{id} {version} is a pre-release build shipped in {p.Name}", ctx.Repo.Exists("Directory.Packages.props") && LineOf(ctx.Repo.Text(p.Path), id) is null ? "Directory.Packages.props" : p.Path, line, line, 1)); }
        var unlocked = prod.Where(p => p.Packages.Count > 0 && !p.LockFile).ToList();
        if (unlocked.Count > 0 && unlocked.Count == prod.Count(p => p.Packages.Count > 0)) ctx.Add(new Finding("dependencies-not-locked", "SC1", $"none of the {unlocked.Count} production projects restores with a lock file (packages.lock.json / RestorePackagesWithLockFile): the build is not reproducible", unlocked.OrderBy(p => p.Path, StringComparer.Ordinal).First().Path, null, null, Math.Min(3, unlocked.Count)));
        else foreach (var p in unlocked) ctx.Add(new Finding("dependencies-not-locked", "SC1", $"{p.Name} restores without a lock file while the other projects lock theirs: the build is not reproducible", p.Path, null, null, 1));
        var lockedShare = prod.Count(p => p.Packages.Count > 0) == 0 ? 1 : (double)prod.Count(p => p.Packages.Count > 0 && p.LockFile) / prod.Count(p => p.Packages.Count > 0);
        var lockedMode = ctx.Repo.Projects.Any(p => p.RestoreLockedMode) || ctx.Repo.Files.Where(f => f.StartsWith(".github/workflows/")).Any(f => ctx.Repo.Text(f).Contains("--locked-mode"));
        ctx.Measure("SC1", Shape.FromShare(lockedShare) * (lockedMode ? 1 : 0.8), advisory: true, note: $"{lockedShare:P0} of production projects lock their packages; locked-mode restore: {lockedMode}");
        var vulnerable = ctx.FindingsFor("D30").Count(); var malicious = ctx.FindingsFor("D43").Count();
        var hygiene = 10.0;
        hygiene *= Math.Exp(-0.35 * vulnerable - 2.0 * malicious);
        hygiene *= Math.Exp(-0.3 * pre);
        hygiene *= 0.7 + 0.3 * lockedShare;
        ctx.Measure("D12", hygiene, coverage: 0.6, note: $"{vulnerable} vulnerable, {malicious} malicious, {pre} pre-release packages; {lockedShare:P0} locked. Coverage 0.6: 'outdated' and 'deprecated' need registry data this offline engine does not have");
        // D14: licences from the local NuGet cache, when one was supplied
        if (ctx.NuGetPackagesDir is null) { ctx.Skip("D14", "needs package metadata: pass --nuget-packages <dir> (a restored NuGet cache) to read licence expressions offline"); return; }
        int seen = 0, violations = 0;
        foreach (var (p, id, version) in packages.Where(x => x.p.IsProduction).DistinctBy(x => (x.id.ToLowerInvariant(), x.version)))
        {
            var nuspec = Path.Combine(ctx.NuGetPackagesDir, id.ToLowerInvariant(), version!.ToLowerInvariant(), id.ToLowerInvariant() + ".nuspec");
            if (!File.Exists(nuspec)) continue;
            seen++;
            var text = File.ReadAllText(nuspec);
            var expr = Regex.Match(text, @"<license[^>]*type=""expression""[^>]*>([^<]+)</license>").Groups[1].Value.Trim();
            var url = Regex.Match(text, @"<licenseUrl>([^<]+)</licenseUrl>").Groups[1].Value.Trim();
            var ids = Regex.Split(expr, @"\s+(?:OR|AND|WITH)\s+|\(|\)").Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            var hit = ids.FirstOrDefault(i => ctx.Data.CopyleftLicences.Contains(i)) ?? (Regex.IsMatch(url, @"(?i)gpl-?[23]|agpl|/gpl") && !Regex.IsMatch(url, @"(?i)lgpl") ? url : null);
            if (hit is not null) { violations++; ctx.Add(new Finding("license-policy-violation", "D14", $"{id} {version} is licensed {expr}{(url.Length > 0 && expr.Length == 0 ? url : "")} ({hit}): copyleft, incompatible with the default policy", null, null, null, 2)); }
        }
        if (seen == 0) ctx.Skip("D14", "no package metadata found in the supplied NuGet cache (restore the solution first)");
        else ctx.Measure("D14", Shape.FromFindings(ctx.FindingsFor("D14"), 1.0, 1.0), coverage: Math.Round((double)seen / packages.Where(x => x.p.IsProduction).DistinctBy(x => (x.id.ToLowerInvariant(), x.version)).Count(), 2), note: $"{violations} copyleft of {seen} packages with metadata");
    }

    private static int? LineOf(string text, string needle) { var i = text.IndexOf(needle, StringComparison.OrdinalIgnoreCase); return i < 0 ? null : text.Take(i).Count(ch => ch == '\n') + 1; }

    // ---------------- P1 P3 P12 ----------------
    /// <summary>Randomness that only makes test data unique (a registration number inside a string, a payload field) does not change the outcome; randomness that reaches an assertion, a loop bound or a branch does.</summary>
    private static bool RandomOnlyShapesData(string body)
    {
        var lines = body.Split('\n').Where(l => Regex.IsMatch(l, @"new Random\(\s*\)|Random\.Shared")).ToList();
        if (lines.Any(l => Regex.IsMatch(l, @"Assert|Should|\bif\s*\(|\bfor\s*\(|\bwhile\s*\("))) return false;
        var vars = lines.SelectMany(l => Regex.Matches(l, @"\bvar\s+(\w+)\s*=").Select(m => m.Groups[1].Value)).ToList();
        return vars.All(v => !Regex.IsMatch(body, $@"(Assert|Should)[^\n]*\b{Regex.Escape(v)}\b"));
    }

    /// <summary>Where a repository documents itself: README files, docs/, doc/, runbooks/, ops/, wiki/ and decision folders. A Markdown file elsewhere (a fixture, a vendored package, a benchmark key) is not the project's documentation.</summary>
    public static IEnumerable<string> DocumentationFiles(Repository repo) => repo.Files.Where(f => Regex.IsMatch(f, @"(?i)\.(md|rst|adoc|txt)$") && (Regex.IsMatch(f, @"(?i)^(readme|runbook|operations|disaster[-_]?recovery|accessibility|a11y)[^/]*$") || Regex.IsMatch(f, @"(?i)^(docs?|runbooks?|ops|wiki|adrs?|decisions|architecture)/")));

    public static IEnumerable<string> WorkflowFiles(Repository repo) => repo.Files.Where(f => Regex.IsMatch(f, @"^\.github/workflows/[^/]+\.ya?ml$|^\.gitlab-ci\.ya?ml$|^azure-pipelines[^/]*\.ya?ml$|^\.circleci/config\.ya?ml$|^Jenkinsfile$|^bitbucket-pipelines\.ya?ml$|^\.drone\.ya?ml$|^\.tekton/|^\.buildkite/"));

    private static void Pipeline(ScanContext ctx)
    {
        var repo = ctx.Repo;
        var workflows = WorkflowFiles(repo).ToList();
        if (workflows.Count == 0)
        {
            ctx.Add(new Finding("ci-build-and-test-pipeline", "P1", "no CI pipeline definition in the repository", null, null, null, 2)); ctx.Measure("P1", 0, note: "no pipeline");
            ctx.Skip("P12", "no CI pipeline to judge"); ctx.Measure("P3", 0, note: "no pipeline, so no security tooling gate"); return;
        }
        var all = string.Join("\n", workflows.Select(repo.Text));
        var builds = Regex.IsMatch(all, @"dotnet\s+(build|publish|pack|test)|msbuild|\bnuke\b|cake"); var tests = Regex.IsMatch(all, @"dotnet\s+test|vstest|dotnet\s+run\s+--project\s+\S*[Tt]est");
        var onPr = Regex.IsMatch(all, @"pull_request|merge_request|pullrequest|pr:"); var onPush = Regex.IsMatch(all, @"(?m)^\s*push\s*:|on:\s*\[?\s*push|trigger:|branches:");
        var p1 = new (string, bool)[] { ("a pipeline that builds", builds), ("a pipeline that runs the tests", tests), ("runs on pull requests", onPr), ("runs on push to the main branch", onPush) };
        foreach (var (name, ok) in p1) if (!ok) ctx.Add(new Finding("ci-build-and-test-pipeline", "P1", $"CI: missing {name}", workflows[0], null, null, 1));
        ctx.Measure("P1", Shape.FromChecklist(p1.Count(c => c.Item2), p1.Length), note: string.Join(", ", p1.Where(c => c.Item2).Select(c => c.Item1)));
        // P3: security and performance tooling wired in
        var codeql = Regex.IsMatch(all, @"github/codeql-action|codeql|semgrep|sonar|security-code-scan|snyk|checkmarx|veracode|trivy|grype");
        var depbot = repo.Exists(".github/dependabot.yml") || repo.Exists(".github/dependabot.yaml") || repo.FilesNamed("renovate.json", "renovate.json5", ".renovaterc", ".renovaterc.json").Any();
        var vulnGate = Regex.IsMatch(all, @"list\s+package\s+--vulnerable|NuGetAudit|osv-scanner|dependency-review-action|dotnet-outdated|audit") || repo.Projects.Any(p => p.Packages.Any(x => x.id.Contains("SecurityCodeScan")));
        var secretScan = Regex.IsMatch(all, @"gitleaks|trufflehog|detect-secrets|secretlint|git-secrets") || repo.Exists(".pre-commit-config.yaml") && repo.Text(".pre-commit-config.yaml").Contains("secret");
        var analyzers = repo.Files.Where(f => f.EndsWith(".props") || f.EndsWith(".csproj")).Any(f => Regex.IsMatch(repo.Text(f), @"AnalysisMode|EnableNETAnalyzers|TreatWarningsAsErrors>true|SecurityCodeScan|Microsoft\.CodeAnalysis\.NetAnalyzers|SonarAnalyzer|Roslynator|StyleCop"));
        var benchmarks = repo.Projects.Any(p => p.HasPackage("BenchmarkDotNet"));
        var p3 = new (string, bool)[] { ("SAST scanning (CodeQL/Semgrep/Sonar/…)", codeql), ("dependency update automation (Dependabot/Renovate)", depbot), ("vulnerable-package gate or audit", vulnGate), ("secret scanning (a SAST suite counts)", secretScan || codeql) };
        foreach (var (name, ok) in p3) if (!ok) ctx.Add(new Finding("security-tooling-in-ci", "P3", $"tooling: missing {name}", null, null, null, 0.5));
        ctx.Facts["tooling"] = $"analyzers={analyzers} benchmarks={benchmarks}";
        ctx.Measure("P3", Shape.FromChecklist(p3.Count(c => c.Item2), p3.Length), note: string.Join(", ", p3.Where(c => c.Item2).Select(c => c.Item1)));
        // P12: does the gate run what it appears to run
        var p12 = new List<Finding>();
        foreach (var wf in workflows)
        {
            var text = repo.Text(wf); var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var l = lines[i];
                if (!Regex.IsMatch(l, @"dotnet\s+test")) continue;
                if (Regex.IsMatch(l, @"--filter\s+\S*(!=|!~|Category|TestCategory|FullyQualifiedName!~)")) p12.Add(new Finding("ci-test-gate-integrity", "P12", $"the test step filters tests out: {l.Trim()}", wf, i + 1, i + 1, 1));
                if (Regex.IsMatch(l, @"\|\|\s*true|;\s*exit 0")) p12.Add(new Finding("ci-test-gate-integrity", "P12", $"the test step cannot fail: {l.Trim()}", wf, i + 1, i + 1, 2));
                var window = string.Join("\n", lines.Skip(Math.Max(0, i - 6)).Take(12));
                if (Regex.IsMatch(window, @"continue-on-error:\s*true|allow_failure:\s*true|continueOnError:\s*true")) p12.Add(new Finding("ci-test-gate-integrity", "P12", "the test step is allowed to fail (continue-on-error)", wf, i + 1, i + 1, 2));
                if (Regex.IsMatch(l, @"--collect|coverlet|/p:CollectCoverage=true") && !Regex.IsMatch(text, @"Threshold|threshold|minimum|coverage-gate|ReportGenerator.*minimum|codecov.*(target|fail_under)|patch:|project:")) p12.Add(new Finding("ci-test-gate-integrity", "P12", $"coverage is collected ({Path.GetFileName(wf)}:{i + 1}) and uploaded, but no threshold gates it: the number is reported, never enforced", null, null, null, 2));
            }
        }
        var skippedTests = ctx.FindingsFor("D10").Count(f => f.RuleId == "skipped-test-without-reason");
        if (skippedTests > 0) p12.Add(new Finding("ci-test-gate-integrity", "P12", $"{skippedTests} test(s) are skipped without a structured reason, so the gate runs less than its inventory says", null, null, null, 0.5 * skippedTests));
        foreach (var f in p12) ctx.Add(f);
        ctx.Measure("P12", Shape.FromFindings(p12, 1.0, 0.7), note: $"{p12.Count} honesty finding(s) across {workflows.Count} pipeline file(s)");
    }

    // ---------------- P2 P4 P5 P6 P7 P8 P10 P11 ----------------
    private static void Operations(ScanContext ctx)
    {
        var repo = ctx.Repo; var prodText = string.Join("\n", ctx.Workspace.ProductionTrees.Select(t => t.tree.GetRoot().ToString()));
        var hasApp = repo.Projects.Any(p => p.Role is ProjectRole.Web or ProjectRole.Worker);
        // P2 observability
        if (!hasApp) ctx.Skip("P2", "no web or worker application: observability is a property of a running process");
        else
        {
            var p2 = new (string, bool)[]
            {
                ("structured logging (ILogger / Serilog / NLog message templates)", Regex.IsMatch(prodText, @"ILogger<|LoggerMessage|Serilog|NLog|LogInformation\(|Log\.Information")),
                ("tracing or metrics (OpenTelemetry / ActivitySource / Meter)", Regex.IsMatch(prodText, @"OpenTelemetry|ActivitySource|System\.Diagnostics\.Metrics|new Meter\(|AddOpenTelemetry|ApplicationInsights|Datadog|Prometheus")),
                ("health checks", Regex.IsMatch(prodText, @"AddHealthChecks|MapHealthChecks|IHealthCheck|/health|healthz")),
                ("correlation / request logging", Regex.IsMatch(prodText, @"TraceIdentifier|CorrelationId|UseHttpLogging|UseSerilogRequestLogging|Activity\.Current|W3C|traceparent")),
            };
            foreach (var (name, ok) in p2) if (!ok) ctx.Add(new Finding("observability", "P2", $"observability: missing {name}", null, null, null, 1));
            // Console.Write in a service whose project already logs through ILogger: diagnostics that never reach the structured log
            var consoleSites = new List<Finding>();
            foreach (var pc in ctx.Workspace.Production.Where(pc => pc.Project.Role is ProjectRole.Web or ProjectRole.Worker or ProjectRole.Infrastructure or ProjectRole.Application))
            {
                if (!pc.Trees.Any(t => t.GetRoot().ToString().Contains("ILogger"))) continue;
                foreach (var tree in pc.Trees.Where(t => !pc.IsGenerated(t)))
                    foreach (var cls in tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>().Where(c => c.Identifier.Text is not ("Program" or "Startup") && !Regex.IsMatch(c.Identifier.Text, @"Command$|Cli$|Console$|Printer$|Reporter$|Formatter$")))
                        foreach (var inv in cls.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(i => Regex.IsMatch(i.Expression.ToString(), @"^Console\.(Error\.|Out\.)?Write(Line)?$")))
                            consoleSites.Add(new Finding("observability", "P2", $"{cls.Identifier.Text} writes diagnostics with {inv.Expression} while the project logs through ILogger elsewhere: the line never reaches the structured log", ctx.Workspace.RelPath(tree.FilePath), Cs.Line(inv), null, 1));
            }
            foreach (var f in consoleSites) ctx.Add(f);
            var weighted = (p2[0].Item2 ? 0.4 : 0) + (p2[2].Item2 ? 0.3 : 0) + (p2[1].Item2 ? 0.15 : 0) + (p2[3].Item2 ? 0.15 : 0);
            ctx.Measure("P2", 10 * weighted * (Shape.FromFindings(consoleSites, ctx.ProductionKloc, 0.5) / 10), note: string.Join(", ", p2.Where(c => c.Item2).Select(c => c.Item1)) + (consoleSites.Count > 0 ? $"; {consoleSites.Count} Console.Write site(s) beside a logger" : ""));
        }
        // P4 deployment & rollback, P5 DR — from manifests and pipeline files
        var k8s = repo.FilesWithExtension(".yml", ".yaml").Where(f => !f.StartsWith(".github/") && Regex.IsMatch(repo.Text(f), @"(?m)^kind:\s*(Deployment|StatefulSet|DaemonSet|Rollout|CronJob|Job|Service|Ingress)")).ToList();
        var helm = repo.FilesNamed("Chart.yaml").Any(); var compose = repo.Files.Any(f => Regex.IsMatch(Path.GetFileName(f), @"^(docker-)?compose[^/]*\.ya?ml$")); var dockerfile = repo.Files.Any(f => Path.GetFileName(f).StartsWith("Dockerfile", StringComparison.OrdinalIgnoreCase));
        var deployWf = WorkflowFiles(repo).Where(f => Regex.IsMatch(repo.Text(f), @"(?i)deploy|kubectl|helm\s|argocd|az\s+webapp|aws\s+ecs|gcloud\s+run|environment:")).ToList();
        var terraform = repo.FilesWithExtension(".tf").Any(); var bicep = repo.FilesWithExtension(".bicep").Any();
        if (k8s.Count == 0 && !helm && deployWf.Count == 0 && !terraform && !bicep && !compose) ctx.Skip("P4", "nothing is deployed from this repository (no manifests, deploy pipeline or infrastructure code)");
        else
        {
            var k8sText = string.Join("\n", k8s.Select(repo.Text));
            var p4 = new (string, bool)[]
            {
                ("readiness and liveness probes", Regex.IsMatch(k8sText, @"readinessProbe") && Regex.IsMatch(k8sText, @"livenessProbe") || k8s.Count == 0 && compose && Regex.IsMatch(string.Join("\n", repo.Files.Where(f => Regex.IsMatch(Path.GetFileName(f), @"compose")).Select(repo.Text)), @"healthcheck:")),
                ("a rolling or progressive update strategy", Regex.IsMatch(k8sText, @"RollingUpdate|maxUnavailable|maxSurge|canary|blueGreen") || Regex.IsMatch(string.Join("\n", deployWf.Select(repo.Text)), @"(?i)canary|slot|blue|green|rollout")),
                ("automated deployment pipeline", deployWf.Count > 0 || helm && WorkflowFiles(repo).Any()),
                ("an approval gate or protected environment", Regex.IsMatch(string.Join("\n", deployWf.Select(repo.Text)), @"(?im)^\s*environment:\s*\S+|approval|when:\s*manual|reviewers")),
                ("pinned image references", k8s.Count == 0 || !Regex.IsMatch(k8sText, @"image:\s*\S+:latest|image:\s*[^:\s@""']+\s*$")),
            };
            foreach (var (name, ok) in p4) if (!ok) ctx.Add(new Finding("deployment-rollback-safety", "P4", $"deployment: missing {name}", k8s.FirstOrDefault() ?? deployWf.FirstOrDefault(), null, null, 1));
            ctx.Measure("P4", Shape.FromChecklist(p4.Count(c => c.Item2), p4.Length), note: $"{k8s.Count} manifest(s), {deployWf.Count} deploy pipeline(s); " + string.Join(", ", p4.Where(c => c.Item2).Select(c => c.Item1)));
        }
        var hasState = Regex.IsMatch(prodText, @"DbContext|IDocumentStore|NpgsqlConnection|SqlConnection|MongoClient|IConnectionMultiplexer|BlobServiceClient|AmazonS3") || repo.Projects.Any(p => p.HasPackage("Microsoft.EntityFrameworkCore") || p.HasPackage("Marten") || p.HasPackage("Dapper") || p.HasPackage("Npgsql") || p.HasPackage("MongoDB.Driver")) || Regex.IsMatch(string.Join("\n", k8s.Select(repo.Text)), @"PersistentVolumeClaim|StatefulSet|volumeClaimTemplates");
        if (!hasState) ctx.Skip("P5", "no persistent state (no database, document store or volume): nothing to back up or recover");
        else
        {
            var docs = string.Join("\n", DocumentationFiles(repo).Select(repo.Text));
            var infra = string.Join("\n", repo.Files.Where(f => Regex.IsMatch(f, @"\.(ya?ml|tf|bicep|sh|ps1)$")).Select(repo.Text));
            var p5 = new (string, bool)[]
            {
                ("a backup job or snapshot policy (in infrastructure code, or a managed service's schedule documented)", Regex.IsMatch(infra, @"(?i)pg_dump|mysqldump|backup|snapshot|velero|point.in.time|pitr|BackupPolicy|backup_retention") || Regex.IsMatch(docs, @"(?i)(daily|nightly|hourly|automated|scheduled|continuous)\s+(backup|snapshot)s?|backups?\s+(run|are taken|are kept)|snapshot (policy|schedule)|point.in.time recovery")),
                ("a documented restore procedure or RTO/RPO", Regex.IsMatch(docs, @"(?i)\bRTO\b|\bRPO\b|disaster recovery|restor(e|ing|ation) (the |a |from |procedure|process)|backup(s)? (are|is|run|schedule)|point.in.time")),
                ("persistence declared in infrastructure (volume claims, named volumes, managed database)", Regex.IsMatch(infra, @"(?i)PersistentVolumeClaim|persistentVolumeClaim:|StatefulSet|volumeClaimTemplates|aws_db_instance|aws_rds_cluster|azurerm_(postgresql|mssql|cosmosdb)|google_sql|storageClassName|(?m)^volumes:\s*$") || Regex.IsMatch(docs, @"(?i)managed (postgres|postgresql|sql|database|mysql)|RDS|Cloud SQL|Azure (Database|SQL)|Aurora")),
                ("retention or deletion protection", Regex.IsMatch(infra, @"(?i)deletion_protection|retention|reclaimPolicy:\s*Retain|prevent_destroy")),
            };
            foreach (var (name, ok) in p5) if (!ok) ctx.Add(new Finding("disaster-recovery-evidence", "P5", $"disaster recovery: missing {name}", null, null, null, 1));
            var p5Weighted = (p5[0].Item2 ? 0.4 : 0) + (p5[1].Item2 ? 0.3 : 0) + (p5[2].Item2 ? 0.15 : 0) + (p5[3].Item2 ? 0.15 : 0);
            ctx.Measure("P5", 10 * p5Weighted, note: string.Join(", ", p5.Where(c => c.Item2).Select(c => c.Item1)));
        }
        // P6 release hygiene
        var changelog = repo.Files.FirstOrDefault(f => Regex.IsMatch(f, @"^(CHANGELOG|CHANGES|HISTORY|RELEASES)(\.md|\.txt|\.rst)?$", RegexOptions.IgnoreCase));
        var versionStamp = repo.Projects.Any(p => p.Version is not null) || repo.Files.Any(f => f.EndsWith(".props") && Regex.IsMatch(repo.Text(f), @"<(Version|VersionPrefix)>")) || repo.Projects.Any(p => p.HasPackage("MinVer") || p.HasPackage("Nerdbank.GitVersioning") || p.HasPackage("GitVersion")) || repo.Exists("version.json") || repo.Exists("GitVersion.yml");
        var tags = ctx.History.Tags.Where(t => Regex.IsMatch(t, @"^v?\d+\.\d+")).ToList();
        var p6 = new List<(string, bool, string?)> { ("a changelog", changelog is not null, null), ("explicit version stamping (<Version>, MinVer, GitVersion, …)", versionStamp, null), ("release tags", tags.Count > 0 || !ctx.History.Available, null) };
        if (changelog is not null)
        {
            var text = repo.Text(changelog);
            var keepAChangelog = Regex.IsMatch(text, @"(?m)^##\s*\[?(Unreleased|\d+\.\d+)") ;
            p6.Add(("Keep-a-Changelog shape (## [version] sections)", keepAChangelog, changelog));
            var versionedEntries = Regex.Matches(text, @"(?m)^##\s*\[?v?\d+\.\d+").Count;
            p6.Add(("a changelog that covers the release history (not a thin stub)", versionedEntries >= 2 && !Regex.IsMatch(text, @"(?i)(earlier|older|previous|prior)\s+(history|releases?|versions?|changes|entries)[^.\n]*\b(git|log|history)\b|see (the )?git (log|history)"), changelog));
            var latestTag = tags.Select(t => t.TrimStart('v', 'V')).Where(t => NuGetVersion.TryParse(t, out _)).OrderByDescending(t => { NuGetVersion.TryParse(t, out var v); return v; }).FirstOrDefault();
            var tagsMissing = tags.Select(t => t.TrimStart('v', 'V')).Where(t => NuGetVersion.TryParse(t, out _) && !Regex.IsMatch(text, $@"(?m)^##\s*\[?{Regex.Escape(t)}\b")).ToList();
            if (tags.Count > 1) p6.Add(("the changelog covers the released tags", tagsMissing.Count < 2, changelog));
            if (tagsMissing.Count >= 2) ctx.Add(new Finding("release-hygiene", "P6", $"the changelog has no entry for {tagsMissing.Count} released tags: {string.Join(", ", tagsMissing.Take(4))}", changelog, 1, 1, Math.Min(2, tagsMissing.Count)));
            var projectVersion = repo.Projects.Select(p => p.Version).FirstOrDefault(v => v is not null) ?? Regex.Match(string.Join("\n", repo.Files.Where(f => f.EndsWith(".props")).Select(repo.Text)), @"<(?:Version|VersionPrefix)>([^<$]+)<").Groups[1].Value;
            if (!string.IsNullOrEmpty(projectVersion) && latestTag is not null && NuGetVersion.TryParse(projectVersion, out var pv) && NuGetVersion.TryParse(latestTag, out var tv) && pv.CompareTo(tv) < 0)
                ctx.Add(new Finding("release-hygiene", "P6", $"the project version {projectVersion} is behind the latest release tag {latestTag}", null, null, null, 1));
        }
        foreach (var (name, ok, where) in p6) if (!ok && !name.StartsWith("the changelog covers")) ctx.Add(new Finding("release-hygiene", "P6", $"release hygiene: missing {name}", null, null, null, 1));
        double P6Weight((string, bool, string?) c) => c.Item1.StartsWith("the changelog covers") ? 2 : 1;
        ctx.Measure("P6", 10 * p6.Where(c => c.Item2).Sum(P6Weight) / p6.Sum(P6Weight), note: string.Join(", ", p6.Where(c => c.Item2).Select(c => c.Item1)));
        // P7 outbound HTTP resilience
        var httpSites = new List<(string file, int line, string what, bool resilient)>();
        foreach (var (p, tree) in ctx.Workspace.ProductionTrees)
        {
            var rel = ctx.Workspace.RelPath(tree.FilePath); var root = tree.GetRoot();
            foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(i => Cs.MemberName(i.Expression) is "AddHttpClient" or "AddRefitClient" or "AddGrpcClient"))
            {
                var chain = inv.Ancestors().TakeWhile(a => a is ExpressionSyntax or ExpressionStatementSyntax or EqualsValueClauseSyntax or VariableDeclaratorSyntax).Select(a => a.ToString()).LastOrDefault() ?? inv.ToString();
                var stmt = inv.Ancestors().OfType<StatementSyntax>().FirstOrDefault()?.ToString() ?? chain;
                var resilient = Regex.IsMatch(stmt, @"AddStandardResilienceHandler|AddResilienceHandler|AddPolicyHandler|AddTransientHttpErrorPolicy|AddStandardHedgingHandler|UseSocketsHttpHandler|Timeout\s*=|ConfigureHttpClient\([^)]*Timeout|AddRetryPolicy|Polly");
                var handedToCaller = inv.Ancestors().OfType<ReturnStatementSyntax>().Any() || (Cs.EnclosingMethod(inv) as MethodDeclarationSyntax)?.ReturnType.ToString().Contains("IHttpClientBuilder") == true || p.Project.IsPackable;
                if (handedToCaller) continue;   // a library returning the builder leaves the policy to the host that owns the process
                httpSites.Add((rel, Cs.Line(inv), $"AddHttpClient {Trunc(inv.ArgumentList.ToString(), 40)}", resilient));
            }
            foreach (var oc in root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>().Where(o => Cs.Simple(o.Type) == "HttpClient"))
            {
                var method = Cs.EnclosingMethod(oc); var body = method is null ? "" : Cs.BodyOf(method)?.ToString() ?? "";
                var resilient = Regex.IsMatch(body + (oc.Initializer?.ToString() ?? ""), @"Timeout\s*=|Polly|ResiliencePipeline|RetryPolicy|CircuitBreaker") || Cs.EnclosingType(oc)?.ToString().Contains("Timeout") == true;
                httpSites.Add((rel, Cs.Line(oc), "new HttpClient()", resilient));
            }
        }
        if (httpSites.Count == 0) ctx.Skip("P7", "no outbound HTTP clients");
        else
        {
            var globalResilience = Regex.IsMatch(prodText, @"ConfigureHttpClientDefaults\([^;]*Resilience|AddServiceDefaults\(\)");
            foreach (var s in httpSites.Where(s => !s.resilient && !globalResilience)) ctx.Add(new Finding("outbound-http-resilience", "P7", $"{s.what} has no timeout, retry or circuit breaker: a slow dependency cascades into this service", s.file, s.line, s.line, 1));
            var share = globalResilience ? 1 : (double)httpSites.Count(s => s.resilient) / httpSites.Count;
            ctx.Measure("P7", Shape.FromShare(share), note: $"{httpSites.Count(s => s.resilient)} of {httpSites.Count} HTTP clients wrapped in resilience{(globalResilience ? " (defaults configured globally)" : "")}");
        }
        // P8 schema migrations
        var efContexts = ctx.Workspace.ProductionTrees.SelectMany(t => t.tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>().Where(c => c.BaseList?.Types.Any(b => Cs.Simple(b.Type) is "DbContext" or "IdentityDbContext") == true)).ToList();
        var hasDb = efContexts.Count > 0 || repo.Projects.Any(p => p.HasPackage("Microsoft.EntityFrameworkCore") || p.HasPackage("Dapper") || p.HasPackage("Npgsql") || p.HasPackage("DbUp") || p.HasPackage("FluentMigrator") || p.HasPackage("Marten"));
        if (!hasDb) ctx.Skip("P8", "no relational database: no schema to migrate");
        else
        {
            var migrations = repo.Files.Any(f => Regex.IsMatch(f, @"(?i)/Migrations/.*\.cs$") && Regex.IsMatch(repo.Text(f), @"\[Migration\(|: Migration\b|MigrationBuilder")) || repo.Files.Any(f => Regex.IsMatch(f, @"(?i)/(migrations|db/migrate|sql/migrations|scripts/migrations)/.*\.sql$")) || repo.Projects.Any(p => p.HasPackage("DbUp") || p.HasPackage("FluentMigrator") || p.HasPackage("Evolve") || p.HasPackage("grate")) || Regex.IsMatch(prodText, @"\.Migrate(Async)?\(\)|MigrationsAssembly|AutoCreateSchemaObjects\s*=\s*AutoCreate\.None");
            var ensureCreated = new List<(string, int)>();
            foreach (var (p, tree) in ctx.Workspace.ProductionTrees)
                foreach (var inv in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().Where(i => Cs.MemberName(i.Expression) is "EnsureCreated" or "EnsureCreatedAsync" || Cs.MemberName(i.Expression) is "EnsureDeleted" or "EnsureDeletedAsync"))
                    ensureCreated.Add((ctx.Workspace.RelPath(tree.FilePath), Cs.Line(inv)));
            var marten = Regex.IsMatch(prodText, @"AutoCreateSchemaObjects\s*=\s*AutoCreate\.(All|CreateOrUpdate)");
            foreach (var (f, l) in ensureCreated) ctx.Add(new Finding("versioned-schema-migrations", "P8", "the schema is created from the model at runtime (EnsureCreated): no versioned migration path exists for a database that already holds data", f, l, l, 2));
            if (!migrations && ensureCreated.Count == 0) ctx.Add(new Finding("versioned-schema-migrations", "P8", "a database is used but no versioned migrations (EF migrations, DbUp, FluentMigrator, SQL scripts) are committed", null, null, null, 1));
            if (marten) ctx.Add(new Finding("versioned-schema-migrations", "P8", "Marten auto-creates schema objects at runtime", null, null, null, 1));
            ctx.Measure("P8", migrations && ensureCreated.Count == 0 && !marten ? 10 : migrations ? 5 : ensureCreated.Count > 0 ? 2 : 4, note: $"migrations: {migrations}; EnsureCreated sites: {ensureCreated.Count}");
        }
        // P10 library API & versioning
        var libraries = repo.Projects.Where(p => p.IsPackable && p.IsProduction).ToList();
        if (libraries.Count == 0) ctx.Skip("P10", "no packable library: this repository produces a service, not a package");
        else
        {
            var publicTypes = ctx.Workspace.Production.Where(pc => pc.Project.IsPackable).SelectMany(pc => pc.Trees.SelectMany(t => t.GetRoot().DescendantNodes().OfType<BaseTypeDeclarationSyntax>().Where(td => td.Modifiers.Any(SyntaxKind.PublicKeyword)))).Count();
            var p10 = new (string, bool)[]
            {
                ("semantic version stamping", versionStamp), ("a public API baseline (PublicAPI.Shipped.txt / PublicApiAnalyzers)", repo.Files.Any(f => f.EndsWith("PublicAPI.Shipped.txt")) || repo.Projects.Any(p => p.HasPackage("Microsoft.CodeAnalysis.PublicApiAnalyzers"))),
                ("XML documentation generated", repo.Files.Where(f => f.EndsWith(".props") || f.EndsWith(".csproj")).Any(f => repo.Text(f).Contains("<GenerateDocumentationFile>true"))),
                ("a deliberate (small) public surface", publicTypes <= 60 || repo.Files.Any(f => f.EndsWith("PublicAPI.Shipped.txt"))), ("a changelog for consumers", changelog is not null),
            };
            foreach (var (name, ok) in p10) if (!ok) ctx.Add(new Finding("library-api-versioning", "P10", $"library ({libraries[0].Name}): missing {name}", null, null, null, 1));
            ctx.Measure("P10", Shape.FromChecklist(p10.Count(c => c.Item2), p10.Length), note: $"{libraries.Count} packable project(s), {publicTypes} public types");
        }
        // P11 BDD
        var bddProjects = repo.Projects.Where(p => p.HasPackage("SpecFlow") || p.HasPackage("Reqnroll") || p.HasPackage("Xunit.Gherkin") || p.HasPackage("LightBDD") || p.HasPackage("TickSpec")).ToList();
        if (bddProjects.Count == 0) ctx.Skip("P11", "no BDD framework present: only assessed when behaviour is specified in Gherkin");
        else
        {
            var features = repo.FilesWithExtension(".feature").ToList();
            var featureText = string.Join("\n", features.Select(repo.Text));
            var steps = ctx.Workspace.Tests.SelectMany(t => t.Trees).Sum(t => Regex.Matches(t.GetRoot().ToString(), @"\[(Given|When|Then|And|But|StepDefinition)\(").Count);
            var scenarios = Regex.Matches(featureText, @"(?m)^\s*(Scenario|Scenario Outline|Example):").Count;
            var p11 = new (string, bool)[] { ("feature files", features.Count > 0), ("step definitions bound to them", steps > 0), ("scenarios that read as business behaviour (Given/When/Then)", scenarios > 0 && Regex.IsMatch(featureText, @"(?m)^\s*Given") && Regex.IsMatch(featureText, @"(?m)^\s*Then")), ("the specifications run in the pipeline", WorkflowFiles(repo).Any(f => Regex.IsMatch(repo.Text(f), @"dotnet\s+test"))) };
            foreach (var (name, ok) in p11) if (!ok) ctx.Add(new Finding("executable-specifications", "P11", $"BDD: missing {name}", features.FirstOrDefault(), null, null, 1));
            ctx.Measure("P11", Shape.FromChecklist(p11.Count(c => c.Item2), p11.Length), note: $"{features.Count} feature file(s), {scenarios} scenarios, {steps} step bindings");
        }
    }

    private static string Trunc(string s, int n) { s = Regex.Replace(s, @"\s+", " "); return s.Length <= n ? s : s[..n] + "…"; }

    // ---------------- PF1 PF2 (reward-only) ----------------
    private static void Performance(ScanContext ctx)
    {
        var repo = ctx.Repo;
        var benchProjects = repo.Projects.Where(p => p.HasPackage("BenchmarkDotNet")).ToList();
        if (benchProjects.Count == 0) ctx.Skip("PF1", "reward-only: no benchmark suite to credit (its absence is never a deduction)");
        else
        {
            var text = string.Join("\n", ctx.Workspace.Projects.Where(pc => benchProjects.Any(b => b.Path == pc.Project.Path)).SelectMany(pc => pc.Trees).Select(t => t.GetRoot().ToString()));
            var ci = WorkflowFiles(repo).Any(f => Regex.IsMatch(repo.Text(f), @"(?i)benchmark"));
            var checks = new (string, bool)[] { ("a benchmark suite", true), ("memory / allocation measurement ([MemoryDiagnoser])", text.Contains("MemoryDiagnoser")), ("benchmarks run in the pipeline", ci), ("baselines or thresholds ([Benchmark(Baseline = true)] / regression gate)", Regex.IsMatch(text, @"Baseline\s*=\s*true") || Regex.IsMatch(string.Join("\n", WorkflowFiles(repo).Select(repo.Text)), @"(?i)benchmark.*(threshold|alert|compare)")) };
            ctx.Measure("PF1", Shape.FromChecklist(checks.Count(c => c.Item2), checks.Length), note: string.Join(", ", checks.Where(c => c.Item2).Select(c => c.Item1)));
        }
        var allocAware = 0;
        foreach (var (p, tree) in ctx.Workspace.ProductionTrees)
            allocAware += Regex.Matches(tree.GetRoot().ToString(), @"\b(Span<|ReadOnlySpan<|Memory<|ReadOnlyMemory<|ArrayPool<|MemoryPool<|stackalloc\b|ValueTask<?|IBufferWriter<|ObjectPool<|ArraySegment<|SequenceReader<|ref struct|\bin\s+[A-Z]\w*\s+\w+\s*[,)]|string\.Create\(|Utf8Parser|Utf8Formatter|CollectionsMarshal|MemoryMarshal|PooledArray|RecyclableMemoryStream)").Count;
        if (allocAware == 0) ctx.Skip("PF2", "reward-only: no allocation-aware API usage to credit (a simpler style is never penalised)");
        else { var density = allocAware / ctx.ProductionKloc; ctx.Measure("PF2", Math.Min(10, 6 + 4 * Math.Min(1, density / 6)), note: $"{allocAware} allocation-aware constructs ({density:F1} per kLOC)"); }
    }

    // ---------------- X8 JS interop ----------------
    private static readonly HashSet<string> BrowserBuiltins = new(StringComparer.Ordinal) { "console", "localStorage", "sessionStorage", "navigator", "document", "window", "alert", "confirm", "prompt", "eval", "import", "Blazor", "history", "location", "scrollTo", "scroll", "open", "print", "fetch", "setTimeout", "clearTimeout", "setInterval", "clearInterval", "requestAnimationFrame", "getComputedStyle", "matchMedia", "crypto", "performance", "URL", "JSON", "Intl", "screen", "focus", "blur", "addEventListener", "removeEventListener", "dispatchEvent", "indexedDB", "caches", "speechSynthesis", "Notification", "structuredClone", "atob", "btoa", "devicePixelRatio", "innerWidth", "innerHeight" };

    private static void JsInterop(ScanContext ctx)
    {
        var calls = new List<(string name, string file, int line)>();
        foreach (var (p, tree) in ctx.Workspace.ProductionTrees)
        {
            var rel = ctx.Workspace.RelPath(tree.FilePath);
            foreach (var inv in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>().Where(i => Cs.MemberName(i.Expression) is "InvokeAsync" or "InvokeVoidAsync" or "Invoke" or "InvokeUnmarshalled" && i.ArgumentList.Arguments.Count >= 1 && i.ArgumentList.Arguments[0].Expression is LiteralExpressionSyntax { Token.Value: string }))
            {
                var receiver = Cs.ReceiverText(inv.Expression);
                if (!Regex.IsMatch(receiver, @"(?i)js|jsruntime|runtime|module|interop|objectreference")) continue;
                calls.Add((((LiteralExpressionSyntax)inv.ArgumentList.Arguments[0].Expression).Token.ValueText, rel, Cs.Line(inv)));
            }
        }
        // Razor files call interop too
        foreach (var f in ctx.Repo.FilesWithExtension(".razor", ".cshtml"))
        {
            var text = ctx.Repo.Text(f); var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++) foreach (Match m in Regex.Matches(lines[i], @"\.Invoke(?:Void)?Async(?:<[^>]+>)?\(\s*""([^""]+)"""))
                calls.Add((m.Groups[1].Value, f, i + 1));
        }
        if (calls.Count == 0) { ctx.Skip("X8", "no string-named JavaScript interop calls"); return; }
        var js = ctx.Repo.Files.Where(f => Regex.IsMatch(f, @"\.(js|mjs|ts)$") && !Regex.IsMatch(f, @"(?i)/(lib|libs|vendor|node_modules|dist|_framework|bin|obj)/|\.min\.js$|/wwwroot/lib/")).ToList();
        var jsText = string.Join("\n", js.Select(ctx.Repo.Text).Concat(ctx.Repo.FilesWithExtension(".razor", ".cshtml", ".html").Select(ctx.Repo.Text)));
        var defined = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(jsText, @"(?m)(?:^|[\s;{(])(?:window|globalThis|self)\.([A-Za-z_$][\w$]*(?:\.[A-Za-z_$][\w$]*)*)\s*=")) { defined.Add(m.Groups[1].Value); defined.Add(m.Groups[1].Value.Split('.').Last()); }
        foreach (Match m in Regex.Matches(jsText, @"(?m)^\s*(?:export\s+)?(?:async\s+)?function\s+([A-Za-z_$][\w$]*)")) defined.Add(m.Groups[1].Value);
        foreach (Match m in Regex.Matches(jsText, @"(?m)^\s*export\s+(?:const|let|var)\s+([A-Za-z_$][\w$]*)")) defined.Add(m.Groups[1].Value);
        foreach (Match m in Regex.Matches(jsText, @"(?m)^\s*(?:const|let|var)\s+([A-Za-z_$][\w$]*)\s*=\s*(?:async\s*)?(?:\([^)]*\)|[A-Za-z_$][\w$]*)\s*=>")) defined.Add(m.Groups[1].Value);
        foreach (Match m in Regex.Matches(jsText, @"(?m)^\s*([A-Za-z_$][\w$]*)\s*(?::\s*(?:async\s+)?function\s*\(|\s*\([^)]*\)\s*\{)")) defined.Add(m.Groups[1].Value);
        foreach (Match m in Regex.Matches(jsText, @"(?m)^\s*(?:export\s+)?(?:async\s+)?([A-Za-z_$][\w$]*)\s*\([^)]*\)\s*\{")) defined.Add(m.Groups[1].Value);
        foreach (Match m in Regex.Matches(jsText, @"([A-Za-z_$][\w$]*)\s*:\s*(?:async\s*)?(?:function\s*\(|\([^)]*\)\s*=>)")) defined.Add(m.Groups[1].Value);
        var missing = new List<Finding>();
        foreach (var (name, file, line) in calls.OrderBy(c => c.file, StringComparer.Ordinal).ThenBy(c => c.line))
        {
            var root = name.Split('.')[0]; var leaf = name.Split('.').Last();
            if (BrowserBuiltins.Contains(root) || name.StartsWith("import") || name.Contains("DotNet")) continue;
            if (defined.Contains(name) || defined.Contains(leaf) || js.Count == 0 && ctx.Repo.FilesWithExtension(".razor").Any() == false) continue;
            missing.Add(new Finding("js-interop-contract-mismatch", "X8", $"JS interop calls \"{name}\" but no first-party JavaScript defines it ({js.Count} script file(s) searched): the call fails at runtime", file, line, line, 1));
        }
        foreach (var f in missing) ctx.Add(f);
        var guarded = ctx.Workspace.Tests.Any(t => t.Trees.Any(tr => tr.GetRoot().ToString().Contains("IJSRuntime") || tr.GetRoot().ToString().Contains("JSInterop"))) || ctx.Repo.FilesWithExtension(".d.ts").Any();
        ctx.Measure("X8", Shape.FromShare(1 - (double)missing.Count / calls.Count) * (guarded ? 1 : 0.9), note: $"{missing.Count} of {calls.Count} interop names unresolved; boundary guarded by tests/types: {guarded}");
    }
}
