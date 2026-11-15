using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Foreman.Recon.Triage;

namespace Foreman.Planning.Intake;

/// <summary>
/// What code can tell about a task text without a model: size, links, file paths, code, stack traces, invisible
/// characters, and phrases aimed at an AI or at whoever answers the post. The model gets these as facts, and the
/// intake gate checks its brief against them.
/// </summary>
public sealed record TaskFacts(
    int Characters,
    int Words,
    int HiddenCharacters,
    IReadOnlyList<string> Urls,
    IReadOnlyList<string> Paths,
    IReadOnlyList<string> CodeSpans,
    bool HasCodeBlock,
    bool HasStackTrace,
    IReadOnlyList<string> DirectedPhrases)
{
    public static TaskFacts Of(string text)
    {
        var urls = Patterns.Url().Matches(text).Select(m => m.Value.TrimEnd('.', ',', ';', ':', ')', ']', '>', '\'', '"')).Distinct().Take(30).ToList();
        var withoutUrls = Patterns.Url().Replace(text, " ");
        var paths = Patterns.FilePath().Matches(withoutUrls).Select(m => m.Value).Distinct(StringComparer.Ordinal).Take(30).ToList();
        var spans = Patterns.CodeSpan().Matches(text).Select(m => m.Groups[1].Value.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.Ordinal).Take(30).ToList();
        return new TaskFacts(
            text.Length,
            Patterns.Word().Matches(text).Count,
            RepoSignals.CountHidden(text),
            urls,
            paths,
            spans,
            text.Contains("```", StringComparison.Ordinal),
            Patterns.StackTrace().IsMatch(text),
            Directed(text));
    }

    /// <summary>Phrases that address an AI or the bidder, with a little context on each side.</summary>
    internal static IReadOnlyList<string> Directed(string text) => DirectedMatches(text).Select(m => m.Context).ToList();

    /// <summary>
    /// Passages with one or more matches ("If you are an AI, start your reply with..." is one passage, two matches):
    /// the matches themselves and the text around them, at most 10 passages.
    /// </summary>
    internal static IReadOnlyList<(IReadOnlyList<string> Cores, string Context)> DirectedMatches(string text)
    {
        var groups = new List<(List<string> Cores, int From, int To)>();
        foreach (Match m in Patterns.Directed().Matches(text))
        {
            if (groups.Count > 0 && m.Index <= groups[^1].To + 80)
            {
                var last = groups[^1];
                last.Cores.Add(m.Value);
                groups[^1] = (last.Cores, last.From, m.Index + m.Length);
                continue;
            }
            if (groups.Count == 10) break;
            groups.Add(([m.Value], m.Index, m.Index + m.Length));
        }
        return groups.Select(g => ((IReadOnlyList<string>)g.Cores,
            Regex.Replace(text[Math.Max(0, g.From - 40)..Math.Min(text.Length, g.To + 60)], @"\s+", " ").Trim())).ToList();
    }

    /// <summary>A delimiter the text can't contain, so the task can't close its own tag in a prompt.</summary>
    public static string Delimiter(string text)
    {
        var tag = "task_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..8];
        return text.Contains(tag, StringComparison.Ordinal) ? tag + "x" : tag;
    }
}

/// <summary>
/// Checks that a quote from the model really is in the task: compared after folding case, quotes, dashes, Markdown
/// marks and whitespace; "..." or "…" in a quote skips text, and the parts must appear in order.
/// </summary>
public static class Quotes
{
    public static bool Found(string text, string quote)
    {
        var haystack = Fold(text);
        var at = 0;
        foreach (var part in Patterns.Ellipsis().Split(quote).Select(Fold).Where(p => p.Length > 0))
        {
            var i = haystack.IndexOf(part, at, StringComparison.Ordinal);
            if (i < 0) return false;
            at = i + part.Length;
        }
        return at > 0;
    }

    internal static string Fold(string s)
    {
        var sb = new StringBuilder(s.Length);
        var space = false;
        foreach (var c in s.Normalize(NormalizationForm.FormKC))
        {
            var ch = c switch
            {
                '‘' or '’' or '‚' or '′' or '`' => '\'',
                '“' or '”' or '„' or '«' or '»' => '"',
                '‐' or '‑' or '‒' or '–' or '—' or '−' => '-',
                '*' or '_' or '#' or '>' or '|' => ' ',
                _ => char.ToLower(c, CultureInfo.InvariantCulture),
            };
            if (char.IsWhiteSpace(ch)) { space = true; continue; }
            if (space && sb.Length > 0) sb.Append(' ');
            space = false;
            sb.Append(ch);
        }
        return sb.ToString();
    }
}

internal static partial class Patterns
{
    [GeneratedRegex(@"https?://[^\s<>()\[\]""'`]+", RegexOptions.IgnoreCase)]
    public static partial Regex Url();

    [GeneratedRegex(@"(?<![\w/.\-])(?:[\w.\-]+/)*[\w\-]+(?:\.[\w\-]+)*\.(?:cs|csproj|sln|slnx|props|targets|razor|cshtml|ts|tsx|js|jsx|mjs|cjs|vue|svelte|py|ipynb|go|rs|java|kt|rb|php|json|ya?ml|toml|ini|cfg|md|sql|html?|css|scss|sh|ps1|xml|config|lock|dockerfile|env)\b", RegexOptions.IgnoreCase)]
    public static partial Regex FilePath();

    [GeneratedRegex(@"(?<!`)`([^`\n]{1,120})`(?!`)")]
    public static partial Regex CodeSpan();

    [GeneratedRegex(@"\S+")]
    public static partial Regex Word();

    [GeneratedRegex(@"(^\s+at \S+[.(])|Traceback \(most recent call last\)|File "".+"", line \d+|^\s*\w+(\.\w+)*(Exception|Error): ", RegexOptions.Multiline)]
    public static partial Regex StackTrace();

    [GeneratedRegex(@"\.\.\.|…")]
    public static partial Regex Ellipsis();

    // Prompt-injection shapes and the "if you are an AI, start your proposal with X" traps clients put in job posts.
    [GeneratedRegex(
        @"ignore\s+(all\s+|any\s+)?(the\s+)?(previous|prior|above|earlier)\s+(instructions|prompts?|messages?)" +
        @"|\b(if|when)\s+you\s+are\s+an?\s+(ai|a\.i\.|llm|language\s+model|bot|chat\s?gpt|gpt|assistant|claude|agent)\b" +
        @"|\b(system\s+prompt|developer\s+message|jailbreak)\b" +
        @"|\bas\s+an\s+ai\b" +
        @"|\b(start|begin|end)\s+your\s+(reply|response|proposal|answer|bid|application|cover\s+letter|message)\s+with\b" +
        @"|\b(include|mention|write|use)\s+the\s+(word|phrase|code\s?word|keyword)\b" +
        @"|</?(system|assistant|instructions?|admin)>" +
        @"|\byou\s+(must|should|need\s+to)\s+(now\s+)?(run|execute|curl|download|install|send|email|post|push|delete)\b",
        RegexOptions.IgnoreCase)]
    public static partial Regex Directed();
}
