using System.Text;
using System.Text.RegularExpressions;
using Foreman.Recon.CodeMap;

namespace Foreman.Implement;

/// <summary>A deliberately broken version of a fix, as a whole patch to apply instead of the fix.</summary>
/// <param name="Kind"><c>partial</c> (one hunk of the fix left out) or <c>operator</c> (one added line changed).</param>
public sealed record Mutant(string Id, string Kind, string Description, string Patch);

/// <summary>
/// Broken versions of a reference fix, made by code from the diff's text, so the acceptance tests can be tested: a
/// test suite that passes on all of them doesn't pin the behaviour. Two kinds, both plausible wrong fixes rather than
/// syntax noise: a partial fix (one hunk left out, when the fix has several) and a classic mutation operator on one
/// added line (a comparison or boolean operator flipped, an integer moved by one, a boolean literal negated). Test
/// files, comments and blank lines are never mutated. Each mutant is a whole patch that applies where the fix applies.
/// </summary>
public static partial class Mutants
{
    private sealed record FileBlock(string Path, string Header, List<List<string>> Hunks, bool New);

    public static IReadOnlyList<Mutant> Of(string fix, int max = 6)
    {
        var files = Split(fix);
        var code = files.Where(f => !CodePaths.IsTest(f.Path) && IsSource(f.Path)).ToList();
        var mutants = new List<Mutant>();

        // Partial fixes: every hunk of an existing file left out in turn, if the fix has more than one hunk.
        var hunks = code.Where(f => !f.New).SelectMany(f => f.Hunks.Select((h, i) => (File: f, Index: i))).ToList();
        if (code.Sum(f => f.Hunks.Count) > 1)
            foreach (var (file, index) in hunks.Take(Math.Max(1, max / 2)))
                mutants.Add(new Mutant($"M{mutants.Count + 1}", "partial",
                    $"the fix without its change to {file.Path} at {HunkPlace(file.Hunks[index])}",
                    Join(files.Select(f => f == file ? f with { Hunks = f.Hunks.Where((_, i) => i != index).ToList() } : f))));

        // Operators on added code lines, spread across the fix.
        var candidates = new List<(FileBlock File, int Hunk, int Line, string Mutated, string What)>();
        foreach (var f in code)
            for (var h = 0; h < f.Hunks.Count; h++)
                for (var l = 0; l < f.Hunks[h].Count; l++)
                {
                    var line = f.Hunks[h][l];
                    if (!line.StartsWith('+') || !IsCode(line[1..])) continue;
                    if (Mutate(line[1..]) is { } m) candidates.Add((f, h, l, "+" + m.Text, m.What));
                }
        var room = max - mutants.Count;
        var step = candidates.Count <= room ? 1.0 : (double)candidates.Count / room;
        for (var i = 0; i < room && (int)(i * step) < candidates.Count; i++)
        {
            var c = candidates[(int)(i * step)];
            var mutated = files.Select(f => f != c.File ? f : f with
            {
                Hunks = f.Hunks.Select((h, hi) => hi != c.Hunk ? h : h.Select((x, li) => li == c.Line ? c.Mutated : x).ToList()).ToList(),
            });
            mutants.Add(new Mutant($"M{mutants.Count + 1}", "operator", $"{c.What} in {c.File.Path}: `{c.File.Hunks[c.Hunk][c.Line][1..].Trim()}`", Join(mutated)));
        }
        return mutants;
    }

    /// <summary>One mutation of a line: the first operator that applies, or null.</summary>
    internal static (string Text, string What)? Mutate(string line)
    {
        foreach (var (pattern, replacement, what) in Operators)
        {
            var re = new Regex(pattern);
            var m = re.Match(line);
            if (!m.Success || InString(line, m.Index)) continue;
            return (line[..m.Index] + re.Replace(m.Value, replacement, 1) + line[(m.Index + m.Length)..], what);
        }
        return null;
    }

    private static readonly (string Pattern, string Replacement, string What)[] Operators =
    [
        (@" == ", " != ", "== flipped to !="),
        (@" != ", " == ", "!= flipped to =="),
        (@" <= ", " < ", "<= narrowed to <"),
        (@" >= ", " > ", ">= narrowed to >"),
        (@" < ", " <= ", "< widened to <="),
        (@" > ", " >= ", "> widened to >="),
        (@" && ", " || ", "&& flipped to ||"),
        (@" \|\| ", " && ", "|| flipped to &&"),
        (@" and ", " or ", "and flipped to or"),
        (@" or ", " and ", "or flipped to and"),
        (@"\bif not ", "if ", "a condition negated"),
        (@"\bTrue\b", "False", "True flipped to False"),
        (@"\bFalse\b", "True", "False flipped to True"),
        (@"\btrue\b", "false", "true flipped to false"),
        (@"\bfalse\b", "true", "false flipped to true"),
        (@"(?<![\w.])1(?![\w.])", "0", "1 changed to 0"),
        (@"(?<![\w.])0(?![\w.])", "1", "0 changed to 1"),
    ];

    private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".py", ".cs", ".fs", ".vb", ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".go", ".rs", ".java", ".kt", ".rb", ".php", ".swift", ".c", ".h", ".cpp", ".hpp",
    };

    /// <summary>Source code, not docs, changelogs or data: a partial fix that leaves out a changelog line breaks nothing.</summary>
    private static bool IsSource(string path) => SourceExtensions.Contains(Path.GetExtension(path));

    private static bool IsCode(string line)
    {
        var t = line.Trim();
        return t.Length > 0 && !t.StartsWith('#') && !t.StartsWith("//", StringComparison.Ordinal) && !t.StartsWith('*')
               && !t.StartsWith("/*", StringComparison.Ordinal) && !t.StartsWith("\"\"\"", StringComparison.Ordinal) && !t.StartsWith("'''", StringComparison.Ordinal)
               && !t.StartsWith("import ", StringComparison.Ordinal) && !t.StartsWith("using ", StringComparison.Ordinal) && !t.StartsWith("from ", StringComparison.Ordinal);
    }

    /// <summary>True if the position is inside a string literal (an odd number of quotes before it).</summary>
    private static bool InString(string line, int index)
    {
        var before = line[..index];
        return before.Count(c => c == '"') % 2 == 1 || before.Count(c => c == '\'') % 2 == 1 || before.Count(c => c == '`') % 2 == 1;
    }

    private static string HunkPlace(List<string> hunk) => HunkHeader().Match(hunk[0]) is { Success: true } m ? $"line {m.Groups[1].Value}" : "a hunk";

    private static List<FileBlock> Split(string diff)
    {
        var files = new List<FileBlock>();
        // Split on LF only: a CRLF file keeps its CRs, or the mutant wouldn't apply.
        var lines = diff.Split('\n');
        StringBuilder? header = null;
        List<List<string>>? hunks = null;
        string? path = null;
        var isNew = false;
        void Flush()
        {
            if (header is not null && path is not null) files.Add(new FileBlock(path, header.ToString(), hunks!, isNew));
            header = null;
        }
        foreach (var line in lines)
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                Flush();
                header = new StringBuilder(line).Append('\n');
                hunks = [];
                path = line["diff --git a/".Length..].Split(" b/")[0];
                isNew = false;
                continue;
            }
            if (header is null) continue;
            if (line.StartsWith("@@", StringComparison.Ordinal)) { hunks!.Add([line]); continue; }
            if (hunks!.Count > 0) { hunks[^1].Add(line); continue; }
            if (line.StartsWith("new file mode", StringComparison.Ordinal)) isNew = true;
            header.Append(line).Append('\n');
        }
        Flush();
        // A trailing empty line from the final newline belongs to no hunk.
        foreach (var f in files) foreach (var h in f.Hunks) while (h.Count > 1 && h[^1].Length == 0) h.RemoveAt(h.Count - 1);
        return files;
    }

    private static string Join(IEnumerable<FileBlock> files)
    {
        var sb = new StringBuilder();
        foreach (var f in files.Where(f => f.Hunks.Count > 0 || f.New))
        {
            sb.Append(f.Header);
            foreach (var h in f.Hunks) foreach (var l in h) sb.Append(l).Append('\n');
        }
        return sb.ToString();
    }

    [GeneratedRegex(@"^@@ -\d+(?:,\d+)? \+(\d+)")]
    private static partial Regex HunkHeader();
}
