using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Foreman.Core;
using Foreman.Recon.CodeMap;
using Foreman.Recon.Triage;

namespace Foreman.Recon.Dossier;

public sealed record Citation(string Path, int Line, string? Note = null);

public sealed record TaskLocation(string Path, IReadOnlyList<string> Symbols, string Reason, string Confidence);

/// <summary>One reader's section of the dossier, as accepted by <see cref="ReaderTools"/> after checking every reference.</summary>
public sealed record DossierSection(
    string Section,
    string Markdown,
    IReadOnlyList<Citation> Citations,
    IReadOnlyList<TaskLocation>? Locations,
    IReadOnlyList<string>? OpenQuestions,
    IReadOnlyList<string> Warnings,
    int Submissions,
    int ToolCalls);

/// <summary>
/// A dossier reader's tools: read-only access to the repository as cloned (list, read, search, the code map, symbol
/// lookup) and <c>submit</c>, which checks the section by code before accepting it: every cited file and line, every
/// <c>path:line</c> in the text and every symbol named as a task location must exist. Nothing runs; paths are
/// confined to the repository's files (no <c>.git</c>, no dependency folders); regular expressions use the
/// non-backtracking engine, so a pattern from the model can't hang the search.
/// </summary>
public sealed partial class ReaderTools : IToolbox
{
    public const int MaxToolCalls = 50;
    public const int MaxSubmissions = 3;
    public const int MaxWords = 900;
    private const int MaxReadLines = 300;
    private const int MaxListed = 400;
    private const int MaxMatches = 80;
    private const long MaxSearchBytes = 1024 * 1024;

    public static readonly string[] Sections = ["architecture", "conventions", "task", "risks"];

    private readonly Job _job;
    private readonly string _section;
    private readonly string _dir;
    private readonly string _root;
    private RepoFiles? _files;
    private HashSet<string>? _fileSet;
    private CodeIndex? _index;
    private readonly Dictionary<string, string[]> _lines = new(StringComparer.Ordinal);
    private int _submissions;

    public ReaderTools(Job job, string section)
    {
        if (!Sections.Contains(section)) throw new ArgumentException($"unknown dossier section '{section}'");
        _job = job;
        _section = section;
        _root = job.SourceDir;
        _dir = job.StageDir(Path.Combine("dossier", section));
    }

    public int ToolCalls { get; private set; }
    public DossierSection? Result { get; private set; }
    public bool Finished => Result is not null;
    public string? Outcome => Finished ? "submitted" : null;
    public string ResultFile => Path.Combine(_dir, "section.json");

    public IReadOnlyList<ToolDefinition> Definitions => _section == "task" ? TaskDefinitions : CommonDefinitions;

    public Task StartAsync(CancellationToken ct)
    {
        _files = RepoFiles.Scan(_root);
        _fileSet = _files.All.ToHashSet(StringComparer.Ordinal);
        var indexFile = Path.Combine(_job.Dir, "codemap", "index.json");
        _index = File.Exists(indexFile) ? Json.Read<CodeIndex>(File.ReadAllText(indexFile)) : null;
        if (File.Exists(ResultFile)) File.Delete(ResultFile);
        return Task.CompletedTask;
    }

    public async Task<ToolOutput> CallAsync(string name, IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct)
    {
        await Task.CompletedTask;
        if (Finished) return new ToolOutput("already submitted; stop now", true);
        if (++ToolCalls > MaxToolCalls)
            return Logged(name, input, new ToolOutput($"tool budget of {MaxToolCalls} calls is used up: submit your section now", true));
        if (_files is null) await StartAsync(ct);

        string? Str(string key) => input.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        int? Int(string key) => input.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
        bool Bool(string key) => input.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.True;

        ToolOutput output;
        try
        {
            output = name switch
            {
                "list_files" => ListFiles(Str("path"), Str("glob")),
                "read_file" when Str("path") is { } p => ReadFile(p, Int("start_line"), Int("end_line")),
                "search" when Str("pattern") is { } q => Search(q, Str("path"), Str("glob"), Bool("regex"), Bool("ignore_case")),
                "code_map" => CodeMapFor(Str("query"), Int("max_tokens")),
                "find_symbol" when Str("name") is { } n => FindSymbol(n),
                "submit" => Submit(input),
                "read_file" or "search" or "find_symbol" => new ToolOutput($"invalid input for {name}: a required field is missing", true),
                _ => new ToolOutput($"unknown tool {name}", true),
            };
        }
        catch (RegexParseException e)
        {
            output = new ToolOutput($"invalid regular expression: {e.Message}", true);
        }
        return Logged(name, input, output);
    }

    // ---------------------------------------------------------------- read tools

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
        for (var i = from; i <= to; i++) sb.Append(i.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(5)).Append('│').Append(Trim(lines[i - 1], 400)).Append('\n');
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
        if (_index is null) return new ToolOutput("no code map for this job (the codemap stage has not run)", true);
        var budget = Math.Clamp(maxTokens ?? 2500, 500, 6000);
        var focus = string.IsNullOrWhiteSpace(query) ? TaskFocus.Empty : TaskFocusFinder.Find(query, _index, FocusMode.Lexical, p => string.Join('\n', Lines(p) ?? []));
        var map = RepoMap.Build(_index, focus, budget);
        return new ToolOutput($"code map{(focus.IsEmpty ? "" : $" ranked for \"{Trim(query!, 80)}\"")}: {map.SymbolsShown} definitions in {map.FilesShown} files, ~{map.Tokens} tokens\n{map.Text}", false);
    }

    private ToolOutput FindSymbol(string name)
    {
        if (_index is null) return new ToolOutput("no code index for this job (the codemap stage has not run)", true);
        var defs = _index.Files.SelectMany(f => f.Defs.Where(d => d.Name == name).Select(d => (f.Path, d))).ToList();
        var ids = defs.Select(d => d.d.Id).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var refs = _index.Files
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

    // ---------------------------------------------------------------- submit

    private ToolOutput Submit(IReadOnlyDictionary<string, JsonElement> input)
    {
        _submissions++;
        var errors = new List<string>();
        var markdown = input.TryGetValue("markdown", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString()!.Trim() : "";
        var citations = Array<Citation>(input, "citations") ?? [];
        var locations = Array<TaskLocation>(input, "locations");
        var questions = Array<string>(input, "open_questions");

        if (markdown.Length == 0) errors.Add("markdown is empty");
        var words = WordCount().Matches(markdown).Count;
        if (words > MaxWords) errors.Add($"markdown has {words} words; the limit is {MaxWords}: keep what a developer needs, cut the rest");
        var minCitations = _section == "task" ? 1 : 3;
        if (citations.Count < minCitations) errors.Add($"give at least {minCitations} citations (path and line) for the claims in the section");
        foreach (var c in citations) if (CheckLine(c.Path, c.Line) is { } e) errors.Add($"citation {c.Path}:{c.Line}: {e}");
        foreach (Match r in InlineReference().Matches(markdown))
        {
            var path = r.Groups["path"].Value;
            if (!path.Contains('/') && !_fileSet!.Contains(path)) continue; // "Program.cs:12" is checked only if it is a real root file
            if (CheckLine(path, int.Parse(r.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture)) is { } e) errors.Add($"reference {r.Value} in the text: {e}");
        }

        if (_section == "task")
        {
            if (locations is null || locations.Count == 0) errors.Add("locations is required: the files (and symbols) a change for this task would touch, most likely first");
            else if (locations.Count > 10) errors.Add("give at most 10 locations");
            foreach (var l in locations ?? [])
            {
                var p = Clean(l.Path);
                if (!_fileSet!.Contains(p)) { errors.Add($"location {l.Path}: {NotFound(p)}"); continue; }
                if (l.Confidence is not ("high" or "medium" or "low")) errors.Add($"location {l.Path}: confidence must be high, medium or low");
                var defs = _index?.Find(p)?.Defs;
                if (defs is null || defs.Count == 0) continue;
                foreach (var s in l.Symbols ?? [])
                {
                    var bare = s.Split('.', ':').Last().Split('(')[0].Trim();
                    if (!defs.Any(d => d.Name == bare))
                        errors.Add($"location {p}: '{s}' is not defined there; it defines {string.Join(", ", defs.Select(d => d.Name).Distinct().Take(20))}");
                }
            }
        }
        if (questions is { Count: > 8 }) errors.Add("give at most 8 open questions");

        // A third attempt is kept with its problems listed, so the work is not lost; the dossier shows the warnings.
        if (errors.Count > 0 && _submissions < MaxSubmissions)
            return new ToolOutput($"NOT ACCEPTED (attempt {_submissions} of {MaxSubmissions}), fix these and submit again:\n- " + string.Join("\n- ", errors), true);

        Result = new DossierSection(_section, markdown, citations.Select(c => c with { Path = Clean(c.Path) }).ToList(),
            locations?.Select(l => l with { Path = Clean(l.Path), Symbols = l.Symbols ?? [] }).ToList(), questions, errors, _submissions, ToolCalls);
        _job.WriteJson(Path.Combine("dossier", _section), "section.json", Result);
        return new ToolOutput(errors.Count == 0 ? "ACCEPTED. You are done: stop now." : "Kept with the problems listed as warnings. Stop now.", false);
    }

    private string? CheckLine(string path, int line)
    {
        var p = Clean(path);
        if (!_fileSet!.Contains(p)) return NotFound(p);
        var lines = Lines(p);
        if (lines is null) return "file is binary or too large";
        return line < 1 || line > lines.Length ? $"line {line} is outside the file (it has {lines.Length} lines)" : null;
    }

    private static List<T>? Array<T>(IReadOnlyDictionary<string, JsonElement> input, string key)
    {
        if (!input.TryGetValue(key, out var v) || v.ValueKind != JsonValueKind.Array) return null;
        try { return v.Deserialize<List<T>>(SnakeCase); }
        catch (JsonException) { return null; }
    }

    private static readonly JsonSerializerOptions SnakeCase = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    // ---------------------------------------------------------------- helpers

    /// <summary>A repository-relative path with forward slashes and no <c>..</c>; never outside the repository.</summary>
    internal static string Clean(string path)
    {
        var p = path.Trim().Trim('`').Replace('\\', '/');
        if (p.StartsWith("./", StringComparison.Ordinal)) p = p[2..];
        p = p.TrimStart('/');
        return string.Join('/', p.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(s => s is not "." and not ".."));
    }

    private string NotFound(string p)
    {
        var name = Path.GetFileName(p);
        var similar = _files!.All.Where(f => Path.GetFileName(f).Equals(name, StringComparison.OrdinalIgnoreCase)).Take(5).ToList();
        return similar.Count == 0 ? $"no file {p} in the repository" : $"no file {p}; did you mean {string.Join(", ", similar)}?";
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
            if (System.Array.IndexOf(bytes, (byte)0) < 0)
                lines = Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n").Split('\n');
        }
        _lines[path] = lines!;
        return lines;
    }

    private static Regex Glob(string glob) =>
        new("^" + Regex.Escape(glob.Replace('\\', '/')).Replace(@"\*\*/", "(.*/)?").Replace(@"\*", "[^/]*").Replace(@"\?", "[^/]") + "$",
            RegexOptions.NonBacktracking | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

    private static string Size(long bytes) => bytes < 1024 ? $"{bytes} B" : $"{bytes / 1024.0:0.#} KB";

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private ToolOutput Logged(string tool, IReadOnlyDictionary<string, JsonElement> input, ToolOutput output)
    {
        using var span = Telemetry.Source.StartActivity($"execute_tool {tool}", ActivityKind.Internal);
        span?.SetTag("gen_ai.operation.name", "execute_tool");
        span?.SetTag("gen_ai.tool.name", tool);
        var line = JsonSerializer.Serialize(new { ts = DateTime.UtcNow, call = ToolCalls, tool, input, error = output.IsError, output = Trim(output.Text, 4000) }, Json.Line);
        File.AppendAllText(Path.Combine(_dir, "tools.jsonl"), line + "\n", new UTF8Encoding(false));
        return output;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [GeneratedRegex(@"\S+")]
    private static partial Regex WordCount();

    [GeneratedRegex(@"(?<path>[\w.\-/]+\.[A-Za-z0-9]+):(?<line>\d+)")]
    private static partial Regex InlineReference();

    // ---------------------------------------------------------------- definitions

    private static JsonElement Schema(object o) => JsonSerializer.SerializeToElement(o);

    private static readonly ToolDefinition[] ReadTools =
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

    private static readonly object CitationSchema = new
    {
        type = "array",
        description = "Evidence for the section's claims: file and line. Every one is checked.",
        items = new { type = "object", properties = new { path = new { type = "string" }, line = new { type = "integer" }, note = new { type = "string" } }, required = new[] { "path", "line" }, additionalProperties = false },
    };

    private static readonly IReadOnlyList<ToolDefinition> CommonDefinitions =
    [
        .. ReadTools,
        new("submit", $"Submit your section. Foreman checks that every cited file and line and every path:line in the text exists. At most {MaxWords} words.",
            Schema(new
            {
                type = "object",
                properties = new { markdown = new { type = "string", description = "The section in Markdown, without a top-level heading." }, citations = CitationSchema },
                required = new[] { "markdown", "citations" },
                additionalProperties = false,
            })),
    ];

    private static readonly IReadOnlyList<ToolDefinition> TaskDefinitions =
    [
        .. ReadTools,
        new("submit", $"Submit your section. Foreman checks every cited file and line, every path:line in the text, and that each location's symbols are defined in that file. At most {MaxWords} words.",
            Schema(new
            {
                type = "object",
                properties = new
                {
                    markdown = new { type = "string", description = "The section in Markdown, without a top-level heading." },
                    citations = CitationSchema,
                    locations = new
                    {
                        type = "array",
                        description = "Source files a change for this task would touch, most likely first, at most 10. Tests that cover them go in the text, not here.",
                        items = new
                        {
                            type = "object",
                            properties = new
                            {
                                path = new { type = "string" },
                                symbols = new { type = "array", items = new { type = "string" }, description = "Names defined in that file: classes, functions, methods." },
                                reason = new { type = "string" },
                                confidence = new { type = "string", @enum = new[] { "high", "medium", "low" } },
                            },
                            required = new[] { "path", "symbols", "reason", "confidence" },
                            additionalProperties = false,
                        },
                    },
                    open_questions = new { type = "array", items = new { type = "string" }, description = "What the task leaves unclear and the code can't answer, at most 8." },
                },
                required = new[] { "markdown", "citations", "locations" },
                additionalProperties = false,
            })),
    ];
}
