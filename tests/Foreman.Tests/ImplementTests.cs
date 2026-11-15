using Foreman.Implement;
using Foreman.Planning.Spec;
using Foreman.Recon.Baseline;
using Foreman.Recon.Reports;

namespace Foreman.Tests;

public class ImplementTests
{
    private static Prp Prp(IReadOnlyList<AllowedEdit>? allowed = null, IReadOnlyList<TouchPoint>? touch = null) => new("job", "abc", "Ready", DateTime.UtcNow,
        new SpecDraft(
            Title: "Export every CSV row",
            Goal: "export returns every row.",
            Why: ["reports miss a row"],
            Requirements:
            [
                new Requirement("R1", "When the input has no trailing newline, export shall return every row.", ["C1"], [new Check("acceptance_test", "two rows come back")]),
                new Requirement("R2", "Existing tests shall keep passing.", ["C2"], [new Check("existing_tests", "the baseline")]),
            ],
            Interface: [],
            Examples: [],
            OutOfScope: [],
            Decisions: [new Decision("Keep the signature", "callers", "A1")],
            TouchPoints: touch ?? [new TouchPoint("src/export/csv.py", ["export"], "fix the slice")],
            Gotchas: ["the slice is on purpose for a legacy format"],
            Tasks: [new SpecTask("T1", "reproduce", "a failing test", "fails before")],
            AntiPatterns: ["appending a newline to the input"],
            AllowedEdits: allowed ?? []),
        [], [], ["src/gen/model_pb2.py"], []);

    private const string Diff = """
        diff --git a/src/export/csv.py b/src/export/csv.py
        index 1111111..2222222 100644
        --- a/src/export/csv.py
        +++ b/src/export/csv.py
        @@ -2,3 +2,3 @@ import csv

         def export(rows):
        -    return rows[:-1]
        +    return list(rows)
        diff --git a/tests/test_csv.py b/tests/test_csv.py
        new file mode 100644
        index 0000000..3333333
        --- /dev/null
        +++ b/tests/test_csv.py
        @@ -0,0 +1,4 @@
        +from export.csv import export
        +
        +def test_last_row():
        +    assert export(["a", "b"]) == ["a", "b"]
        diff --git a/assets/logo.png b/assets/logo.png
        new file mode 100644
        index 0000000..4444444
        Binary files /dev/null and b/assets/logo.png differ
        diff --git a/old.txt b/old.txt
        deleted file mode 100644
        index 5555555..0000000
        --- a/old.txt
        +++ /dev/null
        @@ -1 +0,0 @@
        -gone
        """;

    [Fact]
    public void A_git_diff_parses_into_files_with_added_line_numbers()
    {
        var files = Patch.Parse(Diff);
        Assert.Equal(["src/export/csv.py", "tests/test_csv.py", "assets/logo.png", "old.txt"], files.Select(f => f.Path));
        Assert.Equal([ChangeKind.Modified, ChangeKind.Added, ChangeKind.Added, ChangeKind.Deleted], files.Select(f => f.Kind));
        Assert.Equal(new AddedLine(4, "    return list(rows)"), Assert.Single(files[0].Added));
        Assert.Equal(["    return rows[:-1]"], files[0].Removed);
        Assert.Equal([1, 2, 3, 4], files[1].Added.Select(a => a.Line));
        Assert.True(files[2].Binary);
        Assert.Equal(["gone"], files[3].Removed);
    }

    [Fact]
    public void Quoted_paths_are_unquoted() => Assert.Equal("a/café.txt", Patch.Unquote("\"a/caf\\303\\251.txt\""));

    private static FileChange Change(string path, ChangeKind kind = ChangeKind.Modified, string[]? added = null, string[]? removed = null) =>
        new(path, kind, false, (added ?? []).Select((t, i) => new AddedLine(i + 1, t)).ToList(), removed ?? []);

    [Fact]
    public void The_guard_blocks_edits_that_turn_a_suite_green_without_fixing_anything()
    {
        var findings = DiffGuard.Check(
        [
            Change("tests/test_other.py", removed: ["    assert total == 3"]),
            Change("tests/test_gone.py", ChangeKind.Deleted),
            Change("tests/test_new.py", ChangeKind.Added, added: ["@pytest.mark.skip(reason='flaky')", "def test_x(): pass"]),
            Change(".github/workflows/ci.yml"),
            Change("pytest.ini"),
            Change("poetry.lock"),
            Change("src/gen/model_pb2.py"),
            Change("CLAUDE.md", ChangeKind.Added),
        ], GuardPolicy.Of(Prp()));
        var blocks = findings.Where(f => f.Blocks).Select(f => (f.Rule, f.Path)).ToList();
        Assert.Contains(("edited-test", "tests/test_other.py"), blocks);
        Assert.Contains(("deleted-test", "tests/test_gone.py"), blocks);
        Assert.Contains(("skip-added", "tests/test_new.py"), blocks);
        Assert.Contains(("ci-config", ".github/workflows/ci.yml"), blocks);
        Assert.Contains(("test-config", "pytest.ini"), blocks);
        Assert.Contains(("lockfile", "poetry.lock"), blocks);
        Assert.Contains(("do-not-edit", "src/gen/model_pb2.py"), blocks);
        Assert.Contains(("agent-config", "CLAUDE.md"), blocks);
        Assert.Contains(findings, f => f is { Rule: "changed-assertion", Blocks: false });
    }

    [Fact]
    public void An_allowed_edit_or_touch_point_passes_but_a_skip_marker_never_does()
    {
        var policy = GuardPolicy.Of(Prp(allowed: [new AllowedEdit("tests/test_csv.py", "a new case in the existing file")]));
        var findings = DiffGuard.Check(
        [
            Change("tests/test_csv.py", added: ["def test_last_row():", "    assert export(['a']) == ['a']"]),
            Change("src/export/csv.py", added: ["    return list(rows)"]),
            Change("tests/test_csv.py", added: ["    it.only('runs alone', () => {})"]),
        ], policy);
        Assert.DoesNotContain(findings, f => f.Blocks && f.Rule != "skip-added");
        Assert.Single(findings, f => f.Rule == "skip-added");
        Assert.DoesNotContain(findings, f => f.Rule == "outside-touch-points");
    }

    [Fact]
    public void Files_outside_the_touch_points_and_manifests_are_reported_not_blocked()
    {
        var findings = DiffGuard.Check([Change("src/util.py", ChangeKind.Added), Change("pyproject.toml")], GuardPolicy.Of(Prp()));
        Assert.All(findings, f => Assert.False(f.Blocks));
        Assert.Equal(["outside-touch-points", "manifest"], findings.Select(f => f.Rule));
    }

    private static TestBaseline B(string id, Verdict v) => new(id, v, [], null);
    private static TestOutcome O(string id, TestStatus s) => new(id, s, null);

    [Fact]
    public void Pass_to_pass_counts_failed_skipped_and_missing_tests_as_regressions()
    {
        var delta = TestDelta.Compare(
            [B("a", Verdict.Pass), B("b", Verdict.Pass), B("c", Verdict.Pass), B("d", Verdict.Pass), B("red", Verdict.KnownRed), B("flaky", Verdict.Flaky)],
            [O("a", TestStatus.Passed), O("b", TestStatus.Failed), O("c", TestStatus.Skipped), O("red", TestStatus.Passed), O("flaky", TestStatus.Failed),
             O("new1", TestStatus.Passed), O("new2", TestStatus.Failed)]);
        Assert.Equal([new TestChange("b", "failed"), new TestChange("c", "skipped"), new TestChange("d", "missing")], delta.Regressions);
        Assert.Equal(["new1"], delta.NewPassing);
        Assert.Equal(["new2"], delta.NewFailing);
        Assert.Equal(["red"], delta.FixedKnownRed);
        Assert.Equal(0, delta.KnownRedStillFailing);
    }

    [Fact]
    public void A_regression_must_fail_twice_to_count()
    {
        var baseline = new[] { B("a", Verdict.Pass), B("b", Verdict.Pass) };
        var first = TestDelta.Compare(baseline, [O("a", TestStatus.Failed), O("b", TestStatus.Failed)]);
        var second = TestDelta.Compare(baseline, [O("a", TestStatus.Passed), O("b", TestStatus.Failed)]);
        Assert.Equal(["b"], first.Confirmed(second).Regressions.Select(r => r.Id));
    }

    [Theory]
    [InlineData("src/a.py", "src/a.py")]
    [InlineData("/work/repo/src/a.py", "src/a.py")]
    [InlineData("./src/a.py", "src/a.py")]
    [InlineData("", ".")]
    public void Editor_paths_resolve_inside_the_repository(string input, string expected) => Assert.Equal(expected, ImplementTools.Resolve(input).Rel);

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("src/../../etc/passwd")]
    [InlineData(".git/config")]
    [InlineData("/work/home/.bashrc")]
    public void Editor_paths_outside_the_repository_or_into_git_are_refused(string input) => Assert.Null(ImplementTools.Resolve(input).Rel);

    [Fact]
    public void A_submission_accounts_for_every_requirement()
    {
        var s = new ImplementSubmission("fixed the slice", "Fix last CSV row", "body",
            [new RequirementStatus("R1", "done", "src/export/csv.py, tests/test_csv.py::test_last_row"), new RequirementStatus("R9", "not_done", "")]);
        var errors = ImplementTools.CheckSubmission(s, Prp());
        Assert.Contains(errors, e => e.Contains("R2 is missing"));
        Assert.Contains(errors, e => e.Contains("R9 is not a requirement"));
        Assert.Contains(errors, e => e.Contains("R9: not_done needs a note"));
    }

    [Fact]
    public void A_change_without_a_test_or_with_only_tests_is_sent_back()
    {
        Assert.Contains(ImplementTools.CheckChange([Change("src/export/csv.py")], Prp()), e => e.Contains("adds none"));
        Assert.Contains(ImplementTools.CheckChange([Change("tests/test_csv.py", ChangeKind.Added)], Prp()), e => e.Contains("only touches tests"));
        Assert.Empty(ImplementTools.CheckChange([Change("src/export/csv.py"), Change("tests/test_csv.py", ChangeKind.Added)], Prp()));
        Assert.Contains(ImplementTools.CheckChange([], Prp()), e => e.Contains("nothing changed"));
    }

    private static ReviewSubmission Review(string verdict, params ReviewFinding[] findings) => new(verdict, "does what the PRP says",
        [new RequirementVerdict("R1", "met", "tests/test_csv.py::test_last_row"), new RequirementVerdict("R2", "met", "no regressions")], findings);

    [Fact]
    public void A_clean_review_passes_its_checks() => Assert.Empty(ReviewTools.Check(Review("approve"), Prp(), Patch.Parse(Diff)));

    [Fact]
    public void Review_findings_are_held_to_the_prp_and_to_the_lines_the_change_adds()
    {
        var changes = Patch.Parse(Diff);
        var errors = ReviewTools.Check(Review("changes_requested",
            new ReviewFinding("major", "correctness", "src/export/csv.py", 40, "an old bug", "fix it"),
            new ReviewFinding("major", "R7", "src/export/csv.py", 4, "x", "y"),
            new ReviewFinding("major", "AP2", "src/export/csv.py", 4, "x", "y"),
            new ReviewFinding("major", "style", "src/export/csv.py", 4, "x", "y"),
            new ReviewFinding("major", "D1", "src/other.py", 1, "x", "y")), Prp(), changes);
        Assert.Contains(errors, e => e.Contains("line 40 is not one this change adds"));
        Assert.Contains(errors, e => e.Contains("R7 is not a requirement"));
        Assert.Contains(errors, e => e.Contains("AP2 doesn't exist"));
        Assert.Contains(errors, e => e.Contains("source 'style'"));
        Assert.Contains(errors, e => e.Contains("neither changed by the diff"));
        Assert.Empty(ReviewTools.Check(Review("changes_requested", new ReviewFinding("major", "AP1", "src/export/csv.py", 4, "appends a newline", "fix the slice")), Prp(), changes));
    }

    [Fact]
    public void The_verdict_must_agree_with_the_findings()
    {
        var changes = Patch.Parse(Diff);
        Assert.Contains(ReviewTools.Check(Review("approve", new ReviewFinding("blocker", "R1", "src/export/csv.py", 4, "x", "y")), Prp(), changes), e => e.Contains("verdict is approve"));
        Assert.Contains(ReviewTools.Check(Review("changes_requested", new ReviewFinding("minor", "R1", "src/export/csv.py", 4, "x", "y")), Prp(), changes), e => e.Contains("verdict is changes_requested"));
    }

    [Fact]
    public void Lint_commands_run_after_the_test_scripts_environment_lines()
    {
        var env = new TestEnvironment("python", "img", ". \"$HOME/venv/bin/activate\"\nexport X=1\npython -m pytest -q", new Dictionary<string, string>(), [], "vol");
        Assert.Equal(". \"$HOME/venv/bin/activate\"\nexport X=1\nruff check .", Verifier.LintScript(env, "ruff check ."));
    }
}
