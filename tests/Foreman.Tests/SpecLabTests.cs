using Foreman.Planning.Spec;
using Foreman.Recon.Reports;
using Foreman.Recon.Workspaces;

namespace Foreman.Tests;

public class SpecLabTests
{
    private static SpecDraft Draft(params AllowedEdit[] edits) => new(
        "Lazy sha1", "goal", ["why"], [new Requirement("R1", "rule", ["task"], [new Check("acceptance_test", "how")])], [], [], ["x"], [], [], [], [], ["y"], edits);

    private static TestRun Run(IReadOnlyList<TestChange>? regressions = null, IReadOnlyList<string>? newPassing = null, IReadOnlyList<string>? newFailing = null) =>
        new([new TestOutcome("t", TestStatus.Passed, null)],
            new TestDelta(10, 9, 1, 0, regressions ?? [], newPassing ?? ["tests.test_signer::test_lazy"], newFailing ?? [], [], [], 0), 1, false, 3, "");

    private const string Pinned = "tests.test_itsdangerous.test_serializer::TestSerializer::test_iter_unsigners";

    [Fact]
    public void A_task_that_changes_no_behaviour_needs_no_sketch() =>
        Assert.Empty(SpecTools.CheckLab(Draft(), "docs", null, []));

    [Fact]
    public void A_bug_needs_a_sketch_run_against_the_suite()
    {
        Assert.Contains("no sketch", Assert.Single(SpecTools.CheckLab(Draft(), "bug", null, [])));
        Assert.Contains("no sketch", Assert.Single(SpecTools.CheckLab(Draft(), "feature", Run(), [])));
        Assert.Empty(SpecTools.CheckLab(Draft(), "bug", Run(), ["src/itsdangerous/signer.py"]));
    }

    [Fact]
    public void A_test_the_sketch_breaks_must_be_an_allowed_edit()
    {
        var run = Run(regressions: [new TestChange(Pinned, "failed")]);
        var error = Assert.Single(SpecTools.CheckLab(Draft(), "bug", run, ["src/itsdangerous/signer.py"]));
        Assert.Contains(Pinned, error);
        Assert.Empty(SpecTools.CheckLab(Draft(new AllowedEdit("tests/test_itsdangerous/test_serializer.py", "the default digest is now _lazy_sha1")), "bug", run, ["x"]));
    }

    [Fact]
    public void New_tests_that_fail_with_the_sketch_and_a_sketch_without_tests_are_sent_back()
    {
        Assert.Contains("can't hold", Assert.Single(SpecTools.CheckLab(Draft(), "bug", Run(newFailing: ["tests/test_ansi.py::test_spans"]), ["x"])));
        Assert.Contains("adds no test", Assert.Single(SpecTools.CheckLab(Draft(), "bug", Run(newPassing: []), ["x"])));
    }

    [Theory]
    [InlineData("tests/test_itsdangerous/test_serializer.py", "", Pinned)]
    [InlineData("src/router/trie-router/router.test.ts", "", "src/router/trie-router/router.test.ts::TrieRouter > Common > case")]
    [InlineData("test/GuardClauses.UnitTests/GuardAgainstExpression.cs", "", "guardclauses.unittests::GuardClauses.UnitTests.GuardAgainstExpression.ThrowsGivenFalse")]
    [InlineData("tests/test_itsdangerous/test_serializer.py", "test_iter_unsigners now expects _lazy_sha1", "tests.test_itsdangerous.test_timed::TestTimedSerializer::test_iter_unsigners")]
    public void An_allowed_edit_covers_the_tests_of_its_file_or_the_ones_it_names(string path, string reason, string testId) =>
        Assert.True(SpecTools.Accounts([new AllowedEdit(path, reason)], testId));

    [Fact]
    public void An_allowed_edit_doesnt_cover_other_files() =>
        Assert.False(SpecTools.Accounts([new AllowedEdit("tests/test_signer.py", "new cases")], Pinned));

    [Theory]
    [InlineData(Pinned, "test_iter_unsigners")]
    [InlineData("src/a.test.ts::Router > Common > GET /files/", "GET /files/")]
    [InlineData("lib.tests::Lib.Tests.Guard.Throws(value: 1)", "Throws")]
    public void The_leaf_is_the_tests_own_name(string id, string leaf) => Assert.Equal(leaf, SpecTools.Leaf(id));

    [Fact]
    public void Sketch_files_come_from_the_diff_headers() =>
        Assert.Equal(["src/a.py", "tests/test a.py"], Lab.SketchFiles("diff --git a/src/a.py b/src/a.py\n+x\ndiff --git a/tests/test a.py b/tests/test a.py\n"));
}
