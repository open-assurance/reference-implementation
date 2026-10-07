using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Cai.Reference.Engine;

public sealed record CommitInfo(string Sha, string Author, DateTimeOffset Date, IReadOnlyList<(string path, int added, int deleted)> Files);

/// <summary>The repository's history as git reports it: commit metadata with per-file churn on the HEAD lineage.</summary>
public sealed class GitHistory
{
    public required IReadOnlyList<CommitInfo> Commits { get; init; }    // newest first, no merges
    public required bool Available { get; init; }
    public required IReadOnlyList<string> Tags { get; init; }

    public static GitHistory Load(Repository repo)
    {
        if (!repo.HasGit) return new GitHistory { Commits = Array.Empty<CommitInfo>(), Available = false, Tags = Array.Empty<string>() };
        Git.Run(repo.Root, "log --no-merges --numstat --date=iso-strict --format=%x01%H%x1f%ae%x1f%an%x1f%aI HEAD", out var log);
        var commits = new List<CommitInfo>();
        foreach (var block in log.Split('\u0001', StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = block.Split('\n');
            var head = lines[0].Split('\u001f');
            if (head.Length < 4) continue;
            var author = (head[1].Length > 0 ? head[1] : head[2]).Trim().ToLowerInvariant();
            DateTimeOffset.TryParse(head[3].Trim(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var date);
            var files = new List<(string, int, int)>();
            foreach (var l in lines.Skip(1))
            {
                var parts = l.Split('\t');
                if (parts.Length < 3) continue;
                var path = parts[2].Trim();
                var m = Regex.Match(path, @"^(.*)\{(.*) => (.*)\}(.*)$");
                if (m.Success) path = (m.Groups[1].Value + m.Groups[3].Value + m.Groups[4].Value).Replace("//", "/");
                else if (path.Contains(" => ")) path = path.Split(" => ")[1];
                int.TryParse(parts[0], out var a); int.TryParse(parts[1], out var d);
                files.Add((path, a, d));
            }
            commits.Add(new CommitInfo(head[0].Trim(), author, date, files));
        }
        Git.Run(repo.Root, "tag --list", out var tags);
        return new GitHistory { Commits = commits, Available = true, Tags = tags.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).OrderBy(t => t, StringComparer.Ordinal).ToList() };
    }

    /// <summary>Every added line across all reachable history, streamed: (commit, path, line number in the new file, text).</summary>
    public static IEnumerable<(string sha, string path, int line, string text)> AddedLines(Repository repo)
    {
        if (!repo.HasGit) yield break;
        var psi = new ProcessStartInfo("git", "log --all -p -U0 --no-color --diff-filter=AM --format=%x01%H") { WorkingDirectory = repo.Root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = System.Text.Encoding.UTF8 };
        psi.Environment["LC_ALL"] = "C";
        using var p = Process.Start(psi)!;
        p.StandardError.ReadToEndAsync();
        string? sha = null, path = null; var line = 0;
        string? l;
        while ((l = p.StandardOutput.ReadLine()) is not null)
        {
            if (l.Length > 0 && l[0] == '\u0001') { sha = l[1..].Trim(); path = null; continue; }
            if (l.StartsWith("+++ b/", StringComparison.Ordinal)) { path = l[6..]; continue; }
            if (l.StartsWith("+++ ", StringComparison.Ordinal)) { path = null; continue; }
            if (l.StartsWith("@@", StringComparison.Ordinal)) { var m = Regex.Match(l, @"\+(\d+)"); line = m.Success ? int.Parse(m.Groups[1].Value) : 0; continue; }
            if (path is null || sha is null) continue;
            if (l.StartsWith("+", StringComparison.Ordinal) && !l.StartsWith("+++", StringComparison.Ordinal)) { yield return (sha, path, line, l[1..]); line++; }
            else if (l.StartsWith("\\", StringComparison.Ordinal)) { }
        }
        p.WaitForExit();
    }

    /// <summary>Commits (from the HEAD lineage) that touched a path, newest first.</summary>
    public IEnumerable<CommitInfo> Touching(string path) => Commits.Where(c => c.Files.Any(f => f.path == path));
}
