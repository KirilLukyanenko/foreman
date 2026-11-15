using System.Text;
using System.Text.Json;
using Foreman.Recon.Workspaces;
using Foreman.Core;
using Foreman.Planning.Spec;
using Foreman.Recon.CodeMap;
using Foreman.Recon.Dossier;
using Foreman.Sandbox;

namespace Foreman.Implement;

/// <param name="Status"><c>done</c>, or <c>not_done</c> with a note saying why.</param>
/// <param name="Where">The files and symbols that make it hold, and the test that shows it.</param>
public sealed record RequirementStatus(string Id, string Status, string Where, string? Note = null);

public sealed record ImplementSubmission(string Summary, string PrTitle, string PrDescription, IReadOnlyList<RequirementStatus> Requirements, string? Notes = null);

/// <param name="Status"><c>submitted</c> or <c>gave_up</c>.</param>
/// <param name="GaveUpKind"><c>spec_problem</c>, <c>test_problem</c>, <c>environment</c> or <c>cannot_do</c>.</param>
public sealed record ImplementResult(string Status, ImplementSubmission? Submission, string? GaveUpKind, string? GaveUpReason, IReadOnlyList<string> Warnings, int Submissions, int ToolCalls, int TestRuns);

/// <summary>Where a round's implementer works and what it is held to; written by the stage, read by the toolbox.</summary>
public sealed record ImplementContext(int Round, string Volume, string StartCommit);

/// <summary>
/// The implementer's whole world, all inside the sandbox: <c>bash</c> and <c>edit</c> (the str_replace editor Claude
/// models are trained on) in a copy of the set-up repository as the unprivileged user with the network off,
/// <c>run_tests</c> (the baseline's test command, compared with the baseline), <c>read_dossier</c>, and
/// <c>submit</c> or <c>give_up</c>. The model credential stays on the host with the driver; nothing in the sandbox
/// can reach the network. <c>submit</c> is checked by code before anything is accepted: the diff guard, every
/// requirement accounted for, a test in the change when the PRP needs one, and the full test run with no regression
/// and no failing new test. Problems go back to the agent; the third submission is kept with them as warnings.
/// </summary>
public sealed class ImplementTools : IToolbox
{
    public const int MaxToolCalls = 150;
    public const int MaxSubmissions = 3;
    public const int MaxCommandSeconds = 900;
    private const int HeadChars = 2_000;
    private const int TailChars = 8_000;
    private const int MaxViewChars = 24_000;

    private readonly Job _job;
    private readonly ImplementContext _context;
    private readonly TestEnvironment _env;
    private readonly Prp _prp;
    private readonly string _dir;
    private readonly SandboxLimits? _limits;
    private Workspace? _workspace;
    private int _submissions;
    private int _testRuns;

    public ImplementTools(Job job, int round, SandboxLimits? limits = null)
    {
        _job = job;
        _limits = limits;
        _context = job.ReadJson<ImplementContext>("implement", RoundDir(round) + "/context.json");
        _env = TestEnvironment.Of(job);
        _prp = job.ReadJson<Prp>("spec", "prp.json");
        _dir = job.StageDir(Path.Combine("implement", RoundDir(round)));
    }

    public static string RoundDir(int round) => $"round-{round}";
    public static string ResultFile(Job job, int round) => Path.Combine(job.Dir, "implement", RoundDir(round), "result.json");

    public int ToolCalls { get; private set; }
    public ImplementResult? Result { get; private set; }
    public bool Finished => Result is not null;
    public string? Outcome => Result?.Status;

    public IReadOnlyList<ToolDefinition> Definitions => Tools;

    public async Task StartAsync(CancellationToken ct)
    {
        if (_workspace is not null) return;
        if (File.Exists(ResultFile(_job, _context.Round))) File.Delete(ResultFile(_job, _context.Round));
        _workspace = await Workspace.OpenAsync(_context.Volume, _env, _limits);
    }

    private Workspace Ws => _workspace ?? throw new InvalidOperationException("the workspace is not started");

    public async Task<ToolOutput> CallAsync(string name, IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct)
    {
        ToolOutput output;
        if (Finished) output = new ToolOutput("already finished; stop now", true);
        else if (++ToolCalls > MaxToolCalls) output = new ToolOutput($"tool budget of {MaxToolCalls} calls is used up: call submit or give_up now", true);
        else
        {
            await StartAsync(ct);
            try
            {
                output = name switch
                {
                    "bash" => await BashAsync(ToolInput.String(input, "command") ?? "", Int(input, "timeout_seconds"), ct),
                    "edit" => await EditAsync(input, ct),
                    "run_tests" => await RunTestsAsync(ct),
                    "read_dossier" => ReadDossier(ToolInput.String(input, "section")),
                    "submit" => await SubmitAsync(input, ct),
                    "give_up" => GiveUp(ToolInput.String(input, "kind") ?? "", ToolInput.String(input, "reason") ?? ""),
                    _ => new ToolOutput($"unknown tool {name}", true),
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

    // ---------------------------------------------------------------- bash and tests

    private async Task<ToolOutput> BashAsync(string command, int? timeout, CancellationToken ct)
    {
        if (command.Trim().Length == 0) return new ToolOutput("command is empty", true);
        var seconds = Math.Clamp(timeout ?? 120, 1, MaxCommandSeconds);
        var r = await Ws.BashAsync(command, TimeSpan.FromSeconds(seconds), ct);
        var text = $"exit {r.ExitCode}{(r.TimedOut ? $" (timed out after {seconds} s)" : "")}, {r.Duration.TotalSeconds:0.0} s\n{Clip(Join(r.Stdout, r.Stderr))}";
        return new ToolOutput(text, false);
    }

    private async Task<ToolOutput> RunTestsAsync(CancellationToken ct)
    {
        var run = Allowing(await Ws.RunTestsAsync(Path.Combine(_dir, $"tests-{++_testRuns}"), ct));
        return new ToolOutput(Describe(run), false);
    }

    /// <summary>The run with tests gone from files the PRP allows editing taken out of the gate (<see cref="TestDelta.Allowing"/>).</summary>
    private TestRun Allowing(TestRun run) => run with { Delta = run.Delta.Allowing(id => SpecTools.Accounts(_prp.Spec.AllowedEdits, id)) };

    private static string Describe(TestRun run)
    {
        if (run.Outcomes.Count == 0)
            return $"The test command produced no report we can read (exit {run.ExitCode}{(run.TimedOut ? ", timed out" : "")}, {run.Seconds} s). It may not compile or collect. Last output:\n{run.LogTail}";
        return $"{run.Delta.Summary()}\n(exit {run.ExitCode}, {run.Seconds} s)";
    }

    // ---------------------------------------------------------------- the editor

    private async Task<ToolOutput> EditAsync(IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct)
    {
        var command = ToolInput.String(input, "command") ?? "";
        var (rel, error) = Resolve(ToolInput.String(input, "path") ?? "");
        if (rel is null) return new ToolOutput(error!, true);
        var path = $"{DockerSandbox.RepoPath}/{rel}";
        var q = Workspace.Quote(path);

        switch (command)
        {
            case "view":
            {
                var kind = await Ws.BashAsync($"if [ -d {q} ]; then echo dir; elif [ -f {q} ]; then echo file; else echo none; fi", TimeSpan.FromSeconds(30), ct);
                var what = kind.Stdout.Trim();
                if (what == "none") return new ToolOutput($"{rel} doesn't exist", true);
                if (what == "dir")
                {
                    var ls = await Ws.BashAsync($"cd {q} && find . -maxdepth 2 -not -path '*/.git' -not -path '*/.git/*' -not -path '*/node_modules/*' -not -path '*/__pycache__*' | sort | head -400", TimeSpan.FromSeconds(60), ct);
                    return new ToolOutput(ls.Stdout, false);
                }
                var text = await ReadTextAsync(path, ct);
                var lines = text.Replace("\r\n", "\n").Split('\n');
                var range = ToolInput.Array<int>(input, "view_range");
                var from = range is { Count: >= 1 } ? Math.Max(1, range[0]) : 1;
                var to = range is { Count: >= 2 } && range[1] != -1 ? Math.Min(lines.Length, range[1]) : lines.Length;
                var sb = new StringBuilder();
                for (var i = from; i <= to && i <= lines.Length; i++)
                {
                    sb.Append(i.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(6)).Append('\t').Append(lines[i - 1]).Append('\n');
                    if (sb.Length > MaxViewChars)
                    {
                        sb.Append($"... cut at line {i} of {lines.Length}: view the rest with view_range [{i + 1}, {lines.Length}]\n");
                        break;
                    }
                }
                return new ToolOutput(sb.ToString(), false);
            }
            case "create":
            {
                var fileText = ToolInput.String(input, "file_text");
                if (fileText is null) return new ToolOutput("create needs file_text", true);
                var exists = await Ws.BashAsync($"[ -e {q} ] && echo yes || echo no", TimeSpan.FromSeconds(30), ct);
                if (exists.Stdout.Trim() == "yes") return new ToolOutput($"{rel} exists: change it with str_replace or insert, so the diff shows only what changed", true);
                await WriteTextAsync(path, fileText, ct, mkdir: true);
                return new ToolOutput($"created {rel} ({fileText.Split('\n').Length} lines)", false);
            }
            case "str_replace":
            {
                var oldStr = ToolInput.String(input, "old_str");
                var newStr = ToolInput.String(input, "new_str") ?? "";
                if (string.IsNullOrEmpty(oldStr)) return new ToolOutput("str_replace needs old_str, copied exactly from the file", true);
                var text = await ReadTextAsync(path, ct);
                var crlf = text.Contains("\r\n", StringComparison.Ordinal) && !oldStr.Contains('\r');
                if (crlf) { oldStr = oldStr.Replace("\n", "\r\n"); newStr = newStr.Replace("\n", "\r\n"); }
                var at = AllIndexes(text, oldStr);
                if (at.Count == 0) return new ToolOutput($"old_str was not found in {rel}; it must match exactly, whitespace and indentation included (view the lines first)", true);
                if (at.Count > 1)
                    return new ToolOutput($"old_str appears {at.Count} times in {rel} (lines {string.Join(", ", at.Select(i => LineOf(text, i)))}); include more surrounding lines so it is unique", true);
                var updated = string.Concat(text.AsSpan(0, at[0]), newStr, text.AsSpan(at[0] + oldStr.Length));
                await WriteTextAsync(path, updated, ct);
                return new ToolOutput($"edited {rel}:\n{Snippet(updated, LineOf(updated, at[0]), newStr.Split('\n').Length)}", false);
            }
            case "insert":
            {
                var newStr = ToolInput.String(input, "new_str") ?? ToolInput.String(input, "insert_text");
                if (newStr is null || !input.TryGetValue("insert_line", out var lineEl) || lineEl.ValueKind != JsonValueKind.Number)
                    return new ToolOutput("insert needs insert_line (0 inserts at the top) and new_str", true);
                var text = await ReadTextAsync(path, ct);
                var eol = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
                var lines = text.Split(eol).ToList();
                var after = lineEl.GetInt32();
                if (after < 0 || after > lines.Count) return new ToolOutput($"insert_line must be between 0 and {lines.Count}", true);
                var insert = newStr.Replace("\r\n", "\n").Split('\n');
                if (insert.Length > 1 && insert[^1].Length == 0) insert = insert[..^1];
                lines.InsertRange(after, insert);
                var updated = string.Join(eol, lines);
                await WriteTextAsync(path, updated, ct);
                return new ToolOutput($"inserted {insert.Length} line(s) into {rel} after line {after}:\n{Snippet(updated, after + 1, insert.Length)}", false);
            }
            default:
                return new ToolOutput("command must be view, create, str_replace or insert", true);
        }
    }

    /// <summary>A path inside the repository, relative; never .git, never outside.</summary>
    internal static (string? Rel, string? Error) Resolve(string path)
    {
        var p = path.Trim().Replace('\\', '/');
        if (p.StartsWith(DockerSandbox.RepoPath + "/", StringComparison.Ordinal)) p = p[(DockerSandbox.RepoPath.Length + 1)..];
        else if (p == DockerSandbox.RepoPath || p is "" or "." or "./") return (".", null);
        else if (p.StartsWith('/')) return (null, $"{path} is outside the repository ({DockerSandbox.RepoPath})");
        if (p.StartsWith("./", StringComparison.Ordinal)) p = p[2..];
        var parts = p.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(s => s == "..")) return (null, "paths may not contain ..");
        if (parts.Length > 0 && parts[0] == ".git") return (null, "the .git folder is not part of the change");
        return (string.Join('/', parts), null);
    }

    private async Task<string> ReadTextAsync(string path, CancellationToken ct)
    {
        var r = await Ws.BashAsync($"[ -f {Workspace.Quote(path)} ] && base64 -w0 {Workspace.Quote(path)}", TimeSpan.FromSeconds(60), ct);
        if (!r.Ok) throw new InvalidOperationException($"{path[(DockerSandbox.RepoPath.Length + 1)..]} doesn't exist or isn't a file");
        var bytes = Convert.FromBase64String(r.Stdout.Trim());
        try { return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes).TrimStart('﻿'); }
        catch (DecoderFallbackException) { throw new InvalidOperationException("the file is not UTF-8 text: change it with bash"); }
    }

    private async Task WriteTextAsync(string path, string text, CancellationToken ct, bool mkdir = false)
    {
        var q = Workspace.Quote(path);
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        var r = await Ws.BashAsync($"{(mkdir ? $"mkdir -p \"$(dirname {q})\" && " : "")}base64 -d > {q}", TimeSpan.FromSeconds(60), ct, stdin: b64);
        if (!r.Ok) throw new InvalidOperationException($"writing the file failed: {r.Tail(3)}");
    }

    private static List<int> AllIndexes(string text, string s)
    {
        var at = new List<int>();
        for (var i = text.IndexOf(s, StringComparison.Ordinal); i >= 0; i = text.IndexOf(s, i + 1, StringComparison.Ordinal)) at.Add(i);
        return at;
    }

    private static int LineOf(string text, int index) => text.AsSpan(0, index).Count('\n') + 1;

    private static string Snippet(string text, int line, int count)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var from = Math.Max(1, line - 3);
        var to = Math.Min(lines.Length, line + count + 3);
        return string.Join('\n', Enumerable.Range(from, to - from + 1).Select(i => $"{i,6}\t{lines[i - 1]}"));
    }

    // ---------------------------------------------------------------- dossier

    private ToolOutput ReadDossier(string? section)
    {
        var file = Path.Combine(_job.Dir, "dossier", "dossier.md");
        if (!File.Exists(file)) return new ToolOutput("there is no dossier for this job", true);
        var text = File.ReadAllText(file).Replace("\r\n", "\n");
        if (!string.IsNullOrWhiteSpace(section))
        {
            var parts = text.Split("\n## ");
            var hit = parts.Skip(1).FirstOrDefault(p => p.StartsWith(section, StringComparison.OrdinalIgnoreCase) || p.Split('\n')[0].Contains(section, StringComparison.OrdinalIgnoreCase));
            if (hit is null) return new ToolOutput($"no section '{section}'; sections: {string.Join(", ", parts.Skip(1).Select(p => p.Split('\n')[0]))}", true);
            text = "## " + hit;
        }
        return new ToolOutput(RepoReader.Trim(text, 30_000), false);
    }

    // ---------------------------------------------------------------- submit and give up

    private async Task<ToolOutput> SubmitAsync(IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct)
    {
        _submissions++;
        ImplementSubmission? s;
        try { s = JsonSerializer.SerializeToElement(input).Deserialize<ImplementSubmission>(ToolInput.SnakeCase); }
        catch (JsonException e) { return Rejected([$"the input doesn't match the schema: {e.Message}"]); }
        if (s is null) return Rejected(["empty input"]);
        s = s with { Requirements = s.Requirements ?? [] };

        var errors = CheckSubmission(s, _prp);
        var diff = await Ws.DiffAsync(_context.StartCommit, ct);
        var changes = Patch.Parse(diff);
        errors.AddRange(CheckChange(changes, _prp));
        TestRun? run = null;
        if (changes.Count > 0)
        {
            run = Allowing(await Ws.RunTestsAsync(Path.Combine(_dir, $"submit-{_submissions}-tests"), ct));
            _testRuns++;
            errors.AddRange(CheckTests(run));
        }

        if (errors.Count > 0 && _submissions < MaxSubmissions) return Rejected(errors, run);
        Finish(new ImplementResult("submitted", s, null, null, errors, _submissions, ToolCalls, _testRuns));
        return new ToolOutput(errors.Count == 0
            ? "ACCEPTED by the checks. Foreman now verifies the change in a fresh copy and a reviewer reads it against the PRP. You are done: stop now."
            : "Kept with the problems listed as warnings; verification will see them. Stop now.", false);
    }

    internal static List<string> CheckSubmission(ImplementSubmission s, Prp prp)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(s.Summary)) errors.Add("summary is empty");
        if (string.IsNullOrWhiteSpace(s.PrTitle)) errors.Add("pr_title is empty");
        if (string.IsNullOrWhiteSpace(s.PrDescription)) errors.Add("pr_description is empty");
        var ids = prp.Spec.Requirements.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var given = s.Requirements.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var missing in ids.Where(i => !given.Contains(i))) errors.Add($"requirements: {missing} is missing; account for every requirement of the PRP");
        foreach (var r in s.Requirements)
        {
            if (!ids.Contains(r.Id)) errors.Add($"requirements: {r.Id} is not a requirement of the PRP ({string.Join(", ", ids)})");
            if (r.Status is not ("done" or "not_done")) errors.Add($"{r.Id}: status must be done or not_done");
            if (r.Status == "not_done" && string.IsNullOrWhiteSpace(r.Note)) errors.Add($"{r.Id}: not_done needs a note saying why");
            if (r.Status == "done" && string.IsNullOrWhiteSpace(r.Where)) errors.Add($"{r.Id}: where: the files and the test that show it holds");
        }
        return errors;
    }

    /// <summary>What code can say about the change before any test runs.</summary>
    internal static List<string> CheckChange(IReadOnlyList<FileChange> changes, Prp prp)
    {
        if (changes.Count == 0) return ["nothing changed in the repository: there is no diff to submit"];
        var errors = DiffGuard.Check(changes, GuardPolicy.Of(prp)).Where(f => f.Blocks)
            .Select(f => $"diff guard, {f.Rule}: {f.Path}{(f.Line is null ? "" : $":{f.Line}")}: {f.Detail}. Undo it; if the PRP really needs it, give_up with kind spec_problem and say so")
            .ToList();
        if (NeedsTest(prp) && !changes.Any(c => c.Kind != ChangeKind.Deleted && CodePaths.IsTest(c.Path)))
            errors.Add("the PRP needs a test (an acceptance test or a reproduction task), and the change adds none: write the test that fails before your fix and passes after it");
        if (!changes.Any(c => !CodePaths.IsTest(c.Path)) && prp.Spec.TouchPoints.Any(t => !CodePaths.IsTest(t.Path)))
            errors.Add("the change only touches tests; the PRP's change goes in " + string.Join(", ", prp.Spec.TouchPoints.Where(t => !CodePaths.IsTest(t.Path)).Select(t => t.Path)));
        return errors;
    }

    internal static bool NeedsTest(Prp prp) =>
        prp.Spec.Requirements.Any(r => r.Verify.Any(v => v.Kind == "acceptance_test")) || prp.Spec.Tasks.Any(t => t.Kind == "reproduce");

    private static IEnumerable<string> CheckTests(TestRun run)
    {
        if (run.Outcomes.Count == 0)
        {
            yield return $"the test command produced no report (exit {run.ExitCode}): the suite may not compile or collect. Last output:\n{run.LogTail}";
            yield break;
        }
        if (run.Delta.Regressions.Count > 0)
            yield return $"{run.Delta.Regressions.Count} test(s) that passed before the change don't now: {string.Join(", ", run.Delta.Regressions.Take(10).Select(r => $"{r.Id} ({r.Now})"))}";
        if (run.Delta.NewFailing.Count > 0)
            yield return $"new test(s) fail: {string.Join(", ", run.Delta.NewFailing.Take(10))}";
    }

    private ToolOutput GiveUp(string kind, string reason)
    {
        if (kind is not ("spec_problem" or "test_problem" or "environment" or "cannot_do")) return new ToolOutput("kind must be spec_problem, test_problem, environment or cannot_do", true);
        if (reason.Trim().Length < 20) return new ToolOutput("give the concrete reason: what is wrong, where, and what a human should decide", true);
        Finish(new ImplementResult("gave_up", null, kind, reason.Trim(), [], _submissions, ToolCalls, _testRuns));
        return new ToolOutput("Recorded. A human will read it. Stop now.", false);
    }

    private void Finish(ImplementResult result)
    {
        Result = result;
        _job.WriteJson("implement", RoundDir(_context.Round) + "/result.json", result);
    }

    private ToolOutput Rejected(IReadOnlyList<string> errors, TestRun? run = null) =>
        new($"NOT ACCEPTED (attempt {_submissions} of {MaxSubmissions}). Fix these and call submit again:\n- " + string.Join("\n- ", errors)
            + (run is null ? "" : $"\n\nThe test run:\n{Describe(run)}"), true);

    // ---------------------------------------------------------------- helpers

    private static int? Int(IReadOnlyDictionary<string, JsonElement> input, string key) =>
        input.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static string Join(string stdout, string stderr) =>
        stderr.Trim().Length == 0 ? stdout : stdout.Trim().Length == 0 ? stderr : stdout.TrimEnd() + "\n--- stderr\n" + stderr;

    internal static string Clip(string s) =>
        s.Length <= HeadChars + TailChars ? s : s[..HeadChars] + $"\n... [{s.Length - HeadChars - TailChars} characters cut] ...\n" + s[^TailChars..];

    public ValueTask DisposeAsync() => _workspace?.DisposeAsync() ?? ValueTask.CompletedTask;

    // ---------------------------------------------------------------- definitions

    private static JsonElement Schema(object o) => RepoReader.Schema(o);

    public static readonly IReadOnlyList<ToolDefinition> Tools =
    [
        new("bash", $"""
            Run a bash command in {DockerSandbox.RepoPath} (the repository, set up as recon left it: dependencies installed, built).
            You are an unprivileged user and the network is OFF: nothing can be installed or downloaded. Files persist between calls;
            the shell does not (no cd or exports carry over). Long output is cut in the middle. Default timeout 120 s, max {MaxCommandSeconds} s.
            Use it to search (grep -rn, find), run single tests, and run the linters that work offline.
            """,
            Schema(new { type = "object", properties = new { command = new { type = "string" }, timeout_seconds = new { type = "integer" } }, required = new[] { "command" }, additionalProperties = false })),
        new("edit", """
            View and change files. Paths are relative to the repository root (or absolute under /work/repo).
            - view: a file with line numbers (view_range [start, end] for part of it; end -1 means to the end), or a folder two levels deep.
            - create: a NEW file with file_text. Existing files are changed with str_replace or insert.
            - str_replace: replace old_str, which must appear exactly once (whitespace included), with new_str. Include enough lines to be unique.
            - insert: insert new_str after line insert_line (0 = at the top).
            """,
            Schema(new
            {
                type = "object",
                properties = new
                {
                    command = new { type = "string", @enum = new[] { "view", "create", "str_replace", "insert" } },
                    path = new { type = "string" },
                    view_range = new { type = "array", items = new { type = "integer" } },
                    file_text = new { type = "string" },
                    old_str = new { type = "string" },
                    new_str = new { type = "string" },
                    insert_line = new { type = "integer" },
                },
                required = new[] { "command", "path" },
                additionalProperties = false,
            })),
        new("run_tests", "Run the repository's whole test suite with the command recon verified (network off) and compare it with the baseline: regressions (tests that passed before and don't now), new tests passing and failing, known-red tests. For single tests, use bash.",
            Schema(new { type = "object", properties = new { }, additionalProperties = false })),
        new("read_dossier", "Read recon's dossier: architecture, conventions, where the task lives, risks. Optionally one section by its heading.",
            Schema(new { type = "object", properties = new { section = new { type = "string" } }, additionalProperties = false })),
        new("submit", """
            Submit the change as it is in the repository now. Foreman takes the diff itself; you describe it. Checked by code before it is
            accepted: the diff guard (no edits to tests, CI, test config or lockfiles the PRP doesn't allow; no skip markers), every requirement
            accounted for, a test in the change when the PRP needs one, and the whole suite run with no regression and no failing new test.
            Problems come back to fix.
            """,
            Schema(new
            {
                type = "object",
                properties = new
                {
                    summary = new { type = "string", description = "What you changed and why, for the reviewer. A few sentences." },
                    pr_title = new { type = "string", description = "In the repository's commit and PR style." },
                    pr_description = new { type = "string", description = "Markdown: the problem, the fix, how it was tested. In the language of the repository's existing PRs (usually English)." },
                    requirements = new
                    {
                        type = "array",
                        description = "Every requirement of the PRP (R1, R2...).",
                        items = new
                        {
                            type = "object",
                            properties = new
                            {
                                id = new { type = "string" },
                                status = new { type = "string", @enum = new[] { "done", "not_done" } },
                                where = new { type = "string", description = "The files and symbols that make it hold, and the test that shows it." },
                                note = new { type = "string", description = "Required for not_done: why." },
                            },
                            required = new[] { "id", "status", "where" },
                            additionalProperties = false,
                        },
                    },
                    notes = new { type = "string", description = "Anything the human should know: a judgment call, a limitation." },
                },
                required = new[] { "summary", "pr_title", "pr_description", "requirements" },
                additionalProperties = false,
            })),
        new("give_up", """
            Stop without a change, for a human to decide. Use it when the PRP contradicts itself or the code, when an existing test looks wrong
            and the PRP doesn't say to change it, or when the environment can't run what the PRP needs. Don't work around such a problem.
            """,
            Schema(new
            {
                type = "object",
                properties = new
                {
                    kind = new { type = "string", @enum = new[] { "spec_problem", "test_problem", "environment", "cannot_do" } },
                    reason = new { type = "string", description = "What is wrong, where (path:line), and what a human should decide." },
                },
                required = new[] { "kind", "reason" },
                additionalProperties = false,
            })),
    ];
}
