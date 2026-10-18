using Cai.Reference.Engine;

namespace Cai.Reference.Tests;

public class DataSnapshotsTests
{
    private static Advisory MakeAdvisory(string packageId, params (string? introduced, string? fixedVersion, string? lastAffected)[] events) =>
        new Advisory("TEST-1", Array.Empty<string>(), "summary", null, null, null,
            new[] { new AffectedPackage(packageId, new[] { new AdvisoryRange("ECOSYSTEM", events) }, Array.Empty<string>()) });

    [Theory]
    [InlineData("1.2.0", true)]
    [InlineData("1.5.0", true)]
    [InlineData("1.1.9", false)]
    public void Affects_returns_true_for_versions_inside_an_open_ended_range_whose_last_event_is_introduced(string version, bool expected)
    {
        var a = MakeAdvisory("Foo", ("1.2.0", null, null));
        Assert.True(NuGetVersion.TryParse(version, out var v));
        Assert.Equal(expected, DataSnapshots.Affects(a, "Foo", v));
    }

    [Theory]
    [InlineData("0.5.0", true)]
    [InlineData("2.0.0", true)]
    public void Affects_returns_true_for_every_version_when_introduced_is_zero(string version, bool expected)
    {
        var a = MakeAdvisory("Foo", ("0", null, null));
        Assert.True(NuGetVersion.TryParse(version, out var v));
        Assert.Equal(expected, DataSnapshots.Affects(a, "Foo", v));
    }
}
