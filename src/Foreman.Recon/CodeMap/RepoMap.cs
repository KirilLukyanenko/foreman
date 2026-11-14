using System.Text;
using Foreman.Core;

namespace Foreman.Recon.CodeMap;

public sealed record RankedFile(string Path, double Rank);

public sealed record RankedSymbol(string Path, string Name, double Rank);

public sealed record RepoMapResult(
    string Text,
    int Tokens,
    int TokenBudget,
    IReadOnlyList<RankedFile> Files,
    IReadOnlyList<RankedSymbol> Symbols,
    int FilesShown,
    int SymbolsShown);

/// <summary>
/// Aider's repository map, rebuilt: files are nodes, a reference from one file to a name another file defines is a
/// weighted edge, PageRank (personalised with what the task points at) ranks the files, each file's rank flows along
/// its edges to the definitions it uses, and the best definitions are printed with their signatures and line
/// numbers, as many as fit the token budget. For C#, edges go to the exact symbol Roslyn resolved, not to every
/// file that happens to define the same name.
/// </summary>
public static class RepoMap
{
    private const double Damping = 0.85;

    /// <summary>Test code is read to find where behaviour is checked, not to learn the design: its definitions rank lower.</summary>
    public const double TestDefinitionWeight = 0.25;
    private static readonly HashSet<string> ContainerKinds = new(StringComparer.Ordinal)
    {
        "class", "struct", "interface", "record", "enum", "type", "module", "trait", "impl", "object", "namespace",
    };

    public static RepoMapResult Build(CodeIndex index, TaskFocus focus, int tokenBudget)
    {
        var files = index.Files.Where(f => f.Error is null).ToList();
        var paths = files.Select(f => f.Path).ToList();
        var position = paths.Select((p, i) => (p, i)).ToDictionary(x => x.p, x => x.i, StringComparer.Ordinal);

        var byName = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var byId = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var f in files)
            foreach (var d in f.Defs)
            {
                Add(byName, d.Name, f.Path);
                if (d.Id is not null) Add(byId, d.Id, f.Path);
            }

        // edges[src][(dst, name)] = weight
        var edges = paths.ToDictionary(p => p, _ => new Dictionary<(string Dst, string Name), double>(), StringComparer.Ordinal);
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in files)
        {
            foreach (var r in f.Refs)
            {
                var definers = r.Id is not null && byId.TryGetValue(r.Id, out var exact) ? exact
                    : byName.TryGetValue(r.Name, out var named) ? named : null;
                if (definers is null) continue;
                referenced.Add(r.Name);
                var weight = Multiplier(r.Name, definers.Count, focus) * Math.Sqrt(r.Count);
                foreach (var dst in definers)
                {
                    var key = (dst, r.Name);
                    edges[f.Path][key] = edges[f.Path].GetValueOrDefault(key) + weight;
                }
            }
        }
        // Names defined but never used still deserve a little rank in their own file.
        foreach (var (name, definers) in byName)
            if (!referenced.Contains(name))
                foreach (var dst in definers)
                    edges[dst][(dst, name)] = edges[dst].GetValueOrDefault((dst, name)) + 0.1;

        var personal = Personalization(paths, focus);
        var rank = PageRank(paths, position, edges, personal);

        // Each file's rank flows to the definitions it uses, in proportion to the edge weights.
        var symbolRank = new Dictionary<(string Path, string Name), double>();
        foreach (var src in paths)
        {
            var total = edges[src].Values.Sum();
            if (total <= 0) continue;
            foreach (var ((dst, name), w) in edges[src])
                symbolRank[(dst, name)] = symbolRank.GetValueOrDefault((dst, name))
                    + rank[position[src]] * w / total * (CodePaths.IsTest(dst) ? TestDefinitionWeight : 1);
        }

        var rankedSymbols = symbolRank.OrderByDescending(s => s.Value).ThenBy(s => s.Key.Path, StringComparer.Ordinal).ThenBy(s => s.Key.Name, StringComparer.Ordinal)
            .Select(s => new RankedSymbol(s.Key.Path, s.Key.Name, s.Value)).ToList();
        var rankedFiles = paths.Select(p => new RankedFile(p, rank[position[p]])).OrderByDescending(f => f.Rank).ThenBy(f => f.Path, StringComparer.Ordinal).ToList();

        var byPath = files.ToDictionary(f => f.Path, StringComparer.Ordinal);
        // The largest number of top definitions whose rendering fits the budget.
        int lo = 0, hi = rankedSymbols.Count;
        var best = Render(byPath, rankedSymbols, 0, rankedFiles);
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            var candidate = Render(byPath, rankedSymbols, mid, rankedFiles);
            if (Tokens.Estimate(candidate.Text) <= tokenBudget) { best = candidate; lo = mid; }
            else hi = mid - 1;
        }

        return new RepoMapResult(best.Text, Tokens.Estimate(best.Text), tokenBudget, rankedFiles, rankedSymbols, best.Files, best.Symbols);
    }

    /// <summary>Aider's edge multipliers: names the task mentions and long specific names count more; private and very common names less.</summary>
    internal static double Multiplier(string name, int definers, TaskFocus focus)
    {
        var mul = 1.0;
        var specific = name.Contains('_') || name.Contains('-') || (name.Any(char.IsUpper) && name.Any(char.IsLower));
        if (focus.Identifiers.Contains(name)) mul *= 10;
        if (specific && name.Length >= 8) mul *= 10;
        if (name.StartsWith('_')) mul *= 0.1;
        if (definers > 5) mul *= 0.1;
        return mul;
    }

    /// <summary>
    /// Where a random walk restarts: files the task names, files whose path parts are mentioned identifiers, and
    /// (keyword mode) files matching the task's words. Uniform when the task points at nothing.
    /// </summary>
    internal static double[] Personalization(IReadOnlyList<string> paths, TaskFocus focus)
    {
        var p = new double[paths.Count];
        if (!focus.IsEmpty)
        {
            var identifiersLower = focus.Identifiers.Select(i => i.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
            for (var i = 0; i < paths.Count; i++)
            {
                var path = paths[i];
                if (focus.Files.Contains(path)) p[i] += 1.0;
                var parts = path.Split(['/', '.'], StringSplitOptions.RemoveEmptyEntries).Select(x => x.ToLowerInvariant());
                if (parts.Any(identifiersLower.Contains)) p[i] += 1.0;
                if (focus.KeywordFiles.TryGetValue(path, out var score)) p[i] += 0.25 * score;
                if (focus.Seeds?.TryGetValue(path, out var seed) == true) p[i] += seed;
            }
        }
        var sum = p.Sum();
        if (sum <= 0) { Array.Fill(p, 1.0 / Math.Max(1, paths.Count)); return p; }
        for (var i = 0; i < p.Length; i++) p[i] /= sum;
        return p;
    }

    internal static double[] PageRank(IReadOnlyList<string> paths, IReadOnlyDictionary<string, int> position,
        IReadOnlyDictionary<string, Dictionary<(string Dst, string Name), double>> edges, double[] personal)
    {
        var n = paths.Count;
        if (n == 0) return [];
        // Collapse to weighted file → file edges.
        var outEdges = new List<(int Dst, double W)>[n];
        var outTotal = new double[n];
        for (var i = 0; i < n; i++)
        {
            outEdges[i] = edges[paths[i]].GroupBy(e => e.Key.Dst).Select(g => (position[g.Key], g.Sum(e => e.Value))).ToList();
            outTotal[i] = outEdges[i].Sum(e => e.W);
        }

        var rank = (double[])personal.Clone();
        var next = new double[n];
        for (var iteration = 0; iteration < 100; iteration++)
        {
            var dangling = 0.0;
            for (var i = 0; i < n; i++) if (outTotal[i] <= 0) dangling += rank[i];
            for (var i = 0; i < n; i++) next[i] = (1 - Damping) * personal[i] + Damping * dangling * personal[i];
            for (var i = 0; i < n; i++)
                if (outTotal[i] > 0)
                    foreach (var (dst, w) in outEdges[i])
                        next[dst] += Damping * rank[i] * w / outTotal[i];
            var delta = 0.0;
            for (var i = 0; i < n; i++) delta += Math.Abs(next[i] - rank[i]);
            (rank, next) = (next, rank);
            if (delta < 1e-9 * n) break;
        }
        return rank;
    }

    private sealed record Rendered(string Text, int Files, int Symbols);

    /// <summary>
    /// The top <paramref name="count"/> definitions grouped by file, files in rank order, each definition with its
    /// line number, plus the types that contain it so a method is never shown without its class.
    /// </summary>
    private static Rendered Render(IReadOnlyDictionary<string, FileTags> files, IReadOnlyList<RankedSymbol> ranked, int count, IReadOnlyList<RankedFile> fileOrder)
    {
        var chosen = ranked.Take(count).GroupBy(s => s.Path).ToDictionary(g => g.Key, g => g.Select(s => s.Name).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        var sb = new StringBuilder();
        var shownSymbols = 0;
        var shownFiles = 0;
        foreach (var file in fileOrder.Where(f => chosen.ContainsKey(f.Path)))
        {
            var tags = files[file.Path];
            var names = chosen[file.Path];
            var picked = tags.Defs.Where(d => names.Contains(d.Name)).ToList();
            var containers = tags.Defs.Where(c => ContainerKinds.Contains(c.Kind) && picked.Any(d => d != c && c.Line <= d.Line && d.EndLine <= c.EndLine));
            var lines = picked.Concat(containers).DistinctBy(d => (d.Line, d.Name)).OrderBy(d => d.Line).ToList();
            if (lines.Count == 0) continue;

            sb.Append(file.Path).Append('\n');
            var last = 0;
            foreach (var d in lines)
            {
                if (last > 0 && d.Line > last + 1) sb.Append("      ⋮\n");
                sb.Append(d.Line.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(5)).Append("  ").Append(d.Signature).Append('\n');
                last = d.Line;
            }
            shownFiles++;
            shownSymbols += picked.Count;
        }
        return new Rendered(sb.ToString(), shownFiles, shownSymbols);
    }

    private static void Add(Dictionary<string, HashSet<string>> map, string key, string value)
    {
        if (!map.TryGetValue(key, out var set)) map[key] = set = new HashSet<string>(StringComparer.Ordinal);
        set.Add(value);
    }
}
