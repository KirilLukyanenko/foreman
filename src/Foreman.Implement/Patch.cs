using System.Text;
using System.Text.RegularExpressions;

namespace Foreman.Implement;

public enum ChangeKind { Added, Modified, Deleted }

/// <summary>A line the change adds, with its number in the new file.</summary>
public sealed record AddedLine(int Line, string Text);

/// <summary>One file of a unified diff, as the diff guard and the reviewer need it.</summary>
public sealed record FileChange(string Path, ChangeKind Kind, bool Binary, IReadOnlyList<AddedLine> Added, IReadOnlyList<string> Removed);

/// <summary>Reads the unified diffs <c>git diff --no-renames</c> writes. No code runs: it is text.</summary>
public static partial class Patch
{
    public static IReadOnlyList<FileChange> Parse(string diff)
    {
        var files = new List<FileChange>();
        string? path = null, oldPath = null;
        var kind = ChangeKind.Modified;
        var binary = false;
        List<AddedLine> added = [];
        List<string> removed = [];
        var newLine = 0;
        var inHunk = false;

        void Flush()
        {
            var p = path ?? oldPath;
            if (p is not null) files.Add(new FileChange(p, kind, binary, added, removed));
            path = oldPath = null;
            kind = ChangeKind.Modified;
            binary = inHunk = false;
            added = [];
            removed = [];
        }

        foreach (var raw in diff.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                Flush();
                // Fallback for binary files, which have no ---/+++ lines: "diff --git a/x b/x".
                var rest = raw["diff --git ".Length..];
                var half = rest.Length / 2;
                if (rest.Length > 4 && rest.StartsWith("a/", StringComparison.Ordinal) && rest[half] == ' ' && rest[(half + 1)..].StartsWith("b/", StringComparison.Ordinal))
                    path = oldPath = Unquote(rest[(half + 3)..]);
                continue;
            }
            if (!inHunk)
            {
                if (raw.StartsWith("new file mode", StringComparison.Ordinal)) kind = ChangeKind.Added;
                else if (raw.StartsWith("deleted file mode", StringComparison.Ordinal)) kind = ChangeKind.Deleted;
                else if (raw.StartsWith("Binary files ", StringComparison.Ordinal) || raw == "GIT binary patch") binary = true;
                else if (raw.StartsWith("--- ", StringComparison.Ordinal)) oldPath = Side(raw[4..], "a/") ?? oldPath;
                else if (raw.StartsWith("+++ ", StringComparison.Ordinal))
                {
                    var p = raw[4..];
                    path = p == "/dev/null" ? null : Side(p, "b/");
                    if (path is null) path = oldPath;
                }
            }
            if (raw.StartsWith("@@", StringComparison.Ordinal) && HunkHeader().Match(raw) is { Success: true } m)
            {
                newLine = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                inHunk = true;
                continue;
            }
            if (!inHunk) continue;
            // A blank context line whose leading space was trimmed (editors and mail do that) is still context.
            if (raw.Length == 0) { newLine++; continue; }
            switch (raw[0])
            {
                case '+': added.Add(new AddedLine(newLine++, raw[1..])); break;
                case '-': removed.Add(raw[1..]); break;
                case ' ': newLine++; break;
                case '\\': break; // "\ No newline at end of file"
                default: inHunk = false; break;
            }
        }
        Flush();
        return files;
    }

    /// <summary>The paths of a change, in diff order.</summary>
    public static IReadOnlyList<string> Paths(string diff) => Parse(diff).Select(f => f.Path).ToList();

    private static string? Side(string p, string prefix)
    {
        if (p == "/dev/null") return null;
        p = Unquote(p.TrimEnd('\t'));
        return p.StartsWith(prefix, StringComparison.Ordinal) ? p[prefix.Length..] : p;
    }

    /// <summary>Git's C-style quoting of unusual paths: <c>"a/caf\303\251.txt"</c>.</summary>
    internal static string Unquote(string p)
    {
        if (p.Length < 2 || p[0] != '"' || p[^1] != '"') return p;
        var bytes = new List<byte>();
        for (var i = 1; i < p.Length - 1; i++)
        {
            var c = p[i];
            if (c != '\\' || i + 1 >= p.Length - 1) { bytes.AddRange(Encoding.UTF8.GetBytes(c.ToString())); continue; }
            var n = p[++i];
            if (n is >= '0' and <= '7' && i + 2 < p.Length - 1)
            {
                bytes.Add(Convert.ToByte(p.Substring(i, 3), 8));
                i += 2;
            }
            else bytes.Add((byte)(n switch { 'n' => '\n', 't' => '\t', 'r' => '\r', 'a' => '\a', 'b' => '\b', 'f' => '\f', 'v' => '\v', _ => n }));
        }
        return Encoding.UTF8.GetString([.. bytes]);
    }

    [GeneratedRegex(@"^@@ -\d+(?:,\d+)? \+(\d+)(?:,\d+)? @@")]
    private static partial Regex HunkHeader();
}
