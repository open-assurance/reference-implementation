using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace Cai.Reference.Engine;

/// <summary>Infrastructure as code and the build pipeline: D31 (containers, Kubernetes, compose, Terraform), D36 (supply chain, CI workflow security), D40 D41 D42 (reward postures).</summary>
public static class Iac
{
    private sealed record Doc(string File, YamlMappingNode Root, int Line);

    public static void Run(ScanContext ctx)
    {
        var repo = ctx.Repo;
        var docs = new List<Doc>();
        foreach (var f in repo.FilesWithExtension(".yml", ".yaml").Where(f => !Readiness.WorkflowFiles(repo).Contains(f) && !Regex.IsMatch(f, @"(?i)(^|/)(\.github/|benchmark/|docs?/|mkdocs|\.pre-commit|dependabot|renovate|codecov|openapi|swagger|appsettings)")))
        {
            var text = repo.Text(f); if (text.Length == 0) continue;
            YamlStream ys; try { ys = new YamlStream(); ys.Load(new StringReader(text)); } catch (Exception) { continue; }
            foreach (var d in ys.Documents) if (d.RootNode is YamlMappingNode m) docs.Add(new Doc(f, m, (int)(m.Start.Line)));
        }
        var k8s = docs.Where(d => Str(d.Root, "kind") is not null && Str(d.Root, "apiVersion") is not null).ToList();
        var compose = docs.Where(d => d.Root.Children.ContainsKey(new YamlScalarNode("services")) && Regex.IsMatch(Path.GetFileName(d.File), @"(?i)compose")).ToList();
        var dockerfiles = repo.Files.Where(f => Path.GetFileName(f).StartsWith("Dockerfile", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".Dockerfile", StringComparison.OrdinalIgnoreCase)).ToList();
        var terraform = repo.FilesWithExtension(".tf").ToList();
        var findings = new List<Finding>();
        foreach (var df in dockerfiles) Dockerfile(ctx, findings, df);
        foreach (var d in k8s) Kubernetes(ctx, findings, d);
        foreach (var d in compose) Compose(ctx, findings, d);
        foreach (var tf in terraform) Terraform(ctx, findings, tf);
        foreach (var f in findings) ctx.Add(f);
        var surface = dockerfiles.Count + k8s.Count + compose.Count + terraform.Count;
        if (surface == 0) ctx.Skip("D31", "no Dockerfile, Kubernetes manifest, compose file or Terraform in the repository");
        else ctx.Measure("D31", Shape.FromFindings(findings, Math.Max(1.0, surface / 4.0), 0.8), note: $"{findings.Count} finding(s) across {dockerfiles.Count} Dockerfile(s), {k8s.Count} Kubernetes object(s), {compose.Count} compose file(s), {terraform.Count} Terraform file(s)");
        SupplyChain(ctx);
        Rewards(ctx, k8s);
    }

    private static string? Str(YamlMappingNode? m, string key) => m is not null && m.Children.TryGetValue(new YamlScalarNode(key), out var v) && v is YamlScalarNode s ? s.Value : null;
    private static YamlMappingNode? Map(YamlMappingNode? m, string key) => m is not null && m.Children.TryGetValue(new YamlScalarNode(key), out var v) && v is YamlMappingNode mm ? mm : null;
    private static YamlSequenceNode? Seq(YamlMappingNode? m, string key) => m is not null && m.Children.TryGetValue(new YamlScalarNode(key), out var v) && v is YamlSequenceNode ss ? ss : null;
    private static int L(YamlNode n) => (int)n.Start.Line;
    private static bool Bool(YamlMappingNode? m, string key, bool dflt) => Str(m ?? new YamlMappingNode(), key) is { } s ? s.Equals("true", StringComparison.OrdinalIgnoreCase) : dflt;

    // ---------------- Dockerfile ----------------
    private static void Dockerfile(ScanContext ctx, List<Finding> findings, string file)
    {
        var lines = ctx.Repo.Text(file).Split('\n');
        var stages = new List<(int line, string image, bool final)>();
        for (var i = 0; i < lines.Length; i++) { var m = Regex.Match(lines[i], @"^\s*FROM\s+(?:--platform=\S+\s+)?(\S+)(?:\s+AS\s+(\S+))?", RegexOptions.IgnoreCase); if (m.Success) stages.Add((i + 1, m.Groups[1].Value, false)); }
        if (stages.Count == 0) return;
        var final = stages[^1]; var finalStart = final.line - 1;
        var finalBody = string.Join("\n", lines.Skip(finalStart));
        var image = final.image;
        var scratchOrDistroless = Regex.IsMatch(image, @"(?i)^scratch$|distroless|chiseled|-nonroot|rootless");
        if (!Regex.IsMatch(image, @"@sha256:") && !Regex.IsMatch(image, @":[\w.-]+$") || Regex.IsMatch(image, @":latest$"))
            findings.Add(new Finding("mutable-image-reference", "D31", $"FROM {image}: the base image has no pinned tag or digest, so every build may pull a different image", file, final.line, final.line, 1));
        var user = Regex.Match(finalBody, @"(?m)^\s*USER\s+(\S+)");
        if (!user.Success && !scratchOrDistroless) findings.Add(new Finding("container-runs-as-root", "D31", "no USER instruction in the final stage: the process runs as root", file, final.line, final.line, 2));
        else if (user.Success && user.Groups[1].Value is "root" or "0") findings.Add(new Finding("container-runs-as-root", "D31", "USER root in the final stage", file, finalStart + finalBody[..user.Index].Count(c => c == '\n') + 1, null, 2));
        if (!Regex.IsMatch(finalBody, @"(?m)^\s*HEALTHCHECK") && !scratchOrDistroless && !Regex.IsMatch(image, @"(?i)sdk") && !ProbedByKubernetes(ctx, file)) findings.Add(new Finding("missing-image-healthcheck", "D31", "no HEALTHCHECK instruction: the runtime cannot tell a hung container from a healthy one", file, final.line, final.line, 1));
        for (var i = 0; i < lines.Length; i++)
        {
            var l = lines[i];
            if (Regex.IsMatch(l, @"(curl|wget)\s[^|]*\|\s*(ba)?sh\b") || Regex.IsMatch(l, @"(curl|wget)\s.*&&\s*(ba)?sh\s+\S*\.sh") && !Regex.IsMatch(l, @"sha256sum|--checksum|gpg --verify"))
                findings.Add(new Finding("download-without-integrity-check", "D31", "a remote script is downloaded and executed with no checksum or signature check", file, i + 1, i + 1, 2));
            if (Regex.IsMatch(l, @"(?i)^\s*(ENV|ARG)\s+\w*(PASSWORD|SECRET|TOKEN|API_?KEY)\w*\s*=?\s*\S{8,}") && !Regex.IsMatch(l, @"\$\{?\w+\}?|^\s*ARG\s+\w+\s*$"))
                findings.Add(new Finding("hardcoded-credential", "D31", "a secret is baked into the image as ENV/ARG", file, i + 1, i + 1, 2));
            if (Regex.IsMatch(l, @"(?i)^\s*EXPOSE\s+22\b")) findings.Add(new Finding("container-excessive-privilege", "D31", "EXPOSE 22: an SSH daemon in a container", file, i + 1, i + 1, 1));
        }
    }

    /// <summary>Kubernetes ignores HEALTHCHECK: when a workload manifest names this image's project and carries probes, the probes are the health check.</summary>
    private static bool ProbedByKubernetes(ScanContext ctx, string dockerfile)
    {
        var project = Path.GetFileName(Path.GetDirectoryName(dockerfile) ?? "") ?? "";
        var stem = project.Split('.').Last().ToLowerInvariant();
        if (stem.Length < 3) return false;
        foreach (var f in ctx.Repo.FilesWithExtension(".yml", ".yaml"))
        {
            var text = ctx.Repo.Text(f);
            if (!Regex.IsMatch(text, @"(?m)^kind:\s*(Deployment|StatefulSet|DaemonSet|CronJob|Job)")) continue;
            if (text.Contains(stem, StringComparison.OrdinalIgnoreCase)) return true;   // the orchestrator ignores HEALTHCHECK; missing probes are reported on the workload itself
        }
        return false;
    }

    // ---------------- Kubernetes ----------------
    private static void Kubernetes(ScanContext ctx, List<Finding> findings, Doc d)
    {
        var kind = Str(d.Root, "kind") ?? "";
        var file = d.File;
        if (kind is "Deployment" or "StatefulSet" or "DaemonSet" or "Job" or "CronJob" or "Pod" or "ReplicaSet")
        {
            var spec = Map(d.Root, "spec");
            var podSpec = kind == "Pod" ? spec : kind == "CronJob" ? Map(Map(Map(Map(spec, "jobTemplate"), "spec"), "template"), "spec") : Map(Map(spec, "template"), "spec");
            if (podSpec is null) return;
            var podSc = Map(podSpec, "securityContext");
            if (Bool(podSpec, "hostNetwork", false) || Bool(podSpec, "hostPID", false) || Bool(podSpec, "hostIPC", false))
                findings.Add(new Finding("host-namespace-sharing", "D31", $"{kind} shares a host namespace (hostNetwork/hostPID/hostIPC)", file, L(podSpec), null, 2));
            if (Bool(podSpec, "automountServiceAccountToken", true) && Str(podSpec, "serviceAccountName") is null && !Regex.IsMatch(ctx.Repo.Text(file), @"automountServiceAccountToken:\s*false"))
                findings.Add(new Finding("automounted-service-account-token", "D31", $"{kind} mounts the default service-account token although nothing in it talks to the API server", file, L(podSpec), null, 1));
            foreach (var vol in Seq(podSpec, "volumes")?.OfType<YamlMappingNode>() ?? Enumerable.Empty<YamlMappingNode>())
                if (Map(vol, "hostPath") is { } hp) findings.Add(new Finding("host-path-mount", "D31", $"hostPath {Str(hp, "path")} mounted into the pod{(Str(hp, "path")?.Contains("docker.sock") == true ? " (the container runtime socket: root on the node)" : "")}", file, L(vol), null, 2));
            var strategy = Map(spec, "strategy");
            foreach (var c in (Seq(podSpec, "containers")?.OfType<YamlMappingNode>() ?? Enumerable.Empty<YamlMappingNode>()).Concat(Seq(podSpec, "initContainers")?.OfType<YamlMappingNode>() ?? Enumerable.Empty<YamlMappingNode>()))
            {
                var name = Str(c, "name") ?? "container"; var sc = Map(c, "securityContext");
                var image = Str(c, "image") ?? "";
                if (image.Length > 0 && !image.Contains("@sha256:") && (!Regex.IsMatch(image, @":[\w.-]+$") || image.EndsWith(":latest")))
                    findings.Add(new Finding("mutable-image-reference", "D31", $"{name} runs image {image}: no immutable tag or digest", file, L(c), null, 1));
                if (sc is null && podSc is null) findings.Add(new Finding("container-security-context-missing", "D31", $"{name} declares no securityContext: runs as the image's default user with every default capability", file, L(c), null, 1));
                if (Bool(sc, "privileged", false)) findings.Add(new Finding("privileged-container", "D31", $"{name} is privileged: equivalent to root on the node", file, L(sc!), null, 3));
                var runAsNonRoot = Bool(sc, "runAsNonRoot", Bool(podSc, "runAsNonRoot", false)); var runAsUser = Str(sc, "runAsUser") ?? Str(podSc, "runAsUser");
                if (!runAsNonRoot && (runAsUser is null || runAsUser == "0") && (sc is not null || podSc is not null) && !Bool(sc, "privileged", false)) findings.Add(new Finding("container-runs-as-root", "D31", $"{name} may run as root: neither runAsNonRoot nor a non-zero runAsUser is set", file, L(sc ?? podSc!), null, 2));
                if (Bool(sc, "allowPrivilegeEscalation", true) && sc is not null && !Bool(sc, "privileged", false)) findings.Add(new Finding("container-privilege-escalation-allowed", "D31", $"{name} leaves allowPrivilegeEscalation at its default (true)", file, L(sc), null, 1));
                var caps = Map(sc, "capabilities");
                var adds = Seq(caps, "add")?.OfType<YamlScalarNode>().Select(x => x.Value).ToList() ?? new();
                var drops = Seq(caps, "drop")?.OfType<YamlScalarNode>().Select(x => x.Value).ToList() ?? new();
                if (adds.Any(a => a is "SYS_ADMIN" or "NET_ADMIN" or "ALL" or "SYS_PTRACE" or "DAC_OVERRIDE" or "SYS_MODULE")) findings.Add(new Finding("container-excess-capabilities", "D31", $"{name} adds {string.Join(", ", adds)}", file, L(caps!), null, 2));
                else if (sc is not null && !drops.Any(x => x is "ALL") && !Bool(sc, "privileged", false)) findings.Add(new Finding("container-excess-capabilities", "D31", $"{name} keeps the default capability set (no `drop: [ALL]`)", file, L(sc), null, 0.5));
                if (sc is not null && !Bool(sc, "readOnlyRootFilesystem", false)) findings.Add(new Finding("container-writable-root-filesystem", "D31", $"{name} has a writable root filesystem", file, L(sc), null, 0.5));
                var seccomp = Map(sc, "seccompProfile") ?? Map(podSc, "seccompProfile");
                var apparmor = Map(Map(Map(Map(spec, "template"), "metadata"), "annotations"), "") ; // placeholder
                var annotations = Map(Map(Map(spec, "template"), "metadata"), "annotations") ?? Map(Map(d.Root, "metadata"), "annotations");
                var hasApparmor = annotations?.Children.Keys.OfType<YamlScalarNode>().Any(k => k.Value?.StartsWith("container.apparmor.security.beta.kubernetes.io") == true) == true || Map(sc, "appArmorProfile") is not null || Map(podSc, "appArmorProfile") is not null;
                if (seccomp is null && !hasApparmor && (sc is not null || podSc is not null)) findings.Add(new Finding("container-confinement-profile-unset", "D31", $"{name} has no seccomp or AppArmor profile", file, L(sc ?? podSc!), null, 0.5));
                var resources = Map(c, "resources");
                if (Map(resources, "limits") is null) findings.Add(new Finding("container-missing-resource-limits", "D31", $"{name} has no resource limits: one runaway pod can starve the node", file, L(c), null, 1));
                if (Map(resources, "requests") is null) findings.Add(new Finding("container-missing-resource-requests", "D31", $"{name} has no resource requests: the scheduler places it blind", file, L(c), null, 0.5));
                if (kind is "Deployment" or "StatefulSet" or "DaemonSet" && Map(c, "livenessProbe") is null && Map(c, "readinessProbe") is null) findings.Add(new Finding("missing-health-probes", "D31", $"{name} has neither a liveness nor a readiness probe", file, L(c), null, 1));
                foreach (var env in Seq(c, "env")?.OfType<YamlMappingNode>() ?? Enumerable.Empty<YamlMappingNode>())
                    if (Regex.IsMatch(Str(env, "name") ?? "", @"(?i)PASSWORD|SECRET|TOKEN|API_?KEY|CONNECTION_?STRING") && Str(env, "value") is { Length: > 5 } v && !Regex.IsMatch(v, @"^\$|^\{\{|^<|placeholder|changeme", RegexOptions.IgnoreCase) && Map(env, "valueFrom") is null)
                        findings.Add(new Finding("hardcoded-credential", "D31", $"{Str(env, "name")} is set to a literal value in the manifest instead of a secretKeyRef", file, L(env), null, 2));
                if (!Regex.IsMatch(image, @"^(mcr\.microsoft\.com|ghcr\.io|registry\.k8s\.io|gcr\.io|quay\.io|public\.ecr\.aws|docker\.io|[\w.-]+\.azurecr\.io|[\w.-]+\.amazonaws\.com|[\w.-]+\.pkg\.dev|[\w.-]+/)") && image.Length > 0 && !image.Contains('/'))
                    findings.Add(new Finding("image-not-from-allowed-registry", "D31", $"{name} pulls {image} from Docker Hub's default namespace with no explicit registry", file, L(c), null, 0.5));
            }
        }
        else if (kind is "Role" or "ClusterRole")
        {
            foreach (var rule in Seq(d.Root, "rules")?.OfType<YamlMappingNode>() ?? Enumerable.Empty<YamlMappingNode>())
            {
                var verbs = Seq(rule, "verbs")?.OfType<YamlScalarNode>().Select(x => x.Value).ToList() ?? new(); var res = Seq(rule, "resources")?.OfType<YamlScalarNode>().Select(x => x.Value).ToList() ?? new();
                if (verbs.Contains("*") || res.Contains("*") || res.Contains("secrets") && verbs.Any(v => v is "list" or "*" or "get" && res.Count == 1 && kind == "ClusterRole"))
                    findings.Add(new Finding("overly-permissive-rbac", "D31", $"{kind} {Str(Map(d.Root, "metadata"), "name")} grants {string.Join(",", verbs)} on {string.Join(",", res)}", file, L(rule), null, 2));
            }
        }
        else if (kind is "ClusterRoleBinding" or "RoleBinding")
        {
            if (Str(Map(d.Root, "roleRef"), "name") is "cluster-admin" or "admin") findings.Add(new Finding("overly-permissive-rbac", "D31", $"{kind} binds {Str(Map(d.Root, "roleRef"), "name")}", file, L(d.Root), null, 2));
        }
        else if (kind is "Secret")
        {
            foreach (var (section, encoded) in new[] { ("data", true), ("stringData", false) })
            {
                var map = Map(d.Root, section); if (map is null) continue;
                foreach (var (k, v) in map.Children)
                {
                    if (v is not YamlScalarNode sv || sv.Value is null) continue;
                    var value = sv.Value;
                    if (encoded) { try { value = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(value.Trim())); } catch (FormatException) { continue; } }
                    if (value.Length < 8 || Regex.IsMatch(value, @"^\$\{|^<|^\{\{|placeholder|changeme|REPLACE", RegexOptions.IgnoreCase)) continue;
                    var keyName = (k as YamlScalarNode)?.Value ?? "";
                    var hit = Secrets.Scan($"{keyName}: {value}\n", file).FirstOrDefault();
                    if (hit.concept is null && Regex.IsMatch(keyName, @"(?i)token|secret|password|key|credential") && Secrets.Entropy(value) >= 3.0) hit = ("hardcoded-credential", "secret material in a committed manifest", 1, keyName, 2);
                    if (hit.concept is not null) findings.Add(new Finding(hit.concept, "D31", $"kind: Secret `{Str(Map(d.Root, "metadata"), "name")}` carries {keyName} in the repository ({(encoded ? "base64 is an encoding, not encryption" : "in clear text")}): anyone who can read the repository holds it", file, L(v), null, 2));
                }
            }
        }
        else if (kind is "Ingress")
        {
            var spec = Map(d.Root, "spec");
            if (Seq(spec, "tls") is null && !(Map(Map(d.Root, "metadata"), "annotations")?.Children.Keys.OfType<YamlScalarNode>().Any(a => a.Value?.Contains("ssl-redirect") == true || a.Value?.Contains("cert-manager") == true) ?? false))
                findings.Add(new Finding("cleartext-transmission", "D31", $"Ingress {Str(Map(d.Root, "metadata"), "name")} has no tls: section: the service is served over plain HTTP at the edge", file, L(spec ?? d.Root), null, 2));
        }
        else if (kind is "Service")
        {
            if (Str(Map(d.Root, "spec"), "type") is "NodePort" or "LoadBalancer" && Regex.IsMatch(ctx.Repo.Text(file), @"\b(5432|3306|6379|27017|9200|1433)\b"))
                findings.Add(new Finding("cleartext-transmission", "D31", "a datastore port is exposed outside the cluster", file, L(d.Root), null, 1));
        }
    }

    // ---------------- docker-compose ----------------
    private static void Compose(ScanContext ctx, List<Finding> findings, Doc d)
    {
        var services = Map(d.Root, "services"); if (services is null) return;
        foreach (var (k, v) in services.Children)
        {
            if (v is not YamlMappingNode svc) continue; var name = (k as YamlScalarNode)?.Value ?? "service";
            if (Bool(svc, "privileged", false)) findings.Add(new Finding("privileged-container", "D31", $"compose service {name} is privileged", d.File, L(svc), null, 3));
            if (Str(svc, "network_mode") == "host" || Str(svc, "pid") == "host") findings.Add(new Finding("host-namespace-sharing", "D31", $"compose service {name} shares the host network or PID namespace", d.File, L(svc), null, 2));
            foreach (var vol in Seq(svc, "volumes")?.OfType<YamlScalarNode>() ?? Enumerable.Empty<YamlScalarNode>())
                if (vol.Value?.Contains("docker.sock") == true) findings.Add(new Finding("host-path-mount", "D31", $"compose service {name} mounts the Docker socket", d.File, L(vol), null, 2));
            var image = Str(svc, "image");
            if (image is not null && (image.EndsWith(":latest") || !image.Contains(':')) && Str(svc, "build") is null && Map(svc, "build") is null) findings.Add(new Finding("mutable-image-reference", "D31", $"compose service {name} uses {image} without an immutable tag", d.File, L(svc), null, 0.5));
            var caps = Seq(svc, "cap_add")?.OfType<YamlScalarNode>().Select(x => x.Value).ToList() ?? new();
            if (caps.Any(c => c is "SYS_ADMIN" or "NET_ADMIN" or "ALL")) findings.Add(new Finding("container-excess-capabilities", "D31", $"compose service {name} adds {string.Join(",", caps)}", d.File, L(svc), null, 2));
        }
    }

    // ---------------- Terraform (text-level) ----------------
    private static void Terraform(ScanContext ctx, List<Finding> findings, string file)
    {
        var lines = ctx.Repo.Text(file).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var l = lines[i];
            if (Regex.IsMatch(l, @"cidr_blocks\s*=\s*\[\s*""0\.0\.0\.0/0""") && string.Join("\n", lines.Skip(Math.Max(0, i - 8)).Take(10)).Contains("ingress")) findings.Add(new Finding("cleartext-transmission", "D31", "an ingress rule open to 0.0.0.0/0", file, i + 1, i + 1, 1));
            if (Regex.IsMatch(l, @"acl\s*=\s*""public-read")) findings.Add(new Finding("container-excessive-privilege", "D31", "a publicly readable bucket ACL", file, i + 1, i + 1, 2));
            if (Regex.IsMatch(l, @"(?i)(password|secret|token)\s*=\s*""[^""$]{8,}""")) findings.Add(new Finding("hardcoded-credential", "D31", "a credential literal in Terraform", file, i + 1, i + 1, 2));
            if (Regex.IsMatch(l, @"encrypted\s*=\s*false|storage_encrypted\s*=\s*false|enable_https_traffic_only\s*=\s*false|min_tls_version\s*=\s*""TLS1_[01]""")) findings.Add(new Finding("cleartext-transmission", "D31", $"encryption or TLS disabled: {l.Trim()}", file, i + 1, i + 1, 2));
        }
    }

    // ---------------- D36 supply chain + CI workflow security ----------------
    private static void SupplyChain(ScanContext ctx)
    {
        var repo = ctx.Repo; var workflows = Readiness.WorkflowFiles(repo).ToList();
        var findings = new List<Finding>();
        int actions = 0, pinned = 0;
        foreach (var wf in workflows)
        {
            var text = repo.Text(wf); var lines = text.Split('\n');
            YamlMappingNode? root = null; try { var ys = new YamlStream(); ys.Load(new StringReader(text)); root = ys.Documents.FirstOrDefault()?.RootNode as YamlMappingNode; } catch (Exception) { }
            var topPermissions = root is not null ? (root.Children.TryGetValue(new YamlScalarNode("permissions"), out var tp) ? tp : null) : null;
            var topWrite = topPermissions is YamlScalarNode tps && tps.Value is "write-all" || topPermissions is YamlMappingNode tpm && tpm.Children.Values.OfType<YamlScalarNode>().Any(v => v.Value == "write");
            var triggers = root is not null && root.Children.TryGetValue(new YamlScalarNode("on"), out var on) ? on.ToString() : text;
            var prTarget = Regex.IsMatch(text, @"pull_request_target|workflow_run");
            var inRun = false; var runIndent = -1;
            for (var i = 0; i < lines.Length; i++)
            {
                var l = lines[i];
                var indent = l.Length - l.TrimStart().Length;
                if (Regex.IsMatch(l, @"^\s*run:\s*[|>]")) { inRun = true; runIndent = indent; }
                else if (inRun && l.Trim().Length > 0 && indent <= runIndent) inRun = false;
                if (inRun && Regex.IsMatch(l, @"\$\{\{\s*secrets\.\w+\s*\}\}") && !Regex.IsMatch(l, @"^\s*run:"))
                    findings.Add(new Finding("secret-in-process-arguments", "D36", $"a secret is expanded into the shell script: {Trunc(l.Trim(), 70)} (it lands in the generated script and in the argv of the process it is passed to; bind it through env:)", wf, i + 1, i + 1, 1));
                var uses = Regex.Match(l, @"^\s*-?\s*uses:\s*([\w.-]+/[\w./-]+)@(\S+)");
                if (uses.Success)
                {
                    actions++;
                    var isSha = Regex.IsMatch(uses.Groups[2].Value, @"^[0-9a-f]{40}$");
                    var firstParty = uses.Groups[1].Value.StartsWith("actions/") || uses.Groups[1].Value.StartsWith("github/");
                    if (isSha) pinned++;
                    else if (!uses.Groups[1].Value.StartsWith("./")) findings.Add(new Finding("unpinned-ci-action", "D36", $"{uses.Groups[1].Value}@{uses.Groups[2].Value}: a mutable tag, not a commit — the action's author (or whoever takes the tag) can change what runs", wf, i + 1, i + 1, firstParty ? 0.5 : 1));
                }
                // expression injection: untrusted event data interpolated into a run script
                if (Regex.IsMatch(l, @"\$\{\{\s*github\.event\.(issue|pull_request|comment|review|discussion|commits?|head_commit|inputs|pages)\.[^}]*(title|body|message|ref|label|name|email|login|branch|default_branch|head_ref)[^}]*\}\}|\$\{\{\s*github\.head_ref\s*\}\}|\$\{\{\s*github\.event\.inputs\.\w+\s*\}\}"))
                {
                    var inRunStep = Enumerable.Range(Math.Max(0, i - 6), 7).Any(j => Regex.IsMatch(lines[j], @"^\s*run:\s*[|>]?")) || Regex.IsMatch(l, @"^\s*run:");
                    if (inRunStep) findings.Add(new Finding("ci-workflow-injection", "D36", $"untrusted event data expanded into a shell step: {Trunc(l.Trim(), 70)}", wf, i + 1, i + 1, 2));
                }
                if (Regex.IsMatch(l, @"^\s*run:.*\$\{\{\s*secrets\.\w+\s*\}\}") || Regex.IsMatch(l, @"^\s*run:.*\$\{\{\s*secrets\.") || Regex.IsMatch(l, @"^\s*-\s*(\S+\s+)?\$\{\{\s*secrets\.\w+\s*\}\}") )
                    findings.Add(new Finding("secret-in-process-arguments", "D36", $"a secret is expanded into a command line: {Trunc(l.Trim(), 70)} (it reaches the script file and the argv of every process it starts; pass it through env:)", wf, i + 1, i + 1, 1));
                var scopeOk = Regex.Match(l, @"^\s*(contents|packages|id-token|actions|pull-requests|issues|deployments|security-events|statuses|checks):\s*write").Groups[1].Value switch
                {
                    "security-events" => Regex.IsMatch(text, @"(?i)codeql|sarif|upload-sarif|security"),
                    "contents" => Regex.IsMatch(text, @"(?i)release|tag|publish|pages|commit|push|dependabot|changelog"),
                    "packages" => Regex.IsMatch(text, @"(?i)docker|ghcr|nuget|publish|push"),
                    "id-token" => Regex.IsMatch(text, @"(?i)attest|sign|cosign|oidc|azure/login|aws-actions|google-github-actions|deploy"),
                    "pull-requests" or "issues" => Regex.IsMatch(text, @"(?i)comment|label|triage|dependabot|review|pr-|preview"),
                    "deployments" or "statuses" or "checks" => Regex.IsMatch(text, @"(?i)deploy|status|check|preview"),
                    "" => true,
                    _ => false,
                };
                if (Regex.IsMatch(l, @"^\s*permissions:\s*write-all") || Regex.IsMatch(l, @"^\s*(contents|packages|id-token|actions|pull-requests|issues|deployments|security-events|statuses|checks):\s*write") && !scopeOk)
                    findings.Add(new Finding("ci-token-excessive-permissions", "D36", $"{l.Trim()}: a write permission on a job that only builds and tests", wf, i + 1, i + 1, 1));
                if (prTarget && Regex.IsMatch(l, @"ref:\s*\$\{\{\s*github\.event\.pull_request\.head\.(sha|ref)"))
                    findings.Add(new Finding("ci-secret-exposure", "D36", "pull_request_target checks out the pull request's head: untrusted code runs with the repository's secrets", wf, i + 1, i + 1, 3));
                if (Regex.IsMatch(l, @"(curl|wget)\s[^|]*\|\s*(ba)?sh\b") && !Regex.IsMatch(l, @"sha256|checksum|gpg")) findings.Add(new Finding("download-without-integrity-check", "D36", "a remote script is piped into a shell in CI", wf, i + 1, i + 1, 2));
            }
            if (root is not null && topPermissions is null && !Regex.IsMatch(text, @"(?m)^\s+permissions:")) findings.Add(new Finding("ci-token-excessive-permissions", "D36", $"{Path.GetFileName(wf)} declares no permissions: the GITHUB_TOKEN gets the repository default, which may be write", wf, 1, 1, 0.5));
        }
        foreach (var f in findings) ctx.Add(f);
        // the posture: provenance, signing, SBOM, pinned actions, locked restores
        var all = string.Join("\n", workflows.Select(repo.Text));
        // a release pipeline ships an artifact somewhere: a tag/release trigger, a registry push, a package push
        var releasePipeline = Regex.IsMatch(all, @"(?m)^\s*tags:\s*$|^\s*tags:\s*\[|^\s*release:\s*$|types:\s*\[\s*(published|created|released)") || Regex.IsMatch(all, @"(?i)dotnet\s+nuget\s+push|docker\s+push|docker/build-push-action|buildx .*--push|push:\s*true|ghcr\.io/|nuget\.org|softprops/action-gh-release|ncipollo/release-action|goreleaser|helm\s+push");
        var provenance = Regex.IsMatch(all, @"attest-build-provenance|slsa-framework|slsa-github-generator|provenance:\s*true|--provenance|in-toto|witness");
        var sbom = Regex.IsMatch(all, @"(?i)sbom|cyclonedx|spdx|syft|sbom-tool|--sbom");
        var signing = Regex.IsMatch(all, @"(?i)cosign|sigstore|notation\s+sign|dotnet\s+nuget\s+sign|gpg\s+--sign|signtool|attest");
        var checks = new (string, bool)[] { ("actions pinned to commits", actions > 0 && pinned == actions), ("build provenance / attestation", provenance), ("an SBOM published with releases", sbom), ("signed artifacts", signing), ("locked restores (packages.lock.json + --locked-mode)", repo.Projects.Where(p => p.IsProduction).All(p => p.LockFile) && Regex.IsMatch(all, @"--locked-mode|RestoreLockedMode")) };
        if (workflows.Count == 0) { ctx.Skip("D36", "no CI pipeline: no build whose provenance could be attested"); return; }
        if (!releasePipeline) foreach (var (name, ok) in checks.Take(1).Concat(checks.Skip(4))) { if (!ok) ctx.Add(new Finding("build-provenance-and-signing", "D36", $"supply chain: missing {name}", null, null, null, 1)); }
        else foreach (var (name, ok) in checks) if (!ok) ctx.Add(new Finding("build-provenance-and-signing", "D36", $"supply chain: missing {name}", null, null, null, 1));
        var applicable = releasePipeline ? checks : checks.Take(1).Concat(checks.Skip(4)).ToArray();
        var posture = Shape.FromChecklist(applicable.Count(c => c.Item2), applicable.Length);
        ctx.Measure("D36", posture * (Shape.FromFindings(findings, Math.Max(1.0, workflows.Count / 2.0), 0.6) / 10), note: $"{pinned}/{actions} actions pinned; release pipeline: {releasePipeline}; provenance {provenance}, sbom {sbom}, signing {signing}; {findings.Count} workflow finding(s)");
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    // ---------------- D40 D41 D42: reward postures for Kubernetes deployments ----------------
    private static void Rewards(ScanContext ctx, List<Doc> k8s)
    {
        var workloads = k8s.Where(d => Str(d.Root, "kind") is "Deployment" or "StatefulSet" or "DaemonSet").ToList();
        if (workloads.Count == 0) { foreach (var d in new[] { "D40", "D41", "D42" }) ctx.Skip(d, "no Kubernetes workloads: nothing to confine"); return; }
        var allText = string.Join("\n", ctx.Repo.FilesWithExtension(".yml", ".yaml").Select(ctx.Repo.Text));
        var egress = k8s.Any(d => Str(d.Root, "kind") is "NetworkPolicy" && Regex.IsMatch(ctx.Repo.Text(d.File), @"(?m)^\s*-\s*Egress|policyTypes:[\s\S]{0,60}Egress")) || k8s.Any(d => Str(d.Root, "kind") is "CiliumNetworkPolicy" or "CiliumClusterwideNetworkPolicy" && ctx.Repo.Text(d.File).Contains("egress"));
        if (!egress) { ctx.Skip("D40", "reward-only: no egress-restricting NetworkPolicy to credit"); }
        else ctx.Measure("D40", k8s.Count(d => Str(d.Root, "kind") is "NetworkPolicy" or "CiliumNetworkPolicy") >= workloads.Count ? 10 : 7, note: "egress NetworkPolicy present");
        var seccomp = workloads.Count(d => ctx.Repo.Text(d.File).Contains("seccompProfile")); var mac = workloads.Count(d => Regex.IsMatch(ctx.Repo.Text(d.File), @"apparmor|appArmorProfile|seLinuxOptions"));
        if (seccomp == 0 && mac == 0) ctx.Skip("D41", "reward-only: no seccomp or AppArmor/SELinux confinement to credit");
        else ctx.Measure("D41", Math.Round(10 * (0.6 * seccomp + 0.4 * mac) / workloads.Count, 1), note: $"{seccomp}/{workloads.Count} workloads with seccomp, {mac} with a MAC profile");
        var agent = Regex.IsMatch(allText, @"(?i)tetragon|TracingPolicy|falco|sysdig|microsoft-defender|kubearmor|neuvector|aqua-enforcer");
        var admission = Regex.IsMatch(allText, @"(?i)kind:\s*(ClusterPolicy|Policy)\b[\s\S]{0,200}kyverno|kind:\s*Constraint|ConstraintTemplate|gatekeeper|policy-controller|cosign|sigstore|ImagePolicyWebhook|ClusterImagePolicy|ValidatingAdmissionPolicy");
        var psa = Regex.Match(allText, @"pod-security\.kubernetes\.io/enforce:\s*(\w+)");
        var psaCredit = !psa.Success ? 0.0 : psa.Groups[1].Value == "restricted" ? 0.2 : 0.1;
        if (!agent && !admission && !psa.Success) ctx.Skip("D42", "reward-only: no admission policy, Pod Security Admission or runtime detection engine to credit");
        else ctx.Measure("D42", Math.Round(10 * ((agent ? 0.5 : 0) + (admission ? 0.3 : 0) + psaCredit), 1), note: $"runtime detection agent: {agent}; admission policy / signed-image gate: {admission}; Pod Security Admission: {(psa.Success ? psa.Groups[1].Value : "none")}");
    }
}
