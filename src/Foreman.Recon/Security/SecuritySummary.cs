using System.Text;

namespace Foreman.Recon.Security;

/// <summary>The security report in the two sizes later stages need: a section for the risks reader, a few lines for the rules file.</summary>
public static class SecuritySummary
{
    public static string ForReader(SecurityReport r)
    {
        var sb = new StringBuilder("## Security scan (before any change: gitleaks, osv-scanner on an offline OSV copy, Opengrep with GitLab's sast-rules; nothing had network)\n\n");

        var inFiles = r.Secrets.Where(s => s.InCurrentFiles).ToList();
        sb.Append($"- Secrets: {inFiles.Count} in the current files ({inFiles.Count(s => s.InTestPath)} in test paths), ");
        sb.Append($"{r.Secrets.Count - inFiles.Count} only in the history of the last {r.HistoryCommits} commits. Values are redacted.\n");
        foreach (var s in r.Secrets.OrderByDescending(s => s.InCurrentFiles).ThenBy(s => s.InTestPath).Take(10))
            sb.Append($"  - `{s.File}:{s.Line}` {s.Rule}{(s.InTestPath ? " (test path)" : "")}{(s.InCurrentFiles ? "" : $" (history only, commit {s.Commit?[..Math.Min(8, s.Commit.Length)]})")}\n");

        var packages = r.Vulnerabilities.GroupBy(v => (v.Ecosystem, v.Package, v.Version)).ToList();
        sb.Append($"- Vulnerable dependencies: {packages.Count} packages, {r.Vulnerabilities.Count} advisories; ");
        sb.Append($"{packages.Count(p => !p.First().Pinned)} matched on a version range rather than a pinned version (unconfirmed), ");
        sb.Append($"{packages.Count(p => p.First().DevOnly)} from lockfiles under tests, benchmarks or examples");
        if (r.Vulnerabilities.Count > 0)
            sb.Append(" (by lockfile: ").Append(string.Join(", ", r.Vulnerabilities.GroupBy(v => v.Source).OrderByDescending(g => g.Count()).Take(6).Select(g => $"`{g.Key}` {g.Count()}"))).Append(')');
        sb.Append($". Vulnerability data downloaded: {(r.DatabaseDownloaded.Count == 0 ? "none needed" : string.Join(", ", r.DatabaseDownloaded.Select(d => $"{d.Key} {d.Value}")))}.\n");
        foreach (var p in packages.OrderBy(g => g.First().DevOnly || !g.First().Pinned).ThenByDescending(g => g.Max(v => v.Severity ?? 0)).Take(10))
        {
            var fixes = p.SelectMany(v => v.Fixed).Distinct().ToList();
            var flags = (p.First().Pinned ? "" : ", range, unconfirmed") + (p.First().DevOnly ? ", dev only" : "");
            sb.Append($"  - {p.Key.Package} {p.Key.Version} ({p.Key.Ecosystem}, `{p.First().Source}`{flags}): {p.Count()} advisories, max CVSS {p.Max(v => v.Severity)?.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) ?? "n/a"}");
            sb.Append(fixes.Count == 0 ? ", no fixed version published" : $", fixed in {string.Join(" / ", fixes.Take(3))}").Append('\n');
        }

        var code = r.Sast.Where(f => !f.InTestPath).ToList();
        sb.Append($"- SAST over {r.SastFilesScanned} files (Opengrep skips test folders by default): {code.Count(f => SecurityStage.SeverityOrder(f.Severity) == 0)} high, ");
        sb.Append($"{code.Count(f => SecurityStage.SeverityOrder(f.Severity) == 1)} medium, {code.Count(f => SecurityStage.SeverityOrder(f.Severity) == 2)} informational in code.\n");
        foreach (var f in code.Where(f => SecurityStage.SeverityOrder(f.Severity) < 2).Take(12))
            sb.Append($"  - `{f.File}:{f.Line}` {f.Severity} {f.Rule}: {FirstSentence(f.Message)}\n");

        if (r.RemovedIgnoreFiles.Count > 0)
            sb.Append($"- The repository's own scanner allowlists were ignored for this scan: {string.Join(", ", r.RemovedIgnoreFiles)}.\n");
        foreach (var w in r.Warnings.Take(3)) sb.Append($"- Scanner warning: {w}\n");
        foreach (var t in r.Tools.Where(t => t.Error is not null)) sb.Append($"- {t.Tool} failed: {t.Error}\n");
        return sb.Append('\n').ToString();
    }

    /// <summary>Lines for the rules file, or nothing when the scan found nothing an implementer must know.</summary>
    public static IReadOnlyList<string> ForRules(SecurityReport r)
    {
        var lines = new List<string>();
        var inFiles = r.Secrets.Where(s => s.InCurrentFiles).ToList();
        if (inFiles.Count > 0)
        {
            var real = inFiles.Where(s => !s.InTestPath).ToList();
            lines.Add($"Secrets in the current files: {inFiles.Count}" +
                (real.Count > 0 ? $", {real.Count} outside tests ({string.Join(", ", real.Take(4).Select(s => $"`{s.File}:{s.Line}`"))})" : ", all in test paths") +
                ". Never print, copy or log them; don't add new ones.");
        }
        var shipped = r.Vulnerabilities.Where(v => v.Pinned && !v.DevOnly).Select(v => (v.Package, v.Version)).Distinct().Count();
        var other = r.Vulnerabilities.Select(v => (v.Package, v.Version)).Distinct().Count() - shipped;
        if (shipped + other > 0)
            lines.Add($"Dependencies with known vulnerabilities: {shipped} pinned in the main lockfiles (which usually include development tools; the risks section in `dossier.md` says which ship)" +
                (other > 0 ? $", {other} more marked dev-only or matched on an unconfirmed version range" : "") +
                ". Don't add a vulnerable version; upgrade only if the task asks or the reviewer agrees.");
        var sast = r.Sast.Count(f => !f.InTestPath && SecurityStage.SeverityOrder(f.Severity) < 2);
        if (sast > 0)
            lines.Add($"Static analysis already reports {sast} high or medium findings in code; Verify flags only new ones.");
        return lines;
    }

    private static string FirstSentence(string message)
    {
        var text = message.Replace('\n', ' ').Trim();
        var end = text.IndexOf(". ", StringComparison.Ordinal);
        if (end > 0) text = text[..(end + 1)];
        return text.Length <= 140 ? text : text[..139] + "…";
    }
}
