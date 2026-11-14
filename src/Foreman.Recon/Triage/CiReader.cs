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

            // The lines below the key: a block scalar (run: |) is one script, kept whole because its lines depend on
            // each other; a list (script:\n  - cmd) is one command per item.
            var block = new List<string>();
            for (var j = i + 1; j < lines.Length; j++)
            {
                var line = lines[j];
                if (line.Trim().Length == 0) { if (rest.Length > 0) block.Add(""); continue; }
                var indent = line.Length - line.TrimStart().Length;
                var listItem = line.TrimStart().StartsWith("- ", StringComparison.Ordinal);
                if (indent < keyIndent || indent == keyIndent && !(rest.Length == 0 && listItem)) break;
                if (rest.Length == 0)
                {
                    if (!listItem) break;
                    var item = Unquote(line.Trim()[2..].Trim());
                    if (item.Length > 0 && !item.StartsWith('#')) yield return Clean(item);
                }
                else
                {
                    block.Add(line);
                }
                i = j;
            }
            if (rest.Length > 0)
            {
                var margin = block.Where(l => l.Trim().Length > 0).Select(l => l.Length - l.TrimStart().Length).DefaultIfEmpty(0).Min();
                var script = string.Join('\n', block.Select(l => l.Length >= margin ? l[margin..] : l.TrimStart())).Trim();
                if (script.Length > 0) yield return Clean(script);
            }
        }
    }

    private static string Unquote(string s) =>
        s.Length >= 2 && (s[0] == '"' && s[^1] == '"' || s[0] == '\'' && s[^1] == '\'') ? s[1..^1] : s;

    private static string Clean(string s) => s.Length > 1500 ? s[..1500] + "…" : s;

    [GeneratedRegex(@"^(\s*)(-\s+)?(run|script|command)\s*:\s*(.*)$")]
    private static partial Regex KeyRegex();
}
