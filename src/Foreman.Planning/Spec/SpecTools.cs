using System.Text.Json;
using System.Text.RegularExpressions;
using Foreman.Core;
using Foreman.Planning.Clarify;
using Foreman.Planning.Intake;
using Foreman.Recon.Dossier;
using Foreman.Recon.Workspaces;

namespace Foreman.Planning.Spec;

/// <summary>What the spec must account for, read from the brief and clarify.</summary>
/// <param name="Criteria">The brief's acceptance criteria as C1, C2...; explicit ones must be covered.</param>
/// <param name="Settled">Answered questions and assumptions (Q1, A1...): each must shape a decision or a requirement.</param>
/// <param name="Known">Gaps the code answered (K1...): fine to cite, not required.</param>
public sealed record SpecContext(string TaskType, IReadOnlyList<(string Id, bool Explicit, string Text)> Criteria, IReadOnlyList<string> Settled, IReadOnlyList<string>? Known = null)
{
    public static SpecContext Of(Brief brief, IReadOnlyList<Gap>? gaps) => new(
        brief.TaskType,
        brief.Acceptance.Select((c, i) => ($"C{i + 1}", c.Explicit, c.Text)).ToList(),
        (gaps ?? []).Where(g => g.Resolution is "ask" or "assume" && g.Id is not null).Select(g => g.Id!).ToList(),
        (gaps ?? []).Where(g => g.Resolution == "repository" && g.Id is not null).Select(g => g.Id!).ToList());
}

public sealed record SpecSubmission(SpecDraft Draft, IReadOnlyList<string> Warnings, int Submissions, int ToolCalls);

/// <summary>
/// The spec writer's tools: the read-only repository tools and <c>submit_spec</c>. The schema gives the PRP's shape;
/// code checks the done contract: every requirement has a check, most of them executable; every explicit acceptance
/// criterion of the brief is covered; every answer and assumption from clarify is used; examples point at real
/// requirements; existing interface symbols and touched files exist where the spec says; a bug starts with a
/// failing reproduction. With a verified test environment the writer also has a lab (<see cref="Lab"/>); a bug or
/// feature PRP must then survive a sketch of its own change against the whole suite (<see cref="CheckLab"/>).
/// </summary>
public sealed partial class SpecTools : IToolbox
{
    public const int MaxToolCalls = 80;
    public const int MaxSubmissions = 3;

    private readonly Job _job;
    private readonly string _dir;
    private readonly SpecContext _context;
    private readonly RepoReader _repo;
    private readonly Lab _lab;
    private bool _started;
    private int _submissions;

    public SpecTools(Job job)
    {
        _job = job;
        _dir = job.StageDir("spec");
        _repo = new RepoReader(job);
        _context = SpecStage.Context(job);
        _lab = new Lab(job, _dir, Lab.SpecSuffix);
    }

    public int ToolCalls { get; private set; }
    public SpecSubmission? Result { get; private set; }
    public bool Finished => Result is not null;
    public string? Outcome => Finished ? "submitted" : null;
    public static string ResultFile(Job job) => Path.Combine(job.Dir, "spec", "submission.json");

    public IReadOnlyList<ToolDefinition> Definitions =>
        _lab.Unavailable is null ? [.. RepoReader.ReadTools, Lab.Run, Lab.RunTests, SubmitSpec] : [.. RepoReader.ReadTools, SubmitSpec];

    public Task StartAsync(CancellationToken ct)
    {
        _repo.Start();
        _started = true;
        if (File.Exists(ResultFile(_job))) File.Delete(ResultFile(_job));
        return Task.CompletedTask;
    }

    public async Task<ToolOutput> CallAsync(string name, IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct)
    {
        ToolOutput output;
        if (Finished) output = new ToolOutput("already submitted; stop now", true);
        else if (++ToolCalls > MaxToolCalls) output = new ToolOutput($"tool budget of {MaxToolCalls} calls is used up: call submit_spec now", true);
        else
        {
            if (!_started) await StartAsync(ct);
            try
            {
                output = name switch
                {
                    "submit_spec" => Submit(input),
                    "lab_run" => await _lab.RunAsync(ToolInput.String(input, "command") ?? "", Lab.Int(input, "timeout_seconds"), ct),
                    "lab_run_tests" => await _lab.RunTestsAsync(ct),
                    _ => _repo.Call(name, input),
                };
            }
            catch (InvalidOperationException e)
            {
                output = new ToolOutput(e.Message, true);
            }
        }
        ToolInput.Log(_dir, ToolCalls, name, input, output);
        return output;
    }

    private ToolOutput Submit(IReadOnlyDictionary<string, JsonElement> input)
    {
        _submissions++;
        SpecDraft? draft;
        try { draft = JsonSerializer.SerializeToElement(input).Deserialize<SpecDraft>(ToolInput.SnakeCase); }
        catch (JsonException e) { return Rejected([$"the input doesn't match the schema: {e.Message}"]); }
        if (draft is null) return Rejected(["empty input"]);
        draft = Normalize(draft);

        var errors = Check(draft, _context, _repo);
        if (_lab.Unavailable is null) errors.AddRange(CheckLab(draft, _context.TaskType, _lab.LastRun, _lab.LastSketchFiles));
        if (errors.Count > 0 && _submissions < MaxSubmissions) return Rejected(errors);
        Result = new SpecSubmission(draft, errors, _submissions, ToolCalls);
        _job.WriteJson("spec", "submission.json", Result);
        return new ToolOutput(errors.Count == 0 ? "ACCEPTED. You are done: stop now." : "Kept with the problems listed as warnings. Stop now.", false);
    }

    private ToolOutput Rejected(IReadOnlyList<string> errors) =>
        new($"NOT ACCEPTED (attempt {_submissions} of {MaxSubmissions}), fix these and call submit_spec again:\n- " + string.Join("\n- ", errors), true);

    internal static SpecDraft Normalize(SpecDraft d) => d with
    {
        Why = d.Why ?? [],
        Requirements = (d.Requirements ?? []).Select(r => r with { Sources = r.Sources ?? [], Verify = r.Verify ?? [] }).ToList(),
        Interface = (d.Interface ?? []).Select(i => i with { Path = RepoReader.Clean(i.Path ?? "") }).ToList(),
        Examples = (d.Examples ?? []).Select(e => e with { Requirements = e.Requirements ?? [] }).ToList(),
        OutOfScope = d.OutOfScope ?? [],
        Decisions = d.Decisions ?? [],
        TouchPoints = (d.TouchPoints ?? []).Select(t => t with { Path = RepoReader.Clean(t.Path ?? ""), Symbols = t.Symbols ?? [] }).ToList(),
        Gotchas = d.Gotchas ?? [],
        Tasks = d.Tasks ?? [],
        AntiPatterns = d.AntiPatterns ?? [],
        AllowedEdits = d.AllowedEdits ?? [],
    };

    /// <summary>Everything wrong with a draft. <paramref name="repo"/> null skips the checks against the code.</summary>
    internal static List<string> Check(SpecDraft d, SpecContext context, RepoReader? repo)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(d.Title)) errors.Add("title is empty");
        if (string.IsNullOrWhiteSpace(d.Goal)) errors.Add("goal is empty: state what is true afterwards, as observable behaviour");
        if (d.Why.Count == 0) errors.Add("why: at least one reason, so the implementer can make small decisions sensibly");

        // Requirements and the done contract.
        if (d.Requirements.Count == 0) errors.Add("requirements: at least one");
        if (d.Requirements.Count > 15) errors.Add("requirements: at most 15; this is one change, merge or cut");
        var ids = d.Requirements.Select(r => r.Id).ToList();
        foreach (var dup in ids.GroupBy(x => x).Where(g => g.Count() > 1)) errors.Add($"requirement id {dup.Key} is used twice");
        var known = context.Criteria.Select(c => c.Id).Concat(context.Settled).Concat(context.Known ?? []).Append("task").ToHashSet(StringComparer.Ordinal);
        foreach (var r in d.Requirements)
        {
            if (!RequirementId().IsMatch(r.Id ?? "")) errors.Add($"requirement id '{r.Id}' must be R1, R2...");
            if (string.IsNullOrWhiteSpace(r.Statement)) errors.Add($"{r.Id}: statement is empty");
            if (r.Sources.Count == 0) errors.Add($"{r.Id}: sources: where it comes from (C1, Q1, A1, K1 or task)");
            foreach (var s in r.Sources.Where(s => !known.Contains(s)))
                errors.Add($"{r.Id}: source {s} doesn't exist; the criteria are {string.Join(", ", context.Criteria.Select(c => c.Id))}{(context.Settled.Count > 0 ? $" and the settled gaps {string.Join(", ", context.Settled)}" : "")}");
            if (r.Verify.Count == 0) errors.Add($"{r.Id}: verify: how it will be checked (acceptance_test, existing_tests, command or review)");
            foreach (var v in r.Verify)
            {
                if (!Prp.CheckKinds.Contains(v.Kind)) errors.Add($"{r.Id}: check kind '{v.Kind}' must be one of {string.Join(", ", Prp.CheckKinds)}");
                if (string.IsNullOrWhiteSpace(v.How)) errors.Add($"{r.Id}: say how the {v.Kind} check works");
            }
        }
        var reviewOnly = d.Requirements.Count(r => r.Verify.Count > 0 && r.Verify.All(v => v.Kind == "review"));
        if (d.Requirements.Count > 0 && reviewOnly * 2 > d.Requirements.Count)
            errors.Add($"{reviewOnly} of {d.Requirements.Count} requirements are checked only by review; most must be checked by a test or a command");
        var behavioural = context.TaskType is "bug" or "feature" or "refactor" or "ui";
        if (behavioural && !d.Requirements.Any(r => r.Verify.Any(v => v.Kind is "acceptance_test" or "existing_tests")))
            errors.Add($"a {context.TaskType} needs at least one requirement checked by an acceptance test or by existing tests");

        // Nothing the client said or agreed is dropped.
        var covered = d.Requirements.SelectMany(r => r.Sources).Concat(d.Decisions.Select(x => x.Source)).ToHashSet(StringComparer.Ordinal);
        foreach (var c in context.Criteria.Where(c => c.Explicit && !covered.Contains(c.Id)))
            errors.Add($"the client's acceptance criterion {c.Id} (\"{RepoReader.Trim(c.Text, 100)}\") is not covered by any requirement");
        foreach (var s in context.Settled.Where(s => !covered.Contains(s)))
            errors.Add($"{s} from clarify (an answer or an assumption) shapes nothing: use it as a decision's source or a requirement's source");

        // Examples.
        foreach (var e in d.Examples)
        {
            if (string.IsNullOrWhiteSpace(e.Given) || string.IsNullOrWhiteSpace(e.When) || string.IsNullOrWhiteSpace(e.Then)) errors.Add($"example {e.Id}: given, when and then are all required");
            if (e.Requirements.Count == 0) errors.Add($"example {e.Id}: name the requirements it shows");
            foreach (var x in e.Requirements.Where(x => !ids.Contains(x))) errors.Add($"example {e.Id}: requirement {x} doesn't exist");
        }
        foreach (var r in d.Requirements.Where(r => r.Verify.Any(v => v.Kind == "acceptance_test") && !d.Examples.Any(e => e.Requirements.Contains(r.Id))))
            errors.Add($"{r.Id} is checked by an acceptance test but has no example: the hidden tests are written from the examples' rules");

        if (d.OutOfScope.Count == 0) errors.Add("out_of_scope: at least one thing a helpful implementer might do and must not");
        foreach (var x in d.Decisions.Where(x => string.IsNullOrWhiteSpace(x.Text) || string.IsNullOrWhiteSpace(x.Reason))) errors.Add("every decision needs text and a reason");

        // Tasks.
        if (d.Tasks.Count == 0) errors.Add("tasks: at least one, in order");
        foreach (var t in d.Tasks)
        {
            if (!Prp.TaskKinds.Contains(t.Kind)) errors.Add($"task {t.Id}: kind must be one of {string.Join(", ", Prp.TaskKinds)}");
            if (string.IsNullOrWhiteSpace(t.DoneWhen)) errors.Add($"task {t.Id}: done_when is required: a check, not a feeling");
        }
        if (context.TaskType == "bug" && d.Tasks.FirstOrDefault()?.Kind != "reproduce")
            errors.Add("a bug starts with a reproduce task: a test that fails on the current code for the reported reason");
        if (d.TouchPoints.Count == 0) errors.Add("touch_points: the files the change goes into");
        if (d.AntiPatterns.Count == 0) errors.Add("anti_patterns: at least one plausible but wrong way to do this");

        if (repo is null) return errors;

        // Against the code.
        foreach (var t in d.TouchPoints)
        {
            if (t.New)
            {
                if (repo.Exists(t.Path)) errors.Add($"touch point {t.Path} is marked new but already exists");
                continue;
            }
            if (!repo.Exists(t.Path)) { errors.Add($"touch point {t.Path}: {repo.NotFound(t.Path)} (mark it new if the change creates it)"); continue; }
            foreach (var s in t.Symbols.Where(s => !Defines(repo, t.Path, s)))
                errors.Add($"touch point {t.Path}: '{s}' is not defined there{Defined(repo, t.Path)}");
        }
        foreach (var i in d.Interface)
        {
            if (!Prp.InterfaceStatuses.Contains(i.Status)) { errors.Add($"interface {i.Name}: status must be existing, changed or new"); continue; }
            if (i.Status == "new") continue;
            if (!repo.Exists(i.Path)) { errors.Add($"interface {i.Name}: {repo.NotFound(i.Path)}"); continue; }
            if (!Defines(repo, i.Path, i.Name)) errors.Add($"interface {i.Name} is marked {i.Status} but is not defined in {i.Path}{Defined(repo, i.Path)}");
        }
        foreach (var text in d.Gotchas.Concat(d.Decisions.Select(x => x.Reason)).Concat(d.TouchPoints.Select(t => t.Change)))
            errors.AddRange(repo.CheckInlineReferences(text));
        return errors;
    }

    /// <summary>True if the index has no symbols for the file (nothing to check against) or one with this name.</summary>
    private static bool Defines(RepoReader repo, string path, string symbol)
    {
        var defs = repo.Index?.Find(RepoReader.Clean(path))?.Defs;
        if (defs is null || defs.Count == 0) return true;
        var bare = symbol.Split('.', ':').Last().Split('(')[0].Trim();
        return defs.Any(d => d.Name == bare);
    }

    private static string Defined(RepoReader repo, string path)
    {
        var defs = repo.Index?.Find(RepoReader.Clean(path))?.Defs;
        return defs is { Count: > 0 } ? $"; it defines {string.Join(", ", defs.Select(d => d.Name).Distinct().Take(20))}" : "";
    }

    /// <summary>
    /// What the lab showed, for a bug or a feature: the PRP was sketched and run against the whole suite, every test the
    /// sketch breaks is one the PRP allows editing (so it doesn't forbid an edit the change must make), and the sketch's
    /// new tests pass (so its expected values can hold). Empty when the task changes no behaviour.
    /// </summary>
    internal static List<string> CheckLab(SpecDraft d, string taskType, TestRun? run, IReadOnlyList<string> sketchFiles)
    {
        if (taskType is not ("bug" or "feature")) return [];
        if (run is null || sketchFiles.Count == 0)
            return ["the lab has no sketch: before submitting a bug or feature PRP, sketch the change it specifies in your lab with lab_run " +
                    "(the reproduce test from the first task and a rough version of the fix) and run lab_run_tests, so the PRP's claims about " +
                    "the code and the existing tests are checked by running them"];
        if (run.Outcomes.Count == 0)
            return ["the last lab_run_tests produced no test report: the sketch doesn't compile or collect; fix it and run lab_run_tests again"];
        var errors = new List<string>();
        var broken = run.Delta.Regressions.Where(r => !Accounts(d.AllowedEdits, r.Id)).ToList();
        if (broken.Count > 0)
            errors.Add($"your sketch breaks {broken.Count} test(s) that passed before: {string.Join(", ", broken.Take(10).Select(r => r.Id))}{(broken.Count > 10 ? $" and {broken.Count - 10} more" : "")}. " +
                       "Each one either pins behaviour this PRP changes (put its test file in allowed_edits, with the assertion that changes and why, " +
                       "and don't also require it to keep passing unchanged), or shows that the sketch or the design is wrong (fix it and run lab_run_tests again)");
        if (run.Delta.NewFailing.Count > 0)
            errors.Add($"new tests fail with your sketch: {string.Join(", ", run.Delta.NewFailing.Take(10))}. If the sketch does what the PRP says, " +
                       "a requirement or example they check can't hold as written: fix the PRP, or finish the sketch and run lab_run_tests again");
        if (run.Delta.NewPassing.Count + run.Delta.NewFailing.Count == 0)
            errors.Add("the sketch adds no test: write the test the PRP's first task asks for in the lab, see it fail without the fix and pass with it, and run lab_run_tests again");
        return errors;
    }

    /// <summary>
    /// True if an allowed edit covers a test: the test's id names the file (a path, a dotted module or a class named
    /// after the file), or the edit's reason names the test.
    /// </summary>
    internal static bool Accounts(IReadOnlyList<AllowedEdit> edits, string testId)
    {
        var leaf = Leaf(testId);
        foreach (var e in edits)
        {
            var path = RepoReader.Clean(e.Path ?? "").Trim('/');
            if (path.Length == 0) continue;
            var noExt = Path.ChangeExtension(path, null) ?? path;
            if (testId.Contains(path, StringComparison.Ordinal) || testId.Contains(noExt.Replace('/', '.'), StringComparison.Ordinal)) return true;
            var stem = Path.GetFileName(noExt);
            if (stem.Length >= 4 && testId.Split('.', ':', '/', ' ').Contains(stem, StringComparer.Ordinal)) return true;
            if (leaf.Length >= 4 && (e.Reason ?? "").Contains(leaf, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>The test's own name: the last part of its id, without parameters.</summary>
    internal static string Leaf(string testId)
    {
        var s = testId.Split('(')[0].Split('[')[0];
        var cut = new[] { s.LastIndexOf("::", StringComparison.Ordinal) + 2, s.LastIndexOf(" > ", StringComparison.Ordinal) + 3, s.LastIndexOf('.') + 1 }.Max();
        return s[Math.Max(0, cut)..].Trim();
    }

    public ValueTask DisposeAsync() => _lab.DisposeAsync();

    [GeneratedRegex(@"^R\d{1,2}$")]
    private static partial Regex RequirementId();

    // ---------------------------------------------------------------- definition

    private static object Strings(string description) => new { type = "array", items = new { type = "string" }, description };

    private static readonly ToolDefinition SubmitSpec = new("submit_spec",
        "Submit the PRP. Foreman checks the done contract (every requirement has checks, most executable), coverage of the brief's criteria and clarify's answers, the examples, and every file and symbol against the code; problems come back to fix.",
        RepoReader.Schema(new
        {
            type = "object",
            properties = new
            {
                title = new { type = "string" },
                goal = new { type = "string", description = "What is true after the change, as observable behaviour. One paragraph." },
                why = Strings("Two to four reasons: the client's need, the engineering reason. They let the implementer make small decisions sensibly."),
                requirements = new
                {
                    type = "array",
                    description = "The normative part, at most 15. Each one checkable from outside the code.",
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            id = new { type = "string", description = "R1, R2..." },
                            statement = new { type = "string", description = "One rule, EARS style where it fits: 'When <trigger>, <the system> shall <response>.' Exact values, inclusive or exclusive bounds, what null means." },
                            sources = Strings("C1.. (the brief's criteria), Q1.. or A1.. (clarify's answers and assumptions), K1.. (what the code showed), or task."),
                            verify = new
                            {
                                type = "array",
                                items = new
                                {
                                    type = "object",
                                    properties = new
                                    {
                                        kind = new { type = "string", @enum = Prp.CheckKinds },
                                        how = new { type = "string", description = "acceptance_test: what the hidden test does through the public interface. existing_tests: which tests. command: the command and what passing means. review: what the reviewer looks for." },
                                    },
                                    required = new[] { "kind", "how" },
                                    additionalProperties = false,
                                },
                            },
                        },
                        required = new[] { "id", "statement", "sources", "verify" },
                        additionalProperties = false,
                    },
                },
                @interface = new
                {
                    type = "array",
                    description = "Every name the acceptance tests may call: functions, classes, methods, endpoints, CLI flags. Existing ones are checked against the code.",
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            name = new { type = "string" },
                            path = new { type = "string", description = "The file that defines it (or will)." },
                            status = new { type = "string", @enum = Prp.InterfaceStatuses },
                            signature = new { type = "string" },
                            note = new { type = "string" },
                        },
                        required = new[] { "name", "path", "status", "signature" },
                        additionalProperties = false,
                    },
                },
                examples = new
                {
                    type = "array",
                    description = "Given / When / Then with real data: the main path and each edge the requirements name.",
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            id = new { type = "string", description = "E1, E2..." },
                            given = new { type = "string" },
                            when = new { type = "string" },
                            then = new { type = "string" },
                            requirements = Strings("The requirement ids it shows."),
                        },
                        required = new[] { "id", "given", "when", "then", "requirements" },
                        additionalProperties = false,
                    },
                },
                out_of_scope = Strings("What a helpful implementer might do on their own and must not."),
                decisions = new
                {
                    type = "array",
                    description = "Fixed technical decisions, including every answer and assumption from clarify (source Q1, A1...).",
                    items = new
                    {
                        type = "object",
                        properties = new { text = new { type = "string" }, reason = new { type = "string" }, source = new { type = "string", description = "Q1, A1, K1, C1 or spec." } },
                        required = new[] { "text", "reason", "source" },
                        additionalProperties = false,
                    },
                },
                touch_points = new
                {
                    type = "array",
                    description = "Files the change goes into, most important first. Existing files and symbols are checked; mark files the change creates as new.",
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            path = new { type = "string" },
                            symbols = Strings("Names defined in that file that change."),
                            change = new { type = "string" },
                            @new = new { type = "boolean" },
                        },
                        required = new[] { "path", "symbols", "change" },
                        additionalProperties = false,
                    },
                },
                gotchas = Strings("Traps specific to this code: the trap, why, what to do instead. Cite path:line."),
                tasks = new
                {
                    type = "array",
                    description = "In order, one commit each. A bug starts with a reproduce task.",
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            id = new { type = "string", description = "T1, T2..." },
                            kind = new { type = "string", @enum = Prp.TaskKinds },
                            task = new { type = "string" },
                            done_when = new { type = "string", description = "A check: a test passes, a command exits 0." },
                        },
                        required = new[] { "id", "kind", "task", "done_when" },
                        additionalProperties = false,
                    },
                },
                anti_patterns = Strings("Plausible but wrong implementations of this change."),
                allowed_edits = new
                {
                    type = "array",
                    description = "Protected paths (existing tests, CI, lockfiles, test config) the change genuinely needs to edit, with the reason. Usually empty.",
                    items = new
                    {
                        type = "object",
                        properties = new { path = new { type = "string" }, reason = new { type = "string" } },
                        required = new[] { "path", "reason" },
                        additionalProperties = false,
                    },
                },
            },
            required = new[] { "title", "goal", "why", "requirements", "interface", "examples", "out_of_scope", "decisions", "touch_points", "gotchas", "tasks", "anti_patterns", "allowed_edits" },
            additionalProperties = false,
        }));
}
