using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Foreman.Core;
using Foreman.Planning.Spec;
using Foreman.Recon.CodeMap;
using Foreman.Recon.Dossier;
using Foreman.Recon.Reports;
using Foreman.Recon.Workspaces;

namespace Foreman.Implement;

/// <summary>A test the writer declares: which requirement it shows and what it must do on the code before the change.</summary>
/// <param name="Match">A part of the test's id as the runner reports it (usually its function or method name).</param>
/// <param name="ExpectOnBase"><c>fail</c> (new behaviour: must fail or not run before the change) or <c>pass</c>
/// (behaviour the change must keep).</param>
public sealed record AcceptanceTestSpec(string Match, string Requirement, string ExpectOnBase);

/// <summary>A mutant the writer says no test can catch because it behaves exactly like the fix, and why.</summary>
public sealed record EquivalentMutant(string Id, string Reason);

public sealed record AcceptanceSubmission(IReadOnlyList<AcceptanceTestSpec> Tests, string ReferenceNote, IReadOnlyList<string>? Gaps = null, IReadOnlyList<EquivalentMutant>? Equivalent = null);

/// <summary>One hidden test as Foreman measured it.</summary>
/// <param name="OnBase">passed, failed, skipped or missing (didn't run or didn't compile) on the code before the change.</param>
public sealed record HiddenTest(string Id, string Requirement, string ExpectOnBase, string OnBase, string OnReference);

/// <param name="Killed">At least one hidden test failed (or didn't run) on the mutant. <c>null</c>: the mutant was
/// stillborn (it didn't apply, or nothing compiled), so it says nothing about the tests.</param>
/// <param name="Equivalent">The writer's reason why this mutant behaves exactly like the fix; it then doesn't count.</param>
public sealed record MutantResult(string Id, string Kind, string Description, bool? Killed, IReadOnlyList<string> KilledBy, string? Equivalent = null);

/// <param name="Status"><c>ready</c>, <c>gave_up</c> or <c>none</c> (the PRP has no acceptance-tested requirement).</param>
public sealed record AcceptanceReport(
    string Status,
    string? Reason,
    IReadOnlyList<string> TestFiles,
    string? TestsSha256,
    IReadOnlyList<HiddenTest> Tests,
    IReadOnlyList<MutantResult> Mutants,
    double? MutationScore,
    IReadOnlyList<string> Gaps,
    IReadOnlyList<string> Warnings,
    int Submissions,
    int ToolCalls)
{
    public IEnumerable<string> HiddenIds => Tests.Select(t => t.Id);
}

/// <summary>
/// The acceptance test writer's tools: the read-only repository tools, a lab (a throwaway copy of the set-up
/// repository, network off), <c>submit_tests</c> and <c>give_up</c>. The writer sees the PRP, never the implementer's
/// work. It writes black-box tests in new files and, next to them, a reference fix of its own. <c>submit_tests</c>
/// tests the tests, by code, in the lab: every declared test runs; the ones for new behaviour fail on the code before
/// the change and those that pin kept behaviour pass; all of them pass with the reference fix and break nothing that
/// passed before; and they catch broken versions of the reference fix (<see cref="Mutants"/>). The tests and the
/// measurements go to <c>acceptance/</c>; the reference fix is kept for the record and never shown to the implementer.
/// </summary>
public sealed partial class AcceptanceTools : IToolbox
{
    public const int MaxToolCalls = 80;
    public const int MaxSubmissions = 3;
    public const int MaxMutants = 4;
    public const double MinMutationScore = 0.5;

    private readonly Job _job;
    private readonly string _dir;
    private readonly Prp _prp;
    private readonly string _taskType;
    private readonly RepoReader _repo;
    private readonly Lab _lab;
    private bool _started;
    private int _submissions;

    public AcceptanceTools(Job job)
    {
        _job = job;
        _dir = job.StageDir("acceptance");
        _prp = job.ReadJson<Prp>("spec", "prp.json");
        _taskType = SpecStage.Context(job).TaskType;
        _repo = new RepoReader(job);
        _lab = new Lab(job, _dir, Lab.AcceptanceSuffix);
    }

    public static string ResultFile(Job job) => Path.Combine(job.Dir, "acceptance", "acceptance.json");
    public int ToolCalls { get; private set; }
    public AcceptanceReport? Result { get; private set; }
    public bool Finished => Result is not null;
    public string? Outcome => Result?.Status;

    public IReadOnlyList<ToolDefinition> Definitions => [.. RepoReader.ReadTools, Lab.Run, Lab.RunTests, SubmitTests, GiveUp];

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
        if (Finished) output = new ToolOutput("already finished; stop now", true);
        else if (++ToolCalls > MaxToolCalls) output = new ToolOutput($"tool budget of {MaxToolCalls} calls is used up: call submit_tests or give_up now", true);
        else
        {
            if (!_started) await StartAsync(ct);
            try
            {
                output = name switch
                {
                    "lab_run" => await _lab.RunAsync(ToolInput.String(input, "command") ?? "", Lab.Int(input, "timeout_seconds"), ct),
                    "lab_run_tests" => await _lab.RunTestsAsync(ct),
                    "submit_tests" => await SubmitAsync(input, ct),
                    "give_up" => GiveUpNow(ToolInput.String(input, "reason") ?? ""),
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

    private ToolOutput GiveUpNow(string reason)
    {
        if (reason.Trim().Length < 20) return new ToolOutput("give the concrete reason: which requirement can't be tested from outside, and what the PRP must say instead", true);
        Finish(new AcceptanceReport("gave_up", reason.Trim(), [], null, [], [], null, [], [], _submissions, ToolCalls));
        return new ToolOutput("Recorded. A human will read it. Stop now.", false);
    }

    private void Finish(AcceptanceReport report)
    {
        Result = report;
        _job.WriteJson("acceptance", "acceptance.json", report);
    }

    // ---------------------------------------------------------------- submit: the tests are tested

    private async Task<ToolOutput> SubmitAsync(IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct)
    {
        _submissions++;
        AcceptanceSubmission? s;
        try { s = JsonSerializer.SerializeToElement(input).Deserialize<AcceptanceSubmission>(ToolInput.SnakeCase); }
        catch (JsonException e) { return Rejected([$"the input doesn't match the schema: {e.Message}"]); }
        if (s is null) return Rejected(["empty input"]);
        s = s with { Tests = s.Tests ?? [], Gaps = s.Gaps ?? [], Equivalent = s.Equivalent ?? [] };

        var diff = await _lab.DiffAsync(ct);
        var files = Patch.Parse(diff);
        var errors = CheckShape(s, files, _prp, _taskType);
        if (errors.Count > 0) return _submissions < MaxSubmissions ? Rejected(errors) : Keep(s, diff, files, errors, [], [], null);

        var tests = Patch.Select(diff, p => IsHiddenTestFile(p));
        var fix = Patch.Select(diff, p => !CodePaths.IsTest(p));
        var mutants = Mutants.Of(fix, MaxMutants);
        List<HiddenTest> measured;
        List<MutantResult> mutantResults = [];
        try
        {
            var onBase = Outcomes(await _lab.RunStateAsync($"submit-{_submissions}-base", [tests], ct));
            var reference = await _lab.RunStateAsync($"submit-{_submissions}-reference", [fix, tests], ct);
            var onReference = Outcomes(reference);
            var hidden = reference.Delta.NewPassing.Concat(reference.Delta.NewFailing).Concat(reference.Delta.NewSkipped).ToList();
            measured = Measure(s.Tests, hidden, onBase, onReference, out var measureErrors);
            errors.AddRange(measureErrors);
            if (reference.Outcomes.Count == 0) errors.Add($"with your reference fix and tests the test command produced no report: {reference.LogTail}");
            var broken = reference.Delta.Regressions.Where(r => !SpecTools.Accounts(_prp.Spec.AllowedEdits, r.Id)).ToList();
            if (broken.Count > 0)
                errors.Add($"your reference fix breaks {broken.Count} test(s) that passed before: {string.Join(", ", broken.Take(10).Select(r => r.Id))}. Fix the reference (the PRP's allowed edits are the only exceptions).");

            if (errors.Count == 0)
            {
                var strong = measured.Where(t => t.OnReference == "passed").Select(t => t.Id).ToList();
                for (var i = 0; i < mutants.Count; i++)
                {
                    var m = mutants[i];
                    TestRun run;
                    try { run = await _lab.RunStateAsync($"submit-{_submissions}-mutant-{i + 1}", [m.Patch, tests], ct); }
                    catch (InvalidOperationException) { mutantResults.Add(new MutantResult(m.Id, m.Kind, m.Description, null, [])); continue; }
                    if (run.Outcomes.Count == 0) { mutantResults.Add(new MutantResult(m.Id, m.Kind, m.Description, null, [])); continue; }
                    var now = Outcomes(run);
                    var killers = strong.Where(id => now.GetValueOrDefault(id) != "passed").ToList();
                    var equivalent = killers.Count == 0 ? s.Equivalent!.FirstOrDefault(e => e.Id == m.Id && e.Reason.Trim().Length >= 15)?.Reason.Trim() : null;
                    mutantResults.Add(new MutantResult(m.Id, m.Kind, m.Description, killers.Count > 0, killers, equivalent));
                }
                var score = Score(mutantResults);
                var survivors = mutantResults.Where(r => r.Killed == false && r.Equivalent is null).ToList();
                if (score is { } sc && sc < MinMutationScore)
                    errors.Add($"your tests let {survivors.Count} of {mutantResults.Count(r => r.Killed is not null)} broken versions of your reference fix pass: " +
                               string.Join("; ", survivors.Select(r => $"{r.Id} ({r.Kind}): {r.Description}")) +
                               ". Tests that a wrong fix also passes don't pin the behaviour: strengthen them (exact values, the edge each requirement names). " +
                               "If a mutant behaves exactly like your fix for every input the PRP allows, list it in equivalent with the reason; Foreman records it.");
            }
        }
        finally
        {
            await _lab.RestoreAsync([diff], ct);
        }

        if (errors.Count > 0 && _submissions < MaxSubmissions) return Rejected(errors);
        return Keep(s, diff, files, errors, measured, mutantResults, tests);
    }

    private ToolOutput Keep(AcceptanceSubmission s, string diff, IReadOnlyList<FileChange> files, List<string> errors, List<HiddenTest> measured, List<MutantResult> mutants, string? tests)
    {
        tests ??= Patch.Select(diff, p => IsHiddenTestFile(p));
        File.WriteAllText(Path.Combine(_dir, "tests.diff"), tests, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(_dir, "reference.diff"), Patch.Select(diff, p => !CodePaths.IsTest(p)), new UTF8Encoding(false));
        var report = new AcceptanceReport("ready", s.ReferenceNote, files.Where(f => IsHiddenTestFile(f.Path)).Select(f => f.Path).ToList(),
            Sha256(tests), measured, mutants, Score(mutants), s.Gaps ?? [], errors, _submissions, ToolCalls);
        Finish(report);
        return new ToolOutput(errors.Count == 0 ? "ACCEPTED: the tests are recorded. Stop now." : "Kept with the problems listed as warnings. Stop now.", false);
    }

    private ToolOutput Rejected(IReadOnlyList<string> errors) =>
        new($"NOT ACCEPTED (attempt {_submissions} of {MaxSubmissions}); your lab is as you left it. Fix these and call submit_tests again:\n- " + string.Join("\n- ", errors), true);

    internal static double? Score(IReadOnlyList<MutantResult> results)
    {
        var valid = results.Where(r => r.Killed is not null && r.Equivalent is null).ToList();
        return valid.Count < 2 ? null : Math.Round((double)valid.Count(r => r.Killed == true) / valid.Count, 2);
    }

    internal static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>A file of hidden tests: a new test file with "acceptance" in its name, so it can't collide with the implementer's.</summary>
    internal static bool IsHiddenTestFile(string path) =>
        CodePaths.IsTest(path) && Path.GetFileName(path).Contains("acceptance", StringComparison.OrdinalIgnoreCase);

    /// <summary>What the submission and the lab's diff must look like before anything runs.</summary>
    internal static List<string> CheckShape(AcceptanceSubmission s, IReadOnlyList<FileChange> files, Prp prp, string taskType)
    {
        var errors = new List<string>();
        var hidden = files.Where(f => IsHiddenTestFile(f.Path)).ToList();
        if (hidden.Count == 0)
            errors.Add("no acceptance test file: write the tests in a NEW test file whose name contains \"acceptance\" (test_acceptance_x.py, x.acceptance.test.ts, AcceptanceTests.cs), where the test runner finds it");
        foreach (var f in hidden.Where(f => f.Kind != ChangeKind.Added))
            errors.Add($"{f.Path} already exists: hidden tests go in new files only");
        foreach (var f in files.Where(f => CodePaths.IsTest(f.Path) && !IsHiddenTestFile(f.Path)))
            errors.Add($"{f.Path}: leave existing and other test files alone (undo the change in the lab); only your new acceptance files are kept");
        if (taskType is "bug" or "feature" && !files.Any(f => !CodePaths.IsTest(f.Path)))
            errors.Add("no reference fix: implement the change in the lab (outside the test files) so the tests can be checked against it");

        var ids = prp.Spec.Requirements.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var t in s.Tests)
        {
            if (!ids.Contains(t.Requirement)) errors.Add($"test '{t.Match}': requirement {t.Requirement} doesn't exist");
            if (t.ExpectOnBase is not ("fail" or "pass")) errors.Add($"test '{t.Match}': expect_on_base must be fail or pass");
            if (string.IsNullOrWhiteSpace(t.Match)) errors.Add($"a test for {t.Requirement} has no match");
        }
        foreach (var r in prp.Spec.Requirements.Where(r => r.Verify.Any(v => v.Kind == "acceptance_test") && !s.Tests.Any(t => t.Requirement == r.Id)))
            errors.Add($"{r.Id} is checked by an acceptance test in the PRP and has none");
        if (taskType is "bug" or "feature" && s.Tests.Count > 0 && s.Tests.All(t => t.ExpectOnBase != "fail"))
            errors.Add("no test is expected to fail on the code before the change: a bug or a feature needs at least one that shows the new behaviour");
        return errors;
    }

    /// <summary>
    /// Matches each declared test to the hidden test ids the reference run reported and checks it on both runs: every
    /// declaration matches at least one id, each matched test passes with the reference, and on the code before the
    /// change a "fail" test doesn't pass and a "pass" test does.
    /// </summary>
    internal static List<HiddenTest> Measure(IReadOnlyList<AcceptanceTestSpec> declared, IReadOnlyList<string> hidden,
        IReadOnlyDictionary<string, string> onBase, IReadOnlyDictionary<string, string> onReference, out List<string> errors)
    {
        errors = [];
        var measured = new List<HiddenTest>();
        if (hidden.Count == 0) errors.Add("the reference run shows no new test: the runner doesn't find your acceptance file, or it doesn't compile");
        foreach (var d in declared)
        {
            var ids = hidden.Where(id => id.Contains(d.Match, StringComparison.Ordinal)).ToList();
            if (ids.Count == 0 && hidden.Count > 0) { errors.Add($"'{d.Match}' ({d.Requirement}) matches no test the runner reported; new tests are: {string.Join(", ", hidden.Take(12))}"); continue; }
            foreach (var id in ids.Where(id => measured.All(m => m.Id != id)))
            {
                var b = onBase.GetValueOrDefault(id, "missing");
                var r = onReference.GetValueOrDefault(id, "missing");
                measured.Add(new HiddenTest(id, d.Requirement, d.ExpectOnBase, b, r));
                if (r != "passed") errors.Add($"{id} ({d.Requirement}) is {r} with your reference fix: the test or the reference is wrong");
                if (d.ExpectOnBase == "fail" && b == "passed") errors.Add($"{id} ({d.Requirement}) passes on the code before the change: it shows nothing new (or declare it expect_on_base: pass)");
                if (d.ExpectOnBase == "pass" && b != "passed") errors.Add($"{id} ({d.Requirement}) is declared to pass before the change but is {b} there");
            }
        }
        foreach (var id in hidden.Where(id => measured.All(m => m.Id != id)))
            errors.Add($"{id} is in your acceptance file but in no declaration: declare it (match, requirement, expect_on_base) or remove it");
        return measured;
    }

    private static Dictionary<string, string> Outcomes(TestRun run) =>
        run.Outcomes.GroupBy(o => o.Id).ToDictionary(g => g.Key, g => g.First().Status.ToString().ToLowerInvariant(), StringComparer.Ordinal);

    public ValueTask DisposeAsync() => _lab.DisposeAsync();

    // ---------------------------------------------------------------- definitions

    private static readonly ToolDefinition SubmitTests = new("submit_tests",
        "Submit the acceptance tests in your lab. Foreman takes the new test files whose names contain \"acceptance\" as the hidden tests and " +
        "everything outside test files as your reference fix, then tests the tests: each declared test runs; 'fail' tests fail on the code before " +
        "the change and 'pass' tests pass there; all pass with the reference fix, which breaks nothing that passed; and they catch broken versions of " +
        "the reference fix (a hunk left out, a condition flipped). Problems come back; your lab stays as it was.",
        RepoReader.Schema(new
        {
            type = "object",
            properties = new
            {
                tests = new
                {
                    type = "array",
                    description = "Every test in your acceptance files.",
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            match = new { type = "string", description = "Part of the test's id as the runner reports it, normally its function or method name, e.g. test_r1_last_row_exported." },
                            requirement = new { type = "string", description = "R1, R2..." },
                            expect_on_base = new { type = "string", @enum = new[] { "fail", "pass" }, description = "fail: new behaviour; pass: behaviour the change must keep." },
                        },
                        required = new[] { "match", "requirement", "expect_on_base" },
                        additionalProperties = false,
                    },
                },
                reference_note = new { type = "string", description = "One or two sentences on your reference fix (kept for the record, never shown to the implementer)." },
                gaps = new { type = "array", items = new { type = "string" }, description = "Where the PRP left a behaviour open, so you left that case untested." },
                equivalent = new
                {
                    type = "array",
                    description = "Mutants from an earlier rejection that behave exactly like your fix for every input the PRP allows, so no test can catch them.",
                    items = new { type = "object", properties = new { id = new { type = "string" }, reason = new { type = "string" } }, required = new[] { "id", "reason" }, additionalProperties = false },
                },
            },
            required = new[] { "tests", "reference_note" },
            additionalProperties = false,
        }));

    private static readonly ToolDefinition GiveUp = new("give_up",
        "Stop without tests when the PRP can't be tested from outside as written (a requirement with no observable effect, an interface that " +
        "contradicts the code). Say which requirement and what the PRP must say instead; a human decides.",
        RepoReader.Schema(new { type = "object", properties = new { reason = new { type = "string" } }, required = new[] { "reason" }, additionalProperties = false }));
}
