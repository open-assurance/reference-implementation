using System.Text.RegularExpressions;

namespace Cai.Reference.Engine;

/// <summary>
/// D13 (secrets in the working tree) and D28 (secrets anywhere in history). Format-anchored rules first, then
/// assignment-shaped generic rules with an entropy floor; placeholders, templates, public material and substitution
/// tokens are excluded by their shape, never by file name.
/// </summary>
public static class Secrets
{
    public sealed record Rule(string Concept, string Name, Regex Pattern, double Weight, bool NeedsEntropy = false, int Group = 0, bool RealFormat = false);

    private static Regex R(string p) => new(p, RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static readonly IReadOnlyList<Rule> Rules = new List<Rule>
    {
        new("hardcoded-credential", "AWS access key id", R(@"\b((?:AKIA|ASIA|AROA|AIDA)[0-9A-Z]{16})\b"), 2, Group: 1, RealFormat: true),
        new("hardcoded-credential", "AWS secret access key", R(@"(?i)secret_?(?:access_?)?key[""']?\s*(?:[:=]|=>)\s*[""']?([A-Za-z0-9/+=]{40})\b"), 2, Group: 1, RealFormat: true),
        new("hardcoded-credential", "GitHub token", R(@"\b((?:ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{36,255}|github_pat_[A-Za-z0-9_]{60,})\b"), 2, Group: 1, RealFormat: true),
        new("hardcoded-credential", "GitLab token", R(@"\b(glpat-[A-Za-z0-9\-_]{20,})\b"), 2, Group: 1, RealFormat: true),
        new("hardcoded-credential", "Slack token", R(@"\b(xox[abprs]-[A-Za-z0-9-]{10,})\b"), 2, Group: 1, RealFormat: true),
        new("hardcoded-credential", "Slack webhook", R(@"(https://hooks\.slack\.com/services/T[A-Za-z0-9]+/B[A-Za-z0-9]+/[A-Za-z0-9]{20,})"), 2, Group: 1, RealFormat: true),
        new("hardcoded-credential", "Stripe key", R(@"\b((?:sk|rk)_(?:live|test)_[A-Za-z0-9]{20,})\b"), 2, Group: 1, RealFormat: true),
        new("hardcoded-credential", "Google API key", R(@"\b(AIza[0-9A-Za-z\-_]{35})\b"), 2, Group: 1, RealFormat: true),
        new("hardcoded-credential", "SendGrid key", R(@"\b(SG\.[A-Za-z0-9_\-]{22}\.[A-Za-z0-9_\-]{43})\b"), 2, Group: 1, RealFormat: true),
        new("hardcoded-credential", "npm token", R(@"\b(npm_[A-Za-z0-9]{36})\b"), 2, Group: 1, RealFormat: true),
        new("hardcoded-credential", "NuGet API key", R(@"\b(oy2[a-z0-9]{43})\b"), 2, Group: 1, RealFormat: true),
        new("hardcoded-credential", "Azure storage account key", R(@"(?i)AccountKey\s*=\s*([A-Za-z0-9+/]{86}==)"), 2, Group: 1, RealFormat: true),
        new("hardcoded-credential", "Azure shared access signature", R(@"(?i)(?:SharedAccessSignature|[?&]sig)=([A-Za-z0-9%+/]{40,})"), 1.5, Group: 1),
        new("hardcoded-credential", "bearer token literal", R(@"(?i)Bearer\s+([A-Za-z0-9\-._~+/]{24,}=*)"), 2, Group: 1, NeedsEntropy: true),
        new("hardcoded-credential", "bearer token literal", R(@"AuthenticationHeaderValue\(\s*""Bearer""\s*,\s*""([A-Za-z0-9\-._~+/]{20,}=*)""\s*\)"), 2, Group: 1, NeedsEntropy: true),
        new("hardcoded-cryptographic-key", "dotenv signing / encryption key", R(@"(?i)^\s*(?:export\s+)?[A-Za-z][\w]*(?:SIGNING|ENCRYPTION|JWT|HMAC|AES|CIPHER|MASTER)[\w]*(?:KEY|SECRET)\w*\s*=\s*[""']?([A-Za-z0-9\-_./+=!@#$%^&*]{16,})[""']?\s*$"), 2, Group: 1, NeedsEntropy: true),
        new("hardcoded-password", "dotenv password", R(@"(?i)^\s*(?:export\s+)?[A-Za-z][\w]*(?:PASSWORD|PASSWD|PWD)\w*\s*=\s*[""']?([^""'\s]{6,})[""']?\s*$"), 2, Group: 1),
        new("hardcoded-credential", "dotenv secret / token / key", R(@"(?i)^\s*(?:export\s+)?[A-Za-z][\w]*(?:SECRET|TOKEN|API_?KEY|ACCESS_?KEY)\w*\s*=\s*[""']?([A-Za-z0-9\-_./+=]{16,})[""']?\s*$"), 1.5, Group: 1, NeedsEntropy: true),
        new("hardcoded-credential", "JSON Web Token", R(@"\b(eyJ[A-Za-z0-9_-]{10,}\.eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,})\b"), 1.5, Group: 1, RealFormat: true),
        new("hardcoded-credential", "generic api key / token assignment", R(@"(?i)\b(?:api[_-]?key|apikey|auth[_-]?token|access[_-]?token|client[_-]?secret|secret[_-]?key|private[_-]?token|app[_-]?secret|token)[""']?\s*(?:[:=]|=>|,)\s*[""']([A-Za-z0-9\-_./+=]{20,})[""']"), 1.5, Group: 1, NeedsEntropy: true),
        new("hardcoded-password", "password in connection string", R(@"(?i)(?:password|pwd)\s*=\s*([^;""'\s]{6,})(?=[;""'\s]|$)"), 2, Group: 1),
        new("hardcoded-password", "password assignment", R(@"(?i)\b(?:password|passwd|pwd|db_?pass(?:word)?|smtp_?pass(?:word)?|[a-z_]*password)[""']?\s*(?:[:=]|=>)\s*[""']([^""'\s]{6,})[""']"), 2, Group: 1),
        new("hardcoded-password", "password in URI", R(@"(?i)\b[a-z][a-z0-9+.-]*://[^/\s:@""']+:([^@\s""']{6,})@[^\s""']+"), 2, Group: 1),
        new("hardcoded-cryptographic-key", "signing / encryption key assignment", R(@"(?i)\b(?:signing[_-]?key|encryption[_-]?key|jwt[_:]*(?:secret|key)|hmac[_-]?(?:secret|key)|aes[_-]?key|symmetric[_-]?key|issuer[_-]?signing[_-]?key|cipher[_-]?key|master[_-]?key|secret)[""']?\s*(?:[:=]|=>)\s*[""']([A-Za-z0-9\-_./+=!@#$%^&*]{16,})[""']"), 2, Group: 1, NeedsEntropy: true),
        new("hardcoded-cryptographic-key", "key bytes from a string literal", R(@"(?:Encoding\.(?:UTF8|ASCII|Unicode|Latin1)\.GetBytes|Convert\.FromBase64String|Convert\.FromHexString)\(\s*""([A-Za-z0-9\-_./+=!@#$%^&*]{16,})""\s*\)"), 2, Group: 1, NeedsEntropy: true),
        new("committed-private-key", "PEM private key", R(@"-----BEGIN (?:RSA |EC |DSA |OPENSSH |PGP |ENCRYPTED )?PRIVATE KEY(?: BLOCK)?-----"), 2),
        new("committed-private-key", "PuTTY private key", R(@"PuTTY-User-Key-File-\d"), 2),
    };

    private static readonly Regex Placeholder = new(@"(?i)^(?:<[^>]*>|\$\{[^}]*\}|\$\([^)]*\)|__[A-Z0-9_]+__|%[A-Z0-9_]+%|\{\{[^}]*\}\}|\{[A-Za-z_][\w.:-]*\}|x{6,}|\*{3,}|\.{3,}|#{3,}|changeme|change[-_ ]?me|replace[-_ ]?me|your[-_]?.*|placeholder|example|sample|dummy|todo|tbd|redacted|secret|password|passw0rd|p@ssw0rd|null|none|n/a|undefined|\[.*\]|@\w+@)$", RegexOptions.Compiled);
    private static readonly Regex PlaceholderWord = new(@"(?i)your[-_]|<[a-z][\w-]*>|example|sample|placeholder|changeme|replace|xxxx|dummy|insert|redacted|__\w+__|\$\{|\$\(|\{\{|%\w+%|@\w+@|here$", RegexOptions.Compiled);
    private static readonly Regex TextExt = new(@"\.(cs|csproj|props|targets|json|xml|config|yml|yaml|toml|ini|env|txt|md|ps1|sh|bash|cmd|bat|sql|tf|tfvars|hcl|razor|cshtml|html|js|ts|py|rb|go|java|kt|properties|conf|cfg|pem|key|crt|pub|ppk|dockerfile)$|(^|/)(Dockerfile[^/]*|Makefile|\.env[^/]*|\.npmrc|\.pypirc|\.netrc|\.git-credentials)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LockOrHash = new(@"(?i)(^|/)(packages\.lock\.json|package-lock\.json|yarn\.lock|pnpm-lock\.yaml|Cargo\.lock|go\.sum|poetry\.lock|composer\.lock|Gemfile\.lock|[^/]*\.sum|[^/]*\.sha256|[^/]*\.min\.(js|css))$", RegexOptions.Compiled);

    public static void Run(ScanContext ctx)
    {
        var repo = ctx.Repo;
        var current = new List<Finding>();
        foreach (var file in repo.Files)
        {
            if (LockOrHash.IsMatch(file)) continue;
            if (IsBinaryKeyContainer(file, repo, out var kind)) { current.Add(new Finding("committed-private-key", "D13", $"{kind} committed at {file}: a key container that carries the private half", file, null, null, 2)); continue; }
            if (!TextExt.IsMatch(file)) continue;
            var text = repo.Text(file);
            if (text.Length == 0 || text.Length > 2_000_000) continue;
            foreach (var (concept, name, line, snippet, weight) in Scan(text, file))
                current.Add(new Finding(concept, "D13", $"{name}: {snippet}", file, line, line, weight));
        }
        foreach (var f in current) ctx.Add(f);
        ctx.Measure("D13", Shape.FromFindings(current, Math.Max(1.0, repo.Files.Count / 100.0), 1.5), note: $"{current.Count} secret(s) in the working tree across {current.Select(f => f.File).Distinct().Count()} file(s)");

        if (!repo.HasGit) { ctx.Skip("D28", "no git history available"); return; }
        var currentKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in current.Where(f => f.Line is not null)) { var ls = repo.Text(f.File!).Split('\n'); if (f.Line!.Value - 1 < ls.Length) currentKeys.Add(Norm(ls[f.Line.Value - 1])); }
        var history = new List<Finding>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (sha, path, line, added) in GitHistory.AddedLines(repo))
        {
            if (LockOrHash.IsMatch(path) || !TextExt.IsMatch(path)) continue;
            foreach (var (concept, name, _, snippet, weight) in Scan(added + "\n", path))
            {
                var key = $"{path}\u0001{Norm(added)}";
                if (!seen.Add(key)) continue;   // the same line re-added later is one finding, at its first commit (git log is newest-first, so keep the oldest)
                var retained = !currentKeys.Contains(Norm(added));
                history.Add(new Finding(retained ? "secret-in-version-history" : concept, "D28", $"{name} committed in {sha[..Math.Min(8, sha.Length)]}{(retained ? ", since removed but never rotated" : ", still present")}: {snippet}", path, line, line, retained ? 2 : weight, sha));
            }
        }
        // keep the oldest commit per secret: AddedLines streams newest-first, so re-key by content and take the last seen
        var oldest = history.GroupBy(f => (f.File, Norm(f.Message))).Select(g => g.Last()).ToList();
        foreach (var f in oldest) ctx.Add(f);
        ctx.Measure("D28", Shape.FromFindings(oldest, Math.Max(1.0, repo.Files.Count / 100.0), 1.5), note: $"{oldest.Count} secret(s) ever committed, {oldest.Count(f => f.RuleId == "secret-in-version-history")} since removed");
    }

    private static string Norm(string s) => Regex.Replace(s, @"\s+", "");

    public static IEnumerable<(string concept, string name, int line, string snippet, double weight)> Scan(string text, string file)
    {
        var lines = text.Split('\n');
        var isTemplateFile = Regex.IsMatch(file, @"(?i)(^|[./_-])(example|sample|template|dist)([./_-]|$)");
        var isTestFile = Repository.IsTestPath(file);
        var isProse = Regex.IsMatch(file, @"(?i)\.(md|rst|txt|adoc)$");
        var isJson = file.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length is > 4000 or < 8) continue;
            foreach (var rule in Rules)
            {
                var m = rule.Pattern.Match(line);
                if (!m.Success) continue;
                var value = rule.Group > 0 ? m.Groups[rule.Group].Value : m.Value;
                if (rule.Group > 0 && !Plausible(value, rule, line)) continue;
                if (rule.Concept != "committed-private-key")
                {
                    if (isTemplateFile) continue;                       // a template documents the shape of a secret; it holds none
                    if (isTestFile) continue;                           // fixtures for faked services under tests/ are test data, not credentials
                    if (isProse && !Verified(rule, value)) continue;    // prose shows the shape of a secret; only a token whose own checksum verifies is a leak there
                    if (isJson && rule.Name.Contains("connection string") && !ConnectionStringValue(line, m.Index)) continue;
                }
                yield return (rule.Concept, rule.Name, i + 1, Redact(line.Trim(), value), rule.Weight);
                break;
            }
        }
    }

    /// <summary>A GitHub token carries a CRC-32 of its body in its last six base-62 characters: a value that verifies is a real token wherever it sits.</summary>
    public static bool Verified(Rule rule, string value)
    {
        if (!rule.Name.Contains("GitHub")) return false;
        var m = Regex.Match(value, @"^(gh[pousr]_)([A-Za-z0-9]{30})([A-Za-z0-9]{6})$");
        if (!m.Success) return false;
        var crc = Crc32(System.Text.Encoding.ASCII.GetBytes(m.Groups[2].Value));
        const string alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
        var sb = new System.Text.StringBuilder(); var n = crc;
        do { sb.Insert(0, alphabet[(int)(n % 62)]); n /= 62; } while (n > 0);
        return sb.ToString().PadLeft(6, '0') == m.Groups[3].Value;
    }

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in data) { crc ^= b; for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1; }
        return ~crc;
    }

    /// <summary>In a JSON line, is the match inside a string value made only of `key=value;` pairs (a connection string), not inside prose?</summary>
    private static bool ConnectionStringValue(string line, int matchIndex)
    {
        var open = line.LastIndexOf('"', Math.Max(0, matchIndex - 1));
        if (open < 0) return true;
        var prefix = line[(open + 1)..matchIndex];
        return Regex.IsMatch(prefix, @"^(\s*[\w .()-]+\s*=\s*[^;""]*;\s*)*$");
    }

    /// <summary>Shape-level exclusions: placeholders, substitution tokens, digests, public identifiers, vendor examples, variable NAMES, runtime lookups.</summary>
    private static bool Plausible(string value, Rule rule, string line)
    {
        if (Placeholder.IsMatch(value) || PlaceholderWord.IsMatch(value)) return false;
        if (value.Contains("EXAMPLE") || value.Contains("Example")) return false;
        if (Regex.IsMatch(value, @"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", RegexOptions.IgnoreCase)) return false;   // a GUID is an identifier
        if (Regex.IsMatch(value, @"^[0-9a-f]{64}$", RegexOptions.IgnoreCase) || Regex.IsMatch(value, @"^sha(256|512)-", RegexOptions.IgnoreCase)) return false;   // digests
        if (!rule.RealFormat && Regex.IsMatch(value, @"^[A-Z][A-Z0-9_]{5,}$") && value.Contains('_')) return false;   // an ENVIRONMENT_VARIABLE name
        if (Regex.IsMatch(line, @"(?i)GetEnvironmentVariable|Environment\.|IConfiguration|Configuration\[|\[\s*""[\w:]+""\s*\]|options\.|Options\.|FromEnvironment|KeyVault|SecretClient|\bnameof\(|\bconst string \w*(Variable|Name|Key)\b")) return false;
        if (rule.NeedsEntropy && Entropy(value) < 3.0) return false;
        if (rule.Concept == "hardcoded-password" && Regex.IsMatch(value, @"^(?i)(true|false|null|password|secret|changeme|postgres|admin|root|guest|test|dev|local|example|mysql|sa)$")) return false;
        return true;
    }

    public static double Entropy(string s)
    {
        if (s.Length == 0) return 0;
        return -s.GroupBy(c => c).Select(g => (double)g.Count() / s.Length).Sum(p => p * Math.Log2(p));
    }

    private static string Redact(string line, string value)
    {
        var shown = value.Length <= 8 ? new string('*', value.Length) : value[..4] + new string('*', Math.Min(12, value.Length - 8)) + value[^4..];
        var redacted = value.Length > 0 ? line.Replace(value, shown) : line;
        return redacted.Length > 120 ? redacted[..120] + "…" : redacted;
    }

    private static bool IsBinaryKeyContainer(string file, Repository repo, out string kind)
    {
        kind = "";
        var ext = Path.GetExtension(file).ToLowerInvariant();
        if (ext is ".pfx" or ".p12") { kind = "PKCS#12 bundle (certificate + private key)"; return true; }
        if (ext is ".jks" or ".keystore") { kind = "Java keystore"; return true; }
        if (ext is ".snk")
        {
            var bytes = File.ReadAllBytes(repo.Abs(file));
            if (bytes.Length > 12 && bytes[0] == 0x07 && bytes[1] == 0x02) { kind = "strong-name key PAIR (PRIVATEKEYBLOB)"; return true; }   // 0x06 0x02 is PUBLICKEYBLOB: fine
        }
        return false;
    }
}
