using System.Text.RegularExpressions;

namespace Foreman.Recon.Triage;

/// <summary>
/// Pulls shell commands out of CI files (GitHub Actions, GitLab, Azure Pipelines, CircleCI, Bitbucket).
/// Line-based on purpose: the commands are a hint for the environment agent, not something we execute.
/// </summary>
public static partial class CiReader
{
    public const int MaxCommands = 200;

    public static IEnumerable<string> CiFiles(RepoFiles files) =>
        files.All.Where(f =>
            (f.StartsWith(".github/workflows/", StringComparison.Ordinal) && (f.EndsWith(".yml", StringComparison.Ordinal) || f.EndsWith(".yaml", StringComparison.Ordinal)))
            || f is ".gitlab-ci.yml" or "azure-pipelines.yml" or ".circleci/config.yml" or "bitbucket-pipelines.yml");

    public static IReadOnlyList<CiCommand> Read(RepoFiles files)
    {
        var result = new List<CiCommand>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in CiFiles(files))
        {
            foreach (var cmd in Commands(files.Read(file)))
            {
                if (seen.Add(cmd)) result.Add(new CiCommand(file, cmd));
                if (result.Count >= MaxCommands) return result;
            }
        }
        return result;
    }

    internal static IEnumerable<string> Commands(string yaml)
    {
        var lines = yaml.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var m = KeyRegex().Match(lines[i]);
            if (!m.Success) continue;

            var keyIndent = m.Groups[1].Length + m.Groups[2].Length;
            var rest = m.Groups[4].Value.Trim();

            if (rest.Length > 0 && rest[0] is not ('|' or '>'))
            {
                if (rest.StartsWith('[')) continue; // flow sequence: rare in CI, skip rather than misparse
                yield return Clean(Unquote(rest));
                continue;
            }

            // Block scalar (run: |) or a list of commands (script:\n  - cmd): take the more-indented lines below.
            for (var j = i + 1; j < lines.Length; j++)
            {
                var line = lines[j];
                if (line.Trim().Length == 0) continue;
                var indent = line.Length - line.TrimStart().Length;
                if (indent <= keyIndent && !(rest.Length == 0 && indent == keyIndent && line.TrimStart().StartsWith("- ", StringComparison.Ordinal))) break;
                var text = line.Trim();
                if (rest.Length == 0)
                {
                    if (!text.StartsWith("- ", StringComparison.Ordinal)) break;
                    text = Unquote(text[2..].Trim());
                }
                if (text.Length > 0 && !text.StartsWith('#')) yield return Clean(text);
                i = j;
            }
        }
    }

    private static string Unquote(string s) =>
        s.Length >= 2 && (s[0] == '"' && s[^1] == '"' || s[0] == '\'' && s[^1] == '\'') ? s[1..^1] : s;

    private static string Clean(string s) => s.Length > 300 ? s[..300] + "…" : s;

    [GeneratedRegex(@"^(\s*)(-\s+)?(run|script|command)\s*:\s*(.*)$")]
    private static partial Regex KeyRegex();
}
