using System.Text.Json;
using System.Text.Json.Nodes;
using Cai.Reference.Engine;
using Json.Schema;

namespace Cai.Reference.Tests;

/// <summary>End-to-end: a small repository scanned through the CLI pipeline yields a schema-valid evidence bundle, a SARIF 2.1.0 log, and byte-identical output on a second run.</summary>
public class ScanOutputTests
{
    private static Fixture SampleRepo() => new Fixture()
        .Add("README.md", "# Shop\n\nA sample.\n")
        .Project("Shop.Domain", sources: ("Order.cs", """
            namespace Shop.Domain;
            public sealed class Order
            {
                public Guid Id { get; } = Guid.NewGuid();
                public decimal Total { get; private set; }
                public void Add(int quantity, decimal price) { if (quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity)); Total += quantity * price; }
            }
            """))
        .Project("Shop.Web", web: true, sources: ("Program.cs", """
            var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder(args);
            var app = builder.Build();
            app.MapGet("/health", () => "ok");
            app.Run();
            """))
        .Project("Shop.Tests", test: true, sources: ("OrderTests.cs", """
            using Shop.Domain;
            public class OrderTests { [Xunit.Fact] public void Adds() { var o = new Order(); o.Add(2, 3m); Xunit.Assert.Equal(6m, o.Total); } }
            """));

    private static string Scan(Fixture fx, string outDir)
    {
        Directory.CreateDirectory(fx.Root);
        var ctxArgs = new Args(new[] { "scan", fx.Root, "--out", outDir, "--quiet", "--rubrics", ScoringVectorTests.RubricsDir, "--data", Fixture.FindData() });
        // the fixture materialises files on Run(); write them the same way without running detectors
        fx.Run(_ => { });
        Assert.Equal(0, ScanCommand.Run(ctxArgs));
        return outDir;
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "schemas", "cai-delivery-1.0.schema.json"))) dir = Path.GetDirectoryName(dir);
        return dir!;
    }

    [Fact]
    public void Evidence_bundle_validates_against_the_published_delivery_schema()
    {
        using var fx = SampleRepo();
        var outDir = Scan(fx, fx.Root + ".out");
        var schemaDoc = JsonSchema.FromFile(Path.Combine(RepoRoot(), "schemas", "cai-delivery-1.0.schema.json"));
        // validate the embedded-evidence definition on its own
        var evidenceSchema = JsonSchema.FromText(JsonSerializer.Serialize(JsonNode.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "schemas", "cai-delivery-1.0.schema.json")))!["$defs"]!["evidence"]));
        var evidenceText = File.ReadAllText(Path.Combine(outDir, "evidence.json"));
        var evidence = JsonNode.Parse(evidenceText);
        using var doc = JsonDocument.Parse(evidenceText);
        var result = evidenceSchema.Evaluate(doc.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(result.IsValid, JsonSerializer.Serialize(result));
        Assert.NotNull(schemaDoc);
        // the bundle names the catalog it was measured under and never carries an unmeasured dimension as a score
        Assert.Equal("rubric-2026.10.2", evidence!["rubricVersion"]!.GetValue<string>());
        var scored = evidence["dimensions"]!.AsArray().Select(d => d!["id"]!.GetValue<string>()).ToHashSet();
        var unmeasured = evidence["notMeasured"]!.AsArray().Select(d => d!["id"]!.GetValue<string>()).ToHashSet();
        Assert.Empty(scored.Intersect(unmeasured));
    }

    [Fact]
    public void Sarif_log_is_2_1_0_with_one_rule_per_concept_and_repo_relative_paths()
    {
        using var fx = SampleRepo();
        var outDir = Scan(fx, fx.Root + ".out");
        var sarif = JsonNode.Parse(File.ReadAllText(Path.Combine(outDir, "findings.sarif")))!;
        Assert.Equal("2.1.0", sarif["version"]!.GetValue<string>());
        var run = sarif["runs"]![0]!;
        var rules = run["tool"]!["driver"]!["rules"]!.AsArray().Select(r => r!["id"]!.GetValue<string>()).ToList();
        Assert.Equal(rules.Count, rules.Distinct().Count());
        foreach (var r in run["results"]!.AsArray())
        {
            Assert.Contains(r!["ruleId"]!.GetValue<string>(), rules);
            foreach (var loc in r["locations"]?.AsArray() ?? new JsonArray())
            {
                var uri = loc!["physicalLocation"]!["artifactLocation"]!["uri"]!.GetValue<string>();
                Assert.False(uri.StartsWith('/') || uri.Contains(":\\"), $"path is not repo-relative: {uri}");
            }
        }
    }

    [Fact]
    public void Scanning_the_same_tree_twice_is_byte_identical()
    {
        using var fx = SampleRepo();
        var a = Scan(fx, fx.Root + ".out-a");   // outside the tree: an output directory inside the repository would be scanned by the second run
        var b = Scan(fx, fx.Root + ".out-b");
        foreach (var name in new[] { "evidence.json", "findings.sarif", "scores.json" })
            Assert.Equal(File.ReadAllBytes(Path.Combine(a, name)), File.ReadAllBytes(Path.Combine(b, name)));
    }
}
