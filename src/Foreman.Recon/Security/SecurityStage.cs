using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Foreman.Core;
using Foreman.Recon.CodeMap;
using Foreman.Recon.Triage;
using Foreman.Sandbox;

namespace Foreman.Recon.Security;

/// <summary>A secret gitleaks found. The value itself is never stored: the scanner redacts it.</summary>
/// <param name="InCurrentFiles">Still in the checked-out files; otherwise only in the cloned history.</param>
public sealed record SecretFinding(string Rule, string Description, string File, int Line, string? Commit, string? Date, bool InCurrentFiles, bool InTestPath);

/// <param name="Ids">The advisory group's ids (GHSA, PYSEC, CVE...).</param>
/// <param name="Severity">The group's highest CVSS score, when the advisory has one.</param>
/// <param name="DevGroup">The lockfile itself marks the package as a development dependency (osv-scanner reports it where the format says so).</param>
public sealed record Vulnerability(string Ecosystem, string Package, string Version, string Source, IReadOnlyList<string> Ids, double? Severity, IReadOnlyList<string> Fixed, string? Summary, bool DevGroup = false)
{
    /// <summary>
    /// A real version. A range from a manifest (<c>[8.14.0, )</c> in a .csproj) is compared as if it were one, so the
    /// match is unconfirmed: the version actually installed is decided at restore.
    /// </summary>
    public bool Pinned => Version.Length > 0 && char.IsAsciiLetterOrDigit(Version[0]) && !Version.Any(c => c is ',' or ' ' or '*' or '^' or '~' or '>' or '<' or '[' or '(');

    /// <summary>A development dependency, or from a lockfile under tests, benchmarks or examples: not shipped with the product.</summary>
    public bool DevOnly => DevGroup || Foreman.Recon.CodeMap.CodePaths.IsTest(Source) || Source.Split('/').Any(p => p is "examples" or "example" or "samples" or "docs");
}

public sealed record SastFinding(string Rule, string Severity, string File, int Line, string Message, IReadOnlyList<string> Cwe, bool InTestPath);

public sealed record ToolRun(string Tool, int ExitCode, double Seconds, string? Error);

public sealed record SecurityReport(
    string Commit,
    IReadOnlyList<SecretFinding> Secrets,
    IReadOnlyList<Vulnerability> Vulnerabilities,
    IReadOnlyList<SastFinding> Sast,
    IReadOnlyList<string> Ecosystems,
    IReadOnlyDictionary<string, string> DatabaseDownloaded,
    IReadOnlyList<string> RemovedIgnoreFiles,
    int HistoryCommits,
    IReadOnlyDictionary<string, string> Versions,
    IReadOnlyList<ToolRun> Tools,
    IReadOnlyList<string> Warnings,
    int SastFilesScanned,
    IReadOnlyList<string> DroppedRules,
    double Seconds)
{
    public int SecretsInFiles => Secrets.Count(s => s.InCurrentFiles);
}

/// <summary>
/// Security scans before anything is changed, no model: gitleaks over the files and the cloned history, osv-scanner
/// over the lockfiles against an offline copy of the OSV database, and Opengrep with GitLab's sast-rules. Everything
/// runs in the sandbox as the unprivileged user with no network. The vulnerability database is refreshed by a
/// separate container that has network and no repository, so the client's dependency list never leaves the machine.
/// Findings are the "before" picture: Verify compares a change against them, and the risks reader and the rules
/// file see them.
/// </summary>
public sealed class SecurityStage(SandboxLimits? limits = null, Action<string>? log = null) : IStage
{
    public const string ImageRepository = "foreman-tools/security";

    /// <summary>Shared by all jobs; holds only public advisory data.</summary>
    public const string OsvVolume = "foreman-osv-db";

    public string Name => "security";

    public static Task<string> EnsureImageAsync(CancellationToken ct = default) =>
        ToolImages.EnsureAsync(ImageRepository, ["security/Dockerfile", "security/scan.sh", "security/update-osv.sh"], ct);

    public async Task RunAsync(Job job, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        await DockerSandbox.EnsureDockerAsync();
        var image = await EnsureImageAsync(ct);
        var commit = job.ReadJson<FetchResult>("fetch", "fetch.json").Commit;
        var files = RepoFiles.Scan(job.SourceDir);

        var ecosystems = Ecosystems(files.All);
        var downloaded = new Dictionary<string, string>();
        if (ecosystems.Count > 0)
        {
            log?.Invoke($"  vulnerability data: {string.Join(", ", ecosystems)}");
            var update = await Proc.RunAsync("docker",
                ["run", "--rm", "--network", "bridge", "--cap-drop", "ALL", "--security-opt", "no-new-privileges",
                 "-v", $"{OsvVolume}:/osv", image, "/opt/security/update-osv.sh", .. ecosystems],
                timeout: TimeSpan.FromMinutes(20), ct: ct);
            if (!update.Ok) throw new InvalidOperationException($"updating the OSV database failed: {update.Tail(5)}");
            var dates = await Proc.RunAsync("docker",
                ["run", "--rm", "--network", "none", "-v", $"{OsvVolume}:/osv:ro", image, "sh", "-c",
                 "for d in /osv/osv-scalibr/*/; do echo \"$(basename \"$d\") $(cat \"$d/downloaded\" 2>/dev/null)\"; done"], ct: ct);
            foreach (var line in dates.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = line.Split(' ', 2);
                if (parts.Length == 2 && ecosystems.Contains(parts[0])) downloaded[parts[0]] = parts[1];
            }
        }

        var sandbox = new DockerSandbox(job.Id, limits, "security");
        var output = Path.Combine(job.StageDir(Name), "raw");
        Dirs.Delete(output);
        try
        {
            // The whole clone, history included: gitleaks reads the commits.
            await sandbox.PopulateAsync(job.SourceDir, image, []);
            var r = await sandbox.RunAsync(new SandboxRun(image, "/opt/security/scan.sh", Network.None, TimeSpan.FromMinutes(30),
                Volumes: [$"{OsvVolume}:/osv:ro"]), output, ct);
            if (!r.Ok) throw new InvalidOperationException($"the security scan failed: {r.Tail(10)}");
        }
        finally
        {
            await sandbox.RemoveAsync();
        }

        var report = Read(output, commit, ecosystems, downloaded, Math.Round(clock.Elapsed.TotalSeconds, 1));
        job.WriteJson(Name, "security.json", report);
        job.Event(Name, "scanned", new
        {
            secretsInFiles = report.SecretsInFiles,
            secretsInHistory = report.Secrets.Count - report.SecretsInFiles,
            vulnerabilities = report.Vulnerabilities.Count,
            sast = report.Sast.Count,
            report.RemovedIgnoreFiles,
        });
        foreach (var t in report.Tools.Where(t => t.Error is not null))
            log?.Invoke($"  {t.Tool}: {t.Error}");
    }

    /// <summary>OSV ecosystems whose manifests or lockfiles the repository has.</summary>
    internal static List<string> Ecosystems(IEnumerable<string> paths)
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            if (path.Split('/').Any(p => p is "node_modules" or "vendor")) continue;
            var name = Path.GetFileName(path).ToLowerInvariant();
            var eco = name switch
            {
                "package-lock.json" or "npm-shrinkwrap.json" or "yarn.lock" or "pnpm-lock.yaml" or "bun.lock" or "package.json" => "npm",
                "poetry.lock" or "uv.lock" or "pipfile.lock" or "pdm.lock" or "pylock.toml" or "pyproject.toml" or "setup.py" => "PyPI",
                "go.mod" => "Go",
                "cargo.lock" => "crates.io",
                "packages.lock.json" or "packages.config" or "directory.packages.props" => "NuGet",
                "pom.xml" or "gradle.lockfile" or "build.gradle" or "build.gradle.kts" => "Maven",
                "gemfile.lock" => "RubyGems",
                "composer.lock" => "Packagist",
                "pubspec.lock" => "Pub",
                "mix.lock" => "Hex",
                _ when name.StartsWith("requirements", StringComparison.Ordinal) && name.EndsWith(".txt", StringComparison.Ordinal) => "PyPI",
                _ when name.EndsWith(".csproj", StringComparison.Ordinal) || name.EndsWith(".fsproj", StringComparison.Ordinal) => "NuGet",
                _ => null,
            };
            if (eco is not null) found.Add(eco);
        }
        return found.ToList();
    }

    internal static SecurityReport Read(string dir, string commit, IReadOnlyList<string> ecosystems, IReadOnlyDictionary<string, string> downloaded, double seconds)
    {
        string? Text(string file) => File.Exists(Path.Combine(dir, file)) ? File.ReadAllText(Path.Combine(dir, file)) : null;

        var tools = new List<ToolRun>();
        foreach (var (tool, okCodes) in new[] { ("gitleaks-files", new[] { 0 }), ("gitleaks-history", new[] { 0 }), ("osv", new[] { 0, 1, 128 }), ("opengrep", new[] { 0, 1, 2 }) })
        {
            var run = Text($"{tool}.run")?.Trim().Split(' ');
            var code = run is { Length: 2 } && int.TryParse(run[0], CultureInfo.InvariantCulture, out var c) ? c : -1;
            var secs = run is { Length: 2 } && double.TryParse(run[1], CultureInfo.InvariantCulture, out var s) ? Math.Round(s, 1) : 0;
            // osv-scanner exits 1 when it finds vulnerabilities and 128 when there is nothing to scan; opengrep exits 1 on
            // findings and 2 when some rule or file failed while the rest ran (those go to the warnings).
            var error = code < 0 ? "did not run" : okCodes.Contains(code) ? null : $"exit {code}: {Tail(Text($"{tool}.log"))}";
            tools.Add(new ToolRun(tool, code, secs, error));
        }

        var inFiles = Gitleaks(Text("gitleaks-files.json"));
        var inHistory = Gitleaks(Text("gitleaks-history.json"));
        var current = inFiles.Select(f => (f.File, f.Rule)).ToHashSet();
        var secrets = inFiles.Select(f => f with { InCurrentFiles = true })
            .Concat(inHistory.Where(h => !current.Contains((h.File, h.Rule))).Select(h => h with { InCurrentFiles = false }))
            .OrderByDescending(s => s.InCurrentFiles).ThenBy(s => s.File, StringComparer.Ordinal).ThenBy(s => s.Line).ToList();

        var versions = Text("versions.json") is { } v ? Json.Read<Dictionary<string, string>>(v) ?? [] : [];
        var removed = Lines(Text("removed-ignore-files.txt"));
        var history = int.TryParse(Text("history-commits.txt")?.Trim(), CultureInfo.InvariantCulture, out var h) ? h : 0;

        var (warnings, scanned) = OpengrepMeta(Text("opengrep.json"));
        if (tools.First(t => t.Tool == "opengrep") is { ExitCode: >= 0 } og && Text("opengrep.json") is null)
            tools[tools.IndexOf(og)] = og with { Error = $"exit {og.ExitCode} without a report: {Tail(Text("opengrep.log"))}" };
        return new SecurityReport(commit, secrets, Osv(Text("osv.json")), Opengrep(Text("opengrep.json")), ecosystems, downloaded, removed, history, versions, tools,
            warnings, scanned, Lines(Text("dropped-rules.txt")), seconds);
    }

    internal static List<SecretFinding> Gitleaks(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
        // Only what locates a finding is kept. Match, Secret, Line, Author and Email are dropped: the first three
        // can carry the secret if redaction misses a case, the last two are people's personal data.
        return doc.RootElement.EnumerateArray().Select(e =>
        {
            var file = Clean(Str(e, "File") ?? "");
            var commit = Str(e, "Commit");
            return new SecretFinding(Str(e, "RuleID") ?? "unknown", Str(e, "Description") ?? "", file, Int(e, "StartLine"),
                string.IsNullOrEmpty(commit) ? null : commit, Str(e, "Date") is { Length: > 0 } d ? d : null, false, CodePaths.IsTest(file));
        }).DistinctBy(s => (s.File, s.Rule, s.Line, s.Commit)).ToList();
    }

    internal static List<Vulnerability> Osv(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        using var doc = JsonDocument.Parse(json);
        var result = new List<Vulnerability>();
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return result;
        foreach (var source in results.EnumerateArray())
        {
            var sourcePath = Clean(source.TryGetProperty("source", out var src) ? Str(src, "path") ?? "" : "");
            if (!source.TryGetProperty("packages", out var packages)) continue;
            foreach (var p in packages.EnumerateArray())
            {
                var pkg = p.GetProperty("package");
                var name = Str(pkg, "name") ?? "";
                var vulns = p.TryGetProperty("vulnerabilities", out var vs) ? vs.EnumerateArray().ToList() : [];
                if (!p.TryGetProperty("groups", out var groups)) continue;
                foreach (var g in groups.EnumerateArray())
                {
                    var ids = g.TryGetProperty("ids", out var idList) ? idList.EnumerateArray().Select(i => i.GetString()!).ToList() : [];
                    var aliases = g.TryGetProperty("aliases", out var al) ? al.EnumerateArray().Select(i => i.GetString()!).ToList() : [];
                    var members = vulns.Where(x => ids.Contains(Str(x, "id") ?? "")).ToList();
                    double? severity = Str(g, "max_severity") is { Length: > 0 } ms && double.TryParse(ms, CultureInfo.InvariantCulture, out var sv) ? sv : null;
                    var fixedVersions = members
                        .SelectMany(m => m.TryGetProperty("affected", out var aff) ? aff.EnumerateArray() : Enumerable.Empty<JsonElement>())
                        .Where(a => a.TryGetProperty("package", out var ap) && Str(ap, "name") == name)
                        .SelectMany(a => a.TryGetProperty("ranges", out var rs) ? rs.EnumerateArray() : Enumerable.Empty<JsonElement>())
                        .SelectMany(r => r.TryGetProperty("events", out var ev) ? ev.EnumerateArray() : Enumerable.Empty<JsonElement>())
                        .Select(e => Str(e, "fixed")).OfType<string>().Distinct().ToList();
                    var summary = members.Select(m => Str(m, "summary")).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
                    result.Add(new Vulnerability(Str(pkg, "ecosystem") ?? "", name, Str(pkg, "version") ?? "", sourcePath,
                        ids.Concat(aliases).Distinct().ToList(), severity, fixedVersions, summary,
                        p.TryGetProperty("dependency_groups", out var dg) && dg.ValueKind == JsonValueKind.Array && dg.EnumerateArray().Any(x => x.GetString() == "dev")));
                }
            }
        }
        return result.OrderByDescending(v => v.Severity ?? 0).ThenBy(v => v.Package, StringComparer.Ordinal).ToList();
    }

    internal static List<SastFinding> Opengrep(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out var results)) return [];
        return results.EnumerateArray().Select(r =>
        {
            var extra = r.GetProperty("extra");
            var cwe = extra.TryGetProperty("metadata", out var md) && md.TryGetProperty("cwe", out var c)
                ? c.ValueKind == JsonValueKind.Array ? c.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : [c.GetString() ?? ""]
                : [];
            var file = Clean(Str(r, "path") ?? "");
            var rule = Str(r, "check_id") ?? "";
            // Rule ids arrive prefixed with the folder they were loaded from; keep the rule's own id.
            rule = rule.Contains("rules.", StringComparison.Ordinal) ? rule[(rule.LastIndexOf("rules.", StringComparison.Ordinal) + 6)..] : rule;
            return new SastFinding(rule, Str(extra, "severity") ?? "", file, r.GetProperty("start").GetProperty("line").GetInt32(),
                Str(extra, "message")?.Trim() ?? "", cwe.Where(x => x.Length > 0).ToList(), CodePaths.IsTest(file));
        })
        .OrderBy(f => SeverityOrder(f.Severity)).ThenBy(f => f.InTestPath).ThenBy(f => f.File, StringComparer.Ordinal).ThenBy(f => f.Line)
        .ToList();
    }

    /// <summary>Opengrep's own errors (a rule that didn't parse, a file it timed out on) and how many files it scanned.</summary>
    internal static (List<string> Warnings, int Scanned) OpengrepMeta(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return ([], 0);
        using var doc = JsonDocument.Parse(json);
        var scanned = doc.RootElement.TryGetProperty("paths", out var paths) && paths.TryGetProperty("scanned", out var sc) ? sc.GetArrayLength() : 0;
        var warnings = doc.RootElement.TryGetProperty("errors", out var errors)
            ? errors.EnumerateArray()
                .Select(e => "opengrep: " + (Str(e, "message") ?? "error").Split('\n')[0].Trim())
                .GroupBy(m => m).Select(g => g.Count() == 1 ? g.Key : $"{g.Key} ({g.Count()} times)").ToList()
            : [];
        return (warnings, scanned);
    }

    public static int SeverityOrder(string severity) => severity.ToUpperInvariant() switch
    {
        "ERROR" or "CRITICAL" or "HIGH" => 0,
        "WARNING" or "MEDIUM" => 1,
        _ => 2,
    };

    /// <summary>A path relative to the repository: the scanners report <c>/work/repo/x</c>, <c>./x</c> or <c>x</c>.</summary>
    internal static string Clean(string path)
    {
        var p = path.Replace('\\', '/');
        foreach (var prefix in new[] { DockerSandbox.RepoPath + "/", "./" })
            if (p.StartsWith(prefix, StringComparison.Ordinal)) p = p[prefix.Length..];
        return p;
    }

    private static List<string> Lines(string? text) =>
        (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;

    private static string Tail(string? log) =>
        string.Join(" | ", (log ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(3));
}
