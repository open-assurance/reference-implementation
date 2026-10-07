using System.Text.Json;
using System.Text.RegularExpressions;

namespace Cai.Reference.Engine;

public sealed record Advisory(string Id, string[] Aliases, string Summary, string? Severity, string? Cvss, string? Withdrawn, AffectedPackage[] Affected)
{
    public bool IsMalicious => Id.StartsWith("MAL-", StringComparison.Ordinal);
}
public sealed record AffectedPackage(string Package, AdvisoryRange[] Ranges, string[] Versions);
public sealed record AdvisoryRange(string Type, (string? introduced, string? fixedVersion, string? lastAffected)[] Events);
public sealed record EolCycle(string Cycle, DateOnly? Eol, DateOnly? Release, bool Lts, string? Latest);

/// <summary>The pinned offline data the scan runs against; the bundle records which.</summary>
public sealed class DataSnapshots
{
    public required string Dir { get; init; }
    public required IReadOnlyList<Advisory> Advisories { get; init; }
    public required IReadOnlyList<EolCycle> DotnetCycles { get; init; }
    public required HashSet<string> CopyleftLicences { get; init; }
    public required HashSet<string> WeakCopyleftLicences { get; init; }
    public required Dictionary<string, string> Provenance { get; init; }   // file → "fetchedAt sha256"
    public DateOnly AsOf { get; init; }

    private Dictionary<string, List<Advisory>>? _byPackage;

    public IEnumerable<Advisory> For(string packageId)
    {
        _byPackage ??= Advisories.SelectMany(a => a.Affected.Select(x => (x.Package.ToLowerInvariant(), a))).GroupBy(t => t.Item1).ToDictionary(g => g.Key, g => g.Select(t => t.a).Distinct().ToList());
        return _byPackage.TryGetValue(packageId.ToLowerInvariant(), out var l) ? l : Enumerable.Empty<Advisory>();
    }

    /// <summary>Does the advisory affect this exact version? OSV semantics: introduced ≤ v &lt; fixed, or ≤ last_affected; explicit version lists when no range.</summary>
    public static bool Affects(Advisory a, string packageId, NuGetVersion v)
    {
        foreach (var ap in a.Affected)
        {
            if (!string.Equals(ap.Package, packageId, StringComparison.OrdinalIgnoreCase)) continue;
            if (ap.Ranges.Length == 0) { if (ap.Versions.Any(x => NuGetVersion.TryParse(x, out var pv) && pv.CompareTo(v) == 0)) return true; continue; }
            foreach (var r in ap.Ranges)
            {
                NuGetVersion? intro = null;
                foreach (var e in r.Events)
                {
                    if (e.introduced is not null) { intro = e.introduced == "0" ? NuGetVersion.Zero : NuGetVersion.TryParse(e.introduced, out var iv) ? iv : null; if (intro is not null && ReferenceEquals(e, r.Events[^1]) && v.CompareTo(intro.Value) >= 0) return true; continue; }
                    if (intro is null) continue;
                    if (e.fixedVersion is not null && NuGetVersion.TryParse(e.fixedVersion, out var fv)) { if (v.CompareTo(intro.Value) >= 0 && v.CompareTo(fv) < 0) return true; intro = null; }
                    else if (e.lastAffected is not null && NuGetVersion.TryParse(e.lastAffected, out var la)) { if (v.CompareTo(intro.Value) >= 0 && v.CompareTo(la) <= 0) return true; intro = null; }
                }
                if (intro is not null && v.CompareTo(intro.Value) >= 0) return true;   // open-ended range
            }
        }
        return false;
    }

    public static DataSnapshots Load(string dir)
    {
        var prov = new Dictionary<string, string>(StringComparer.Ordinal);
        var advisories = new List<Advisory>();
        DateOnly asOf = default;
        using (var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "osv-nuget.json"))))
        {
            var root = doc.RootElement;
            prov["osv-nuget.json"] = $"{root.GetProperty("fetchedAt").GetString()} sha256:{root.GetProperty("sha256").GetString()}";
            asOf = DateOnly.FromDateTime(DateTime.Parse(root.GetProperty("fetchedAt").GetString()!, null, System.Globalization.DateTimeStyles.RoundtripKind));
            foreach (var a in root.GetProperty("advisories").EnumerateArray())
            {
                var affected = a.GetProperty("affected").EnumerateArray().Select(x => new AffectedPackage(
                    x.GetProperty("package").GetString()!,
                    x.GetProperty("ranges").EnumerateArray().Select(r => new AdvisoryRange(r.GetProperty("type").GetString() ?? "", r.GetProperty("events").EnumerateArray()
                        .Select(e => (e.TryGetProperty("introduced", out var i) ? i.GetString() : null, e.TryGetProperty("fixed", out var f) ? f.GetString() : null, e.TryGetProperty("last_affected", out var l) ? l.GetString() : null)).ToArray())).ToArray(),
                    x.TryGetProperty("versions", out var vs) ? vs.EnumerateArray().Select(v => v.GetString()!).ToArray() : Array.Empty<string>())).ToArray();
                advisories.Add(new Advisory(a.GetProperty("id").GetString()!, a.GetProperty("aliases").EnumerateArray().Select(x => x.GetString()!).ToArray(),
                    a.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : "", a.TryGetProperty("severity", out var sev) ? sev.GetString() : null,
                    a.TryGetProperty("cvss", out var c) ? c.GetString() : null, a.TryGetProperty("withdrawn", out var w) ? w.GetString() : null, affected));
            }
        }
        var cycles = new List<EolCycle>();
        using (var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "dotnet-eol.json"))))
        {
            var root = doc.RootElement;
            prov["dotnet-eol.json"] = $"{root.GetProperty("fetchedAt").GetString()} sha256:{root.GetProperty("sha256").GetString()}";
            foreach (var c in root.GetProperty("cycles").EnumerateArray())
            {
                DateOnly? D(string n) => c.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String && DateOnly.TryParse(v.GetString(), out var d) ? d : null;
                cycles.Add(new EolCycle(c.GetProperty("cycle").GetString()!, D("eol"), D("releaseDate"), c.TryGetProperty("lts", out var lts) && lts.ValueKind == JsonValueKind.True, c.TryGetProperty("latest", out var la) ? la.GetString() : null));
            }
        }
        var copyleft = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var weak = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "licence-policy.json"))))
        {
            foreach (var x in doc.RootElement.GetProperty("copyleft").EnumerateArray()) copyleft.Add(x.GetString()!);
            foreach (var x in doc.RootElement.GetProperty("weakCopyleft").EnumerateArray()) weak.Add(x.GetString()!);
        }
        return new DataSnapshots { Dir = dir, Advisories = advisories, DotnetCycles = cycles, CopyleftLicences = copyleft, WeakCopyleftLicences = weak, Provenance = prov, AsOf = asOf };
    }

    public static string? FindDir()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var c = Path.Combine(dir, "data");
            if (File.Exists(Path.Combine(c, "osv-nuget.json"))) return c;
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        return null;
    }
}

/// <summary>NuGet version: up to four numeric parts, SemVer 2 pre-release labels; build metadata ignored.</summary>
public readonly struct NuGetVersion : IComparable<NuGetVersion>
{
    public readonly int Major, Minor, Patch, Revision; public readonly string[] Prerelease;
    public static readonly NuGetVersion Zero = new(0, 0, 0, 0, Array.Empty<string>());
    public NuGetVersion(int ma, int mi, int pa, int re, string[] pre) { Major = ma; Minor = mi; Patch = pa; Revision = re; Prerelease = pre; }
    public bool IsPrerelease => Prerelease.Length > 0;
    public static bool TryParse(string? s, out NuGetVersion v)
    {
        v = Zero; if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim(); if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase)) s = s[1..];
        var plus = s.IndexOf('+'); if (plus >= 0) s = s[..plus];
        var dash = s.IndexOf('-'); var pre = Array.Empty<string>();
        if (dash >= 0) { pre = s[(dash + 1)..].Split('.'); s = s[..dash]; }
        var parts = s.Split('.');
        if (parts.Length == 0 || parts.Length > 4) return false;
        var nums = new int[4];
        for (var i = 0; i < parts.Length; i++) if (!int.TryParse(parts[i], out nums[i])) return false;
        v = new NuGetVersion(nums[0], nums[1], nums[2], nums[3], pre); return true;
    }
    public int CompareTo(NuGetVersion o)
    {
        int c;
        if ((c = Major.CompareTo(o.Major)) != 0) return c; if ((c = Minor.CompareTo(o.Minor)) != 0) return c;
        if ((c = Patch.CompareTo(o.Patch)) != 0) return c; if ((c = Revision.CompareTo(o.Revision)) != 0) return c;
        if (Prerelease.Length == 0 && o.Prerelease.Length == 0) return 0;
        if (Prerelease.Length == 0) return 1; if (o.Prerelease.Length == 0) return -1;
        for (var i = 0; i < Math.Max(Prerelease.Length, o.Prerelease.Length); i++)
        {
            if (i >= Prerelease.Length) return -1; if (i >= o.Prerelease.Length) return 1;
            var a = Prerelease[i]; var b = o.Prerelease[i];
            var an = int.TryParse(a, out var ai); var bn = int.TryParse(b, out var bi);
            if (an && bn) { if ((c = ai.CompareTo(bi)) != 0) return c; }
            else if (an) return -1; else if (bn) return 1;
            else if ((c = string.Compare(a, b, StringComparison.OrdinalIgnoreCase)) != 0) return c;
        }
        return 0;
    }
    public override string ToString() => $"{Major}.{Minor}.{Patch}" + (Revision > 0 ? $".{Revision}" : "") + (Prerelease.Length > 0 ? "-" + string.Join('.', Prerelease) : "");
}
