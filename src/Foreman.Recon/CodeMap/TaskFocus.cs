using System.Text.RegularExpressions;

namespace Foreman.Recon.CodeMap;

public enum FocusMode
{
    /// <summary>No task: the map ranks the repository as a whole.</summary>
    None,

    /// <summary>Aider's signals only: files named in the task, and identifiers it mentions that the code defines.</summary>
    Exact,

    /// <summary>Exact, plus plain words of the task matched against path and identifier parts ("invoice" → InvoiceService.cs).</summary>
    Keywords,

    /// <summary>
    /// Exact, plus the files BM25 finds for the task text as restart points: PageRank then spreads that relevance to
    /// the files they depend on, which BM25 alone can't see.
    /// </summary>
    Lexical,
}

/// <summary>What the task text points at in the code, found without a model.</summary>
public sealed record TaskFocus(
    FocusMode Mode,
    IReadOnlySet<string> Files,
    IReadOnlySet<string> Identifiers,
    IReadOnlyDictionary<string, double> KeywordFiles,
    IReadOnlyList<string> Keywords,
    IReadOnlyDictionary<string, double>? Seeds = null)
{
    public static readonly TaskFocus Empty = new(FocusMode.None, new HashSet<string>(), new HashSet<string>(), new Dictionary<string, double>(), []);

    public bool IsEmpty => Files.Count == 0 && Identifiers.Count == 0 && KeywordFiles.Count == 0 && (Seeds?.Count ?? 0) == 0;
}

public static partial class TaskFocusFinder
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "about", "above", "after", "again", "against", "also", "although", "always", "another", "anything", "around", "because",
        "been", "before", "being", "below", "between", "both", "cannot", "could", "currently", "does", "doing", "done", "down",
        "during", "each", "either", "else", "even", "every", "example", "expected", "feature", "find", "first", "following",
        "from", "further", "have", "having", "here", "however", "into", "issue", "itself", "just", "like", "make", "many",
        "maybe", "might", "more", "most", "much", "must", "need", "needs", "never", "only", "other", "otherwise", "over", "please",
        "problem", "rather", "really", "same", "seems", "should", "since", "some", "something", "still", "such", "sure", "than",
        "that", "their", "them", "then", "there", "these", "they", "thing", "things", "this", "those", "though", "through",
        "thanks", "under", "until", "upon", "using", "very", "want", "wants", "well", "were", "what", "when", "where", "whether",
        "which", "while", "will", "with", "within", "without", "work", "works", "would", "your", "yours", "able", "actually",
        "already", "behavior", "behaviour", "believe", "better", "call", "called", "case", "change", "changes", "code", "correct",
        "correctly", "describe", "description", "different", "error", "errors", "fail", "fails", "false", "file", "files", "fine",
        "function", "given", "happen", "happens", "help", "instead", "know", "look", "looks", "method", "missing", "name", "new",
        "note", "null", "number", "object", "output", "possible", "result", "return", "returns", "right", "run", "running", "see",
        "show", "shows", "simple", "solution", "string", "support", "test", "tests", "true", "type", "update", "used", "user",
        "users", "value", "values", "version", "want", "way", "wrong", "reproduce", "reproduction", "steps", "context",
        "additional", "environment", "proposal", "alternatives", "considered", "information", "screenshots", "package",
        "library", "project", "github", "https", "http", "com", "www", "html", "true", "none",
    };

    /// <summary>Number of BM25 files used as restart points in <see cref="FocusMode.Lexical"/>.</summary>
    public const int SeedFiles = 20;

    /// <param name="readFile">File contents by path, for <see cref="FocusMode.Lexical"/>.</param>
    public static TaskFocus Find(string taskText, CodeIndex index, FocusMode mode, Func<string, string>? readFile = null)
    {
        if (mode == FocusMode.None || string.IsNullOrWhiteSpace(taskText)) return TaskFocus.Empty;

        var lowerText = taskText.ToLowerInvariant();
        var tokens = Identifier().Matches(taskText).Select(m => m.Value).ToList();
        var tokenSet = tokens.ToHashSet(StringComparer.Ordinal);

        // Files: the full path, or a file name of at least 5 characters as a word of its own.
        var files = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in index.Files)
        {
            var name = Path.GetFileName(f.Path);
            if (lowerText.Contains(f.Path.ToLowerInvariant(), StringComparison.Ordinal)
                || (name.Length >= 5 && Regex.IsMatch(lowerText, $@"(?<![\w.]){Regex.Escape(name.ToLowerInvariant())}(?![\w])")))
                files.Add(f.Path);
        }

        // Identifiers the task mentions and the code defines: exact, or written the same apart from case when the
        // token itself looks like code (has an upper-case letter after the first, an underscore or a digit).
        var defined = index.Files.SelectMany(f => f.Defs).Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
        var definedLower = defined.GroupBy(d => d.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.ToList());
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in tokenSet)
        {
            if (t.Length < 3) continue;
            if (defined.Contains(t)) identifiers.Add(t);
            else if (LooksLikeCode(t) && definedLower.TryGetValue(t.ToLowerInvariant(), out var same)) identifiers.UnionWith(same);
        }

        var keywordFiles = new Dictionary<string, double>(StringComparer.Ordinal);
        var keywords = new List<string>();
        if (mode == FocusMode.Keywords)
        {
            keywords = tokens.SelectMany(SplitWords)
                .Where(w => w.Length >= 4 && !StopWords.Contains(w) && !w.All(char.IsDigit))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (keywords.Count > 0)
            {
                var keywordSet = keywords.ToHashSet(StringComparer.Ordinal);
                foreach (var f in index.Files)
                {
                    // Path parts count fully; parts of the names a file defines count a quarter, at most two matches' worth.
                    var pathHits = PathWords(f.Path).Count(w => Matches(w, keywordSet));
                    var defHits = f.Defs.SelectMany(d => SplitWords(d.Name)).Distinct().Count(w => Matches(w, keywordSet));
                    var score = pathHits + Math.Min(defHits * 0.25, 2.0);
                    if (score > 0) keywordFiles[f.Path] = score;
                }
            }
        }

        Dictionary<string, double>? seeds = null;
        if (mode == FocusMode.Lexical && readFile is not null)
        {
            var documents = index.Files.Where(f => f.Error is null).ToDictionary(f => f.Path, f => readFile(f.Path), StringComparer.Ordinal);
            var top = Bm25.Rank(taskText, documents).Where(r => r.Rank > 0).Take(SeedFiles).ToList();
            if (top.Count > 0)
                seeds = top.ToDictionary(r => r.Path, r => r.Rank / top[0].Rank, StringComparer.Ordinal);
        }

        return new TaskFocus(mode, files, identifiers, keywordFiles, keywords, seeds);
    }

    /// <summary>A word matches a keyword when equal, or when one starts with the other and the shorter has 5+ letters ("plural" ~ "pluralize").</summary>
    internal static bool Matches(string word, IReadOnlySet<string> keywords)
    {
        if (word.Length < 4) return false;
        if (keywords.Contains(word)) return true;
        foreach (var k in keywords)
        {
            var (shorter, longer) = k.Length <= word.Length ? (k, word) : (word, k);
            if (shorter.Length >= 5 && longer.StartsWith(shorter, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    internal static IEnumerable<string> PathWords(string path) =>
        path.Split(['/', '.', '_', '-'], StringSplitOptions.RemoveEmptyEntries).SelectMany(SplitWords).Distinct();

    /// <summary>Lower-case words of an identifier: <c>parseHTTPHeader_value</c> → parse, http, header, value.</summary>
    public static IEnumerable<string> SplitWords(string identifier) =>
        Words().Matches(identifier).Select(m => m.Value.ToLowerInvariant());

    private static bool LooksLikeCode(string token) =>
        token.Skip(1).Any(char.IsUpper) || token.Contains('_') || token.Any(char.IsDigit);

    [GeneratedRegex(@"[A-Za-z_][A-Za-z0-9_]*")]
    private static partial Regex Identifier();

    [GeneratedRegex(@"[A-Z]+(?![a-z])|[A-Z]?[a-z]+|[0-9]+")]
    private static partial Regex Words();
}
