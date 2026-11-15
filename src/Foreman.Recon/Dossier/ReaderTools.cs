using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Foreman.Core;

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
/// A dossier reader's tools: the read-only repository tools of <see cref="RepoReader"/> and <c>submit</c>, which
/// checks the section by code before accepting it: every cited file and line, every <c>path:line</c> in the text and
/// every symbol named as a task location must exist.
/// </summary>
public sealed partial class ReaderTools : IToolbox
{
    public const int MaxToolCalls = 50;
    public const int MaxSubmissions = 3;
    public const int MaxWords = 900;

    public static readonly string[] Sections = ["architecture", "conventions", "task", "risks"];

    private readonly Job _job;
    private readonly string _section;
    private readonly string _dir;
    private readonly RepoReader _repo;
    private bool _started;
    private int _submissions;

    public ReaderTools(Job job, string section)
    {
        if (!Sections.Contains(section)) throw new ArgumentException($"unknown dossier section '{section}'");
        _job = job;
        _section = section;
        _repo = new RepoReader(job);
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
        _repo.Start();
        _started = true;
        if (File.Exists(ResultFile)) File.Delete(ResultFile);
        return Task.CompletedTask;
    }

    public async Task<ToolOutput> CallAsync(string name, IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct)
    {
        if (Finished) return new ToolOutput("already submitted; stop now", true);
        if (++ToolCalls > MaxToolCalls)
            return Logged(name, input, new ToolOutput($"tool budget of {MaxToolCalls} calls is used up: submit your section now", true));
        if (!_started) await StartAsync(ct);
        return Logged(name, input, name == "submit" ? Submit(input) : _repo.Call(name, input));
    }

    /// <summary>A repository-relative path with forward slashes and no <c>..</c>; never outside the repository.</summary>
    internal static string Clean(string path) => RepoReader.Clean(path);

    // ---------------------------------------------------------------- submit

    private ToolOutput Submit(IReadOnlyDictionary<string, JsonElement> input)
    {
        _submissions++;
        var errors = new List<string>();
        var markdown = ToolInput.String(input, "markdown")?.Trim() ?? "";
        var citations = ToolInput.Array<Citation>(input, "citations") ?? [];
        var locations = ToolInput.Array<TaskLocation>(input, "locations");
        var questions = ToolInput.Array<string>(input, "open_questions");

        if (markdown.Length == 0) errors.Add("markdown is empty");
        var words = WordCount().Matches(markdown).Count;
        if (words > MaxWords) errors.Add($"markdown has {words} words; the limit is {MaxWords}: keep what a developer needs, cut the rest");
        var minCitations = _section == "task" ? 1 : 3;
        if (citations.Count < minCitations) errors.Add($"give at least {minCitations} citations (path and line) for the claims in the section");
        foreach (var c in citations) if (_repo.CheckLine(c.Path, c.Line) is { } e) errors.Add($"citation {c.Path}:{c.Line}: {e}");
        errors.AddRange(_repo.CheckInlineReferences(markdown));

        if (_section == "task")
        {
            if (locations is null || locations.Count == 0) errors.Add("locations is required: the files (and symbols) a change for this task would touch, most likely first");
            else if (locations.Count > 10) errors.Add("give at most 10 locations");
            foreach (var l in locations ?? [])
            {
                var p = Clean(l.Path);
                if (!_repo.Exists(p)) { errors.Add($"location {l.Path}: {_repo.NotFound(p)}"); continue; }
                if (l.Confidence is not ("high" or "medium" or "low")) errors.Add($"location {l.Path}: confidence must be high, medium or low");
                var defs = _repo.Index?.Find(p)?.Defs;
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

    private ToolOutput Logged(string tool, IReadOnlyDictionary<string, JsonElement> input, ToolOutput output)
    {
        ToolInput.Log(_dir, ToolCalls, tool, input, output);
        return output;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [GeneratedRegex(@"\S+")]
    private static partial Regex WordCount();

    // ---------------------------------------------------------------- definitions

    private static readonly IReadOnlyList<ToolDefinition> CommonDefinitions =
    [
        .. RepoReader.ReadTools,
        new("submit", $"Submit your section. Foreman checks that every cited file and line and every path:line in the text exists. At most {MaxWords} words.",
            RepoReader.Schema(new
            {
                type = "object",
                properties = new { markdown = new { type = "string", description = "The section in Markdown, without a top-level heading." }, citations = RepoReader.CitationSchema },
                required = new[] { "markdown", "citations" },
                additionalProperties = false,
            })),
    ];

    private static readonly IReadOnlyList<ToolDefinition> TaskDefinitions =
    [
        .. RepoReader.ReadTools,
        new("submit", $"Submit your section. Foreman checks every cited file and line, every path:line in the text, and that each location's symbols are defined in that file. At most {MaxWords} words.",
            RepoReader.Schema(new
            {
                type = "object",
                properties = new
                {
                    markdown = new { type = "string", description = "The section in Markdown, without a top-level heading." },
                    citations = RepoReader.CitationSchema,
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

/// <summary>Helpers every toolbox uses: typed reads of a tool's JSON input and the per-call log with a trace span.</summary>
public static class ToolInput
{
    public static readonly JsonSerializerOptions SnakeCase = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static List<T>? Array<T>(IReadOnlyDictionary<string, JsonElement> input, string key)
    {
        if (!input.TryGetValue(key, out var v) || v.ValueKind != JsonValueKind.Array) return null;
        try { return v.Deserialize<List<T>>(SnakeCase); }
        catch (JsonException) { return null; }
    }

    public static T? Object<T>(IReadOnlyDictionary<string, JsonElement> input, string key) where T : class
    {
        if (!input.TryGetValue(key, out var v) || v.ValueKind != JsonValueKind.Object) return null;
        try { return v.Deserialize<T>(SnakeCase); }
        catch (JsonException) { return null; }
    }

    public static string? String(IReadOnlyDictionary<string, JsonElement> input, string key) =>
        input.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Appends one line to <c>tools.jsonl</c> in <paramref name="dir"/> and records an <c>execute_tool</c> span.</summary>
    public static void Log(string dir, int call, string tool, IReadOnlyDictionary<string, JsonElement> input, ToolOutput output)
    {
        using var span = Telemetry.Source.StartActivity($"execute_tool {tool}", ActivityKind.Internal);
        span?.SetTag("gen_ai.operation.name", "execute_tool");
        span?.SetTag("gen_ai.tool.name", tool);
        var line = JsonSerializer.Serialize(new { ts = DateTime.UtcNow, call, tool, input, error = output.IsError, output = RepoReader.Trim(output.Text, 4000) }, Json.Line);
        File.AppendAllText(Path.Combine(dir, "tools.jsonl"), line + "\n", new UTF8Encoding(false));
    }
}
