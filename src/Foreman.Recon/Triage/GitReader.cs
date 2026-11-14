using System.Globalization;
using System.Text.RegularExpressions;
using Foreman.Core;

namespace Foreman.Recon.Triage;

public static partial class GitReader
{
    public static async Task<GitFacts> ReadAsync(string repo)
    {
        var head = (await Proc.RunAsync("git", ["rev-parse", "HEAD"], repo)).Stdout.Trim();
        var log = await Proc.RunAsync("git", ["log", "-n", "200", "--format=%an%x1f%aI%x1f%s"], repo);
        if (!log.Ok) return new GitFacts(head.Length > 0 ? head : null, 0, null, null, 0);

        var rows = log.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split('\x1f')).Where(p => p.Length == 3).ToList();
        if (rows.Count == 0) return new GitFacts(head, 0, null, null, 0);

        var conventional = rows.Count(r => ConventionalRegex().IsMatch(r[2]));
        DateTime? last = DateTime.TryParse(rows[0][1], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var d) ? d : null;
        return new GitFacts(head, rows.Count, Math.Round((double)conventional / rows.Count, 2), last, rows.Select(r => r[0]).Distinct().Count());
    }

    [GeneratedRegex(@"^(feat|fix|docs|style|refactor|perf|test|tests|build|ci|chore|revert)(\([^)]*\))?!?:\s")]
    private static partial Regex ConventionalRegex();
}
