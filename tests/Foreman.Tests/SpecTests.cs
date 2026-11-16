using Foreman.Core;
using Foreman.Planning.Clarify;
using Foreman.Planning.Intake;
using Foreman.Planning.Spec;
using Foreman.Recon;
using Foreman.Recon.CodeMap;
using Foreman.Recon.Dossier;

namespace Foreman.Tests;

public sealed class SpecTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "foreman-tests", Guid.NewGuid().ToString("N"));
    private readonly Job _job;

    public SpecTests()
    {
        Directory.CreateDirectory(_home);
        var task = Path.Combine(_home, "task.md");
        File.WriteAllText(task, "The CSV export drops the last row when the file has no trailing newline.");
        _job = Job.Create(_home, "https://example.invalid/repo.git", task);
        var file = Path.Combine(_job.SourceDir, "src", "export", "csv.py");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "import csv\n\ndef export(rows):\n    return rows[:-1]\n");
        _job.WriteJson("codemap", "index.json", new CodeIndex([
            new FileTags("src/export/csv.py", "python", 4, [new CodeSymbol("export", "function", 3, 4, "def export(rows):")], []),
        ]));
        _job.WriteJson("fetch", "fetch.json", new FetchResult("https://example.invalid/repo.git", "0123456789abcdef", null, 200));
        _job.WriteJson("intake", "brief.json", Brief());
    }

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch (IOException) { }
    }

    private static Brief Brief() => new("en", "issue", "CSV export drops the last row", "Every row is exported.", "bug",
        [new Item("A fix")],
        [new Criterion("The last row is exported", "drops the last row", true), new Criterion("Existing tests pass", null, false)],
        [], null, null, [], [], [], new FitEstimate("go", "low", ["small"]));

    private static readonly SpecContext Context = new("bug", [("C1", true, "The last row is exported"), ("C2", false, "Existing tests pass")], ["A1"], ["K1"]);

    private static SpecDraft Draft() => new(
        Title: "Export every CSV row",
        Goal: "export returns every row, including the last one.",
        Why: ["The client's reports miss a row."],
        Requirements:
        [
            new Requirement("R1", "When the input has no trailing newline, export shall return every row.", ["C1"], [new Check("acceptance_test", "export(['a','b']) returns ['a','b']")]),
            new Requirement("R2", "Existing tests shall keep passing.", ["C2", "K1"], [new Check("existing_tests", "the baseline")]),
        ],
        Interface: [new InterfaceItem("export", "src/export/csv.py", "existing", "def export(rows)")],
        Examples: [new Example("E1", "two rows", "export is called", "both rows come back", ["R1"])],
        OutOfScope: ["Changing the CSV dialect"],
        Decisions: [new Decision("Keep the function's signature", "callers depend on it", "A1")],
        TouchPoints: [new TouchPoint("src/export/csv.py", ["export"], "stop dropping the last row")],
        Gotchas: ["`src/export/csv.py:4` slices off the last row on purpose for a header-less legacy format"],
        Tasks: [new SpecTask("T1", "reproduce", "Add a failing test", "the test fails on the current code"), new SpecTask("T2", "implement", "Fix export", "the test passes")],
        AntiPatterns: ["Appending a newline to the input instead of fixing the slice"],
        AllowedEdits: []);

    private RepoReader Repo()
    {
        var repo = new RepoReader(_job);
        repo.Start();
        return repo;
    }

    [Fact]
    public void A_complete_spec_passes() => Assert.Empty(SpecTools.Check(Draft(), Context, Repo()));

    [Fact]
    public void The_done_contract_is_enforced()
    {
        var d = Draft() with
        {
            Requirements =
            [
                new Requirement("R1", "It shall be nice.", ["Q7"], []),
                new Requirement("R2", "The code shall be clean.", ["task"], [new Check("review", "looks clean")]),
                new Requirement("R3", "The docs shall mention it.", ["task"], [new Check("review", "docs updated")]),
            ],
            Examples = [new Example("E1", "x", "y", "z", ["R9"])],
            Decisions = [],
            Tasks = [new SpecTask("T1", "implement", "Fix it", "")],
            OutOfScope = [],
        };
        var errors = SpecTools.Check(d, Context, Repo());
        Assert.Contains(errors, e => e.StartsWith("R1: source Q7 doesn't exist", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("R1: verify: how it will be checked", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("2 of 3 requirements are checked only by review", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("a bug needs at least one requirement checked by an acceptance test", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("the client's acceptance criterion C1", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("A1 from clarify", StringComparison.Ordinal));
        Assert.Contains("example E1: requirement R9 doesn't exist", errors);
        Assert.Contains(errors, e => e.StartsWith("out_of_scope", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("task T1: done_when is required", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("a bug starts with a reproduce task", StringComparison.Ordinal));
        Assert.DoesNotContain(errors, e => e.StartsWith("the client's acceptance criterion C2", StringComparison.Ordinal)); // implied criteria are not required
    }

    [Fact]
    public void Acceptance_tested_requirements_need_an_example() =>
        Assert.Contains("R1 is checked by an acceptance test but has no example: the hidden tests are written from the examples' rules",
            SpecTools.Check(Draft() with { Examples = [] }, Context, null));

    [Fact]
    public void Files_symbols_and_references_are_checked_against_the_code()
    {
        var d = Draft() with
        {
            TouchPoints = [new TouchPoint("src/export/csv.py", ["export_all"], "x"), new TouchPoint("src/export/json.py", [], "x"), new TouchPoint("src/export/csv.py", [], "x", New: true)],
            Interface = [new InterfaceItem("export_rows", "src/export/csv.py", "existing", "def export_rows()"), new InterfaceItem("export_json", "src/export/json.py", "new", "def export_json(rows)")],
            Gotchas = ["see `src/export/csv.py:40`"],
        };
        var errors = SpecTools.Check(d, Context, Repo());
        Assert.Contains(errors, e => e.StartsWith("touch point src/export/csv.py: 'export_all' is not defined there; it defines export", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("touch point src/export/json.py: no file", StringComparison.Ordinal));
        Assert.Contains("touch point src/export/csv.py is marked new but already exists", errors);
        Assert.Contains(errors, e => e.StartsWith("interface export_rows is marked existing but is not defined", StringComparison.Ordinal));
        Assert.DoesNotContain(errors, e => e.Contains("export_json", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("line 40 is outside the file", StringComparison.Ordinal));
    }

    [Fact]
    public void The_spec_waits_for_the_clients_answers()
    {
        var gaps = ClarifyTools.Number([new Gap("data", "Header?", "ask", "Write it", "high", "Header?", ClientDefault: "Yes")]);
        _job.WriteJson("clarify", "questions.json", gaps);
        var e = Assert.Throws<JobWaitingException>(() => SpecStage.AnswersOf(_job, gaps));
        Assert.StartsWith("waiting for the client: 1 questions", e.Message);
        Assert.Equal("answers", e.For);
        Answers.Record(_job, new Dictionary<string, string>(), defaults: true);
        Assert.Single(SpecStage.AnswersOf(_job, gaps));
        Assert.Empty(SpecStage.AnswersOf(_job, []));
    }

    [Fact]
    public void Ready_records_exactly_what_was_approved()
    {
        var prp = new Prp(_job.Id, "0123456789abcdef", "Draft", DateTime.UtcNow, Draft(), [new ValidationStep("python tests", "pytest -q", "baseline", Verified: true)], ["tests/test_x.py::test_slow"], [], []);
        _job.WriteJson("spec", "prp.json", prp);
        var md = prp.Markdown(_job.Manifest.Source, "bug");
        Assert.Contains("| Status | Draft |", md);
        Assert.Contains("| R1 | When the input has no trailing newline, export shall return every row. | C1 | **acceptance test**: export(['a','b']) returns ['a','b'] |", md);
        Assert.Contains("pytest -q", md);
        _job.WriteText("spec", "prp.md", md);

        Assert.StartsWith("the PRP is not Ready", Ready.Check(_job));
        var mark = Ready.Mark(_job);
        Assert.False(mark.EditedByHand);
        Assert.Null(Ready.Check(_job));
        Assert.Contains("| Status | Ready |", File.ReadAllText(Path.Combine(_job.Dir, "spec", "prp.md")));

        File.AppendAllText(Path.Combine(_job.Dir, "spec", "prp.md"), "\nAlso support TSV.\n");
        Assert.StartsWith("the PRP changed after it was marked Ready", Ready.Check(_job));
        Assert.True(Ready.Mark(_job).EditedByHand);
        Assert.Contains("Also support TSV.", File.ReadAllText(Path.Combine(_job.Dir, "spec", "prp.md")));
    }

    [Fact]
    public void The_spec_context_numbers_criteria_and_settled_gaps()
    {
        var gaps = ClarifyTools.Number([
            new Gap("data", "Header?", "ask", "Write it", "high", "Header?", ClientDefault: "Yes"),
            new Gap("scope", "Empty", "assume", "Empty file", "low"),
            new Gap("compatibility", "Version", "repository", "3.10", "low", Evidence: [new Citation("src/export/csv.py", 1)]),
        ]);
        var c = SpecContext.Of(Brief(), gaps);
        Assert.Equal(["C1", "C2"], c.Criteria.Select(x => x.Id));
        Assert.Equal(["Q1", "A1"], c.Settled);
        Assert.Equal(["K1"], c.Known!);
    }
    [Fact]
    public void A_prp_without_a_verified_test_command_says_so()
    {
        var prp = new Prp(_job.Id, "0123456789abcdef", "Draft", DateTime.UtcNow, Draft(), [new ValidationStep("lint or format", "bun run lint", "from ci.yml; not run by recon")], [], [], []);
        var md = prp.Markdown(_job.Manifest.Source, "bug");
        Assert.Contains("**No test command was verified for this job**", md);
        Assert.Contains("bun run lint", md);
    }
}
