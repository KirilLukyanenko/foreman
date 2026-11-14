using System.Text.RegularExpressions;

namespace Foreman.Recon.CodeMap;

/// <summary>
/// BM25 over whole files, the retrieval baseline of the SWE-bench paper: the task text is the query, each source file
/// a document, identifiers split into words (<c>parseHeader</c> → parse, header) so prose can match code.
/// </summary>
public static partial class Bm25
{
    private const double K1 = 1.2;
    private const double B = 0.75;

    public static IReadOnlyList<RankedFile> Rank(string query, IReadOnlyDictionary<string, string> documents)
    {
        var docs = documents.ToDictionary(d => d.Key, d => Terms(d.Value).GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count()));
        var lengths = docs.ToDictionary(d => d.Key, d => d.Value.Values.Sum());
        var avg = lengths.Count == 0 ? 1 : Math.Max(1, lengths.Values.Average());
        var df = new Dictionary<string, int>();
        foreach (var d in docs.Values)
            foreach (var t in d.Keys)
                df[t] = df.GetValueOrDefault(t) + 1;

        var q = Terms(query).Distinct().ToList();
        var n = docs.Count;
        return docs.Select(d =>
        {
            var score = 0.0;
            foreach (var t in q)
            {
                if (!d.Value.TryGetValue(t, out var tf)) continue;
                var idf = Math.Log(1 + (n - df[t] + 0.5) / (df[t] + 0.5));
                score += idf * tf * (K1 + 1) / (tf + K1 * (1 - B + B * lengths[d.Key] / avg));
            }
            return new RankedFile(d.Key, score);
        }).OrderByDescending(r => r.Rank).ThenBy(r => r.Path, StringComparer.Ordinal).ToList();
    }

    /// <summary>Lower-case words of identifiers, of length 2 or more; the identifier itself too when it splits.</summary>
    internal static IEnumerable<string> Terms(string text)
    {
        foreach (Match m in Identifier().Matches(text))
        {
            var words = TaskFocusFinder.SplitWords(m.Value).Where(w => w.Length >= 2).ToList();
            foreach (var w in words) yield return w;
            if (words.Count > 1) yield return m.Value.ToLowerInvariant();
        }
    }

    [GeneratedRegex(@"[A-Za-z_][A-Za-z0-9_]*")]
    private static partial Regex Identifier();
}
