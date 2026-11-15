using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Foreman.Core;
using Foreman.Recon.CodeMap;
using Foreman.Recon.Triage;

namespace Foreman.Recon.Dossier;

/// <summary>
/// Read-only access to a job's repository for agents: list, read, search, the code map and symbol lookup, plus the
/// checks submissions need (does this file and line exist). Nothing runs; paths are confined to the repository's
/// files (no <c>.git</c>, no dependency folders); regular expressions use the non-backtracking engine, so a pattern
/// from the model can't hang the search. Shared by every toolbox that reads code: the dossier readers, clarify.
/// </summary>
public sealed class RepoReader(Job job)
{
    public const int MaxReadLines = 300;
    private const int MaxListed = 400;
    private const int MaxMatches = 80;
    private const long MaxSearchBytes = 1024 * 1024;

    private RepoFiles? _files;
    private HashSet<string>? _fileSet;
    private readonly Dictionary<string, string[]> _lines = new(StringComparer.Ordinal);

    public CodeIndex? Index { get; private set; }

    public IReadOnlyCollection<string> Files => _fileSet ?? throw new InvalidOperationException("RepoReader.Start first");

    public void Start()
    {
        _files = RepoFiles.Scan(job.SourceDir);
        _fileSet = _files.All.ToHashSet(StringComparer.Ordinal);
        var indexFile = Path.Combine(job.Dir, "codemap", "index.json");
        Index = File.Exists(indexFile) ? Json.Read<CodeIndex>(File.ReadAllText(indexFile)) : null;
    }

    public bool IsReadTool(string name) => ReadTools.Any(t => t.Name == name);

    /// <summary>Runs one of <see cref="ReadTools"/>.</summary>
    public ToolOutput Call(string name, IReadOnlyDictionary<string, JsonElement> input)
    {
        if (_files is null) Start();
        string? Str(string key) => input.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        int? Int(string key) => input.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
        bool Bool(string key) => input.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.True;
        try
        {
            return name switch
            {
                "list_files" => ListFiles(Str("path"), Str("glob")),
                "read_file" when Str("path") is { } p => ReadFile(p, Int("start_line"), Int("end_line")),
                "search" when Str("pattern") is { } q => Search(q, Str("path"), Str("glob"), Bool("regex"), Bool("ignore_case")),
                "code_map" => CodeMapFor(Str("query"), Int("max_tokens")),
                "find_symbol" when Str("name") is { } n => FindSymbol(n),
                "read_file" or "search" or "find_symbol" => new ToolOutput($"invalid input for {name}: a required field is missing", true),
                _ => new ToolOutput($"unknown tool {name}", true),
            };
        }
        catch (RegexParseException e)
        {
            return new ToolOutput($"invalid regular expression: {e.Message}", true);
        }
    }

    // ---------------------------------------------------------------- tools

    private ToolOutput ListFiles(string? path, string? glob)
    {
        var prefix = path is null ? "" : Clean(path).TrimEnd('/');
        var matcher = glob is null ? null : Glob(glob);
        var hits = _files!.All
            .Where(f => prefix.Length == 0 || f.StartsWith(prefix + "/", StringComparison.Ordinal) || f == prefix)
            .Where(f => matcher is null || matcher.IsMatch(Path.GetFileName(f)) || matcher.IsMatch(f))
            .ToList();
        if (hits.Count == 0) return new ToolOutput($"no files under '{prefix}'{(glob is null ? "" : $" matching {glob}")}", false);
        var sb = new StringBuilder();
        sb.Append($"{hits.Count} files{(hits.Count > MaxListed ? $", first {MaxListed} shown: narrow with path or glob" : "")}\n");
        foreach (var f in hits.Take(MaxListed))
            sb.Append(f).Append("  ").Append(Size(new FileInfo(_files.FullPath(f)).Length)).Append('\n');
        return new ToolOutput(sb.ToString(), false);
    }

    private ToolOutput ReadFile(string path, int? start, int? end)
    {
        var p = Clean(path);
        if (!_fileSet!.Contains(p)) return new ToolOutput(NotFound(p), true);
        var lines = Lines(p);
        if (lines is null) return new ToolOutput($"{p} is binary or too large to read", true);
        var from = Math.Clamp(start ?? 1, 1, Math.Max(1, lines.Length));
        var to = Math.Clamp(end ?? from + MaxReadLines - 1, from, Math.Min(lines.Length, from + MaxReadLines - 1));
        var sb = new StringBuilder();
        sb.Append($"{p}, lines {from}-{to} of {lines.Length}\n");
        for (var i = from; i <= to; i++) sb.Append(i.ToString(CultureInfo.InvariantCulture).PadLeft(5)).Append('│').Append(Trim(lines[i - 1], 400)).Append('\n');
        if (to < lines.Length) sb.Append($"... {lines.Length - to} more lines: read again with start_line={to + 1}\n");
        return new ToolOutput(sb.ToString(), false);
    }

    private ToolOutput Search(string pattern, string? path, string? glob, bool regex, bool ignoreCase)
    {
        var options = RegexOptions.NonBacktracking | RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : 0);
        var re = new Regex(regex ? pattern : Regex.Escape(pattern), options, TimeSpan.FromSeconds(2));
        var prefix = path is null ? "" : Clean(path).TrimEnd('/');
        var matcher = glob is null ? null : Glob(glob);
        var sb = new StringBuilder();
        int matches = 0, filesWith = 0;
        foreach (var f in _files!.All)
        {
            if (prefix.Length > 0 && !f.StartsWith(prefix + "/", StringComparison.Ordinal) && f != prefix) continue;
            if (matcher is not null && !matcher.IsMatch(Path.GetFileName(f)) && !matcher.IsMatch(f)) continue;
            var lines = Lines(f);
            if (lines is null) continue;
            var found = false;
            for (var i = 0; i < lines.Length; i++)
            {
                if (!re.IsMatch(lines[i])) continue;
                found = true;
                if (++matches <= MaxMatches) sb.Append($"{f}:{i + 1}: {Trim(lines[i].Trim(), 200)}\n");
            }
            if (found) filesWith++;
        }
        if (matches == 0) return new ToolOutput("no matches", false);
        var head = $"{matches} matches in {filesWith} files{(matches > MaxMatches ? $", first {MaxMatches} shown: narrow with path or glob" : "")}\n";
        return new ToolOutput(head + sb, false);
    }

    private ToolOutput CodeMapFor(string? query, int? maxTokens)
    {
        if (Index is null) return new ToolOutput("no code map for this job (the codemap stage has not run)", true);
        var budget = Math.Clamp(maxTokens ?? 2500, 500, 6000);
        var focus = string.IsNullOrWhiteSpace(query) ? TaskFocus.Empty : TaskFocusFinder.Find(query, Index, FocusMode.Lexical, p => string.Join('\n', Lines(p) ?? []));
        var map = RepoMap.Build(Index, focus, budget);
        return new ToolOutput($"code map{(focus.IsEmpty ? "" : $" ranked for \"{Trim(query!, 80)}\"")}: {map.SymbolsShown} definitions in {map.FilesShown} files, ~{map.Tokens} tokens\n{map.Text}", false);
    }

    private ToolOutput FindSymbol(string name)
    {
        if (Index is null) return new ToolOutput("no code index for this job (the codemap stage has not run)", true);
        var defs = Index.Files.SelectMany(f => f.Defs.Where(d => d.Name == name).Select(d => (f.Path, d))).ToList();
        var ids = defs.Select(d => d.d.Id).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var refs = Index.Files
            .Select(f => (f.Path, Count: f.Refs.Where(r => r.Name == name && (r.Id is null || ids.Count == 0 || ids.Contains(r.Id))).Sum(r => r.Count)))
            .Where(r => r.Count > 0).OrderByDescending(r => r.Count).ToList();
        if (defs.Count == 0 && refs.Count == 0) return new ToolOutput($"'{name}' is neither defined nor used in the indexed files (names are case-sensitive)", false);
        var sb = new StringBuilder();
        sb.Append($"{defs.Count} definitions{(defs.Count > 30 ? ", first 30" : "")}:\n");
        foreach (var (path, d) in defs.Take(30)) sb.Append($"  {path}:{d.Line}  {d.Kind}  {d.Signature.Trim()}\n");
        sb.Append($"used in {refs.Count} files{(refs.Count > 30 ? ", top 30" : "")}:\n");
        foreach (var (path, count) in refs.Take(30)) sb.Append($"  {path} ({count})\n");
        return new ToolOutput(sb.ToString(), false);
    }

    // ---------------------------------------------------------------- checks for submissions

    public bool Exists(string path) => _fileSet!.Contains(Clean(path));

    /// <summary>Null when the file and line exist, otherwise what is wrong.</summary>
    public string? CheckLine(string path, int line)
    {
        var p = Clean(path);
        if (!_fileSet!.Contains(p)) return NotFound(p);
        var lines = Lines(p);
        if (lines is null) return "file is binary or too large";
        return line < 1 || line > lines.Length ? $"line {line} is outside the file (it has {lines.Length} lines)" : null;
    }

    /// <summary>Problems with every <c>path:line</c> written in free text; bare file names are checked only if they are real root files.</summary>
    public IEnumerable<string> CheckInlineReferences(string text)
    {
        foreach (Match r in InlineReference.Matches(text))
        {
            var path = r.Groups["path"].Value;
            if (!path.Contains('/') && !_fileSet!.Contains(path)) continue;
            if (CheckLine(path, int.Parse(r.Groups["line"].Value, CultureInfo.InvariantCulture)) is { } e) yield return $"reference {r.Value} in the text: {e}";
        }
    }

    public string NotFound(string p)
    {
        var name = Path.GetFileName(p);
        var similar = _files!.All.Where(f => Path.GetFileName(f).Equals(name, StringComparison.OrdinalIgnoreCase)).Take(5).ToList();
        return similar.Count == 0 ? $"no file {p} in the repository" : $"no file {p}; did you mean {string.Join(", ", similar)}?";
    }

    /// <summary>A repository-relative path with forward slashes and no <c>..</c>; never outside the repository.</summary>
    public static string Clean(string path)
    {
        var p = path.Trim().Trim('`').Replace('\\', '/');
        if (p.StartsWith("./", StringComparison.Ordinal)) p = p[2..];
        p = p.TrimStart('/');
        return string.Join('/', p.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(s => s is not "." and not ".."));
    }

    private string[]? Lines(string path)
    {
        if (_lines.TryGetValue(path, out var cached)) return cached;
        var full = _files!.FullPath(path);
        var info = new FileInfo(full);
        string[]? lines = null;
        if (info.Exists && info.Length <= MaxSearchBytes)
        {
            var bytes = File.ReadAllBytes(full);
            if (Array.IndexOf(bytes, (byte)0) < 0)
                lines = Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n").Split('\n');
        }
        _lines[path] = lines!;
        return lines;
    }

    private static Regex Glob(string glob) =>
        new("^" + Regex.Escape(glob.Replace('\\', '/')).Replace(@"\*\*/", "(.*/)?").Replace(@"\*", "[^/]*").Replace(@"\?", "[^/]") + "$",
            RegexOptions.NonBacktracking | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

    private static string Size(long bytes) => bytes < 1024 ? $"{bytes} B" : $"{bytes / 1024.0:0.#} KB";

    public static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static readonly Regex InlineReference = new(@"(?<path>[\w.\-/]+\.[A-Za-z0-9]+):(?<line>\d+)", RegexOptions.Compiled);

    // ---------------------------------------------------------------- definitions

    public static JsonElement Schema(object o) => JsonSerializer.SerializeToElement(o);

    public static readonly IReadOnlyList<ToolDefinition> ReadTools =
    [
        new("list_files", "List repository files (dependency and build folders are left out) with their sizes. Narrow with a folder and/or a glob such as *.cs or src/**/*.ts.",
            Schema(new { type = "object", properties = new { path = new { type = "string", description = "Folder, relative to the repository root." }, glob = new { type = "string", description = "File name or path pattern." } }, additionalProperties = false })),
        new("read_file", $"Read a file with line numbers, at most {MaxReadLines} lines per call.",
            Schema(new { type = "object", properties = new { path = new { type = "string" }, start_line = new { type = "integer", description = "1-based, default 1." }, end_line = new { type = "integer" } }, required = new[] { "path" }, additionalProperties = false })),
        new("search", $"Search file contents line by line; returns path:line: text, at most {MaxMatches} matches. Plain text unless regex is true.",
            Schema(new { type = "object", properties = new { pattern = new { type = "string" }, path = new { type = "string", description = "Only under this folder." }, glob = new { type = "string", description = "Only files matching this pattern." }, regex = new { type = "boolean" }, ignore_case = new { type = "boolean" } }, required = new[] { "pattern" }, additionalProperties = false })),
        new("code_map", "The repository map: the most connected definitions with signatures and line numbers, ranked by PageRank over references. With a query, ranked for that query instead of the whole repository.",
            Schema(new { type = "object", properties = new { query = new { type = "string", description = "Words or identifiers to rank for." }, max_tokens = new { type = "integer", description = "500 to 6000, default 2500." } }, additionalProperties = false })),
        new("find_symbol", "Where a name is defined (file, line, signature) and which files use it, from the code index (Roslyn for C#, tree-sitter for other languages). Case-sensitive.",
            Schema(new { type = "object", properties = new { name = new { type = "string" } }, required = new[] { "name" }, additionalProperties = false })),
    ];

    public static readonly object CitationSchema = new
    {
        type = "array",
        description = "Evidence: file and line. Every one is checked.",
        items = new { type = "object", properties = new { path = new { type = "string" }, line = new { type = "integer" }, note = new { type = "string" } }, required = new[] { "path", "line" }, additionalProperties = false },
    };
}
