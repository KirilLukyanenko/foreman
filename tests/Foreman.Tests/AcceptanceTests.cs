using Foreman.Implement;
using Foreman.Planning.Spec;

namespace Foreman.Tests;

public class AcceptanceTests
{
    private const string TwoHunks = """
        diff --git a/src/price.py b/src/price.py
        index 1111111..2222222 100644
        --- a/src/price.py
        +++ b/src/price.py
        @@ -1,4 +1,4 @@
         def discount(total):
        -    if total > 100:
        +    if total >= 100:
                 return total * 0.9
             return total
        @@ -20,3 +20,3 @@ def fee(total):
         def free_shipping(total):
        -    return False
        +    return total == 0 or total >= 50
        diff --git a/tests/test_price.py b/tests/test_price.py
        index 3333333..4444444 100644
        --- a/tests/test_price.py
        +++ b/tests/test_price.py
        @@ -1,2 +1,3 @@
         from price import discount
        +assert discount(100) == 90

        """;

    [Fact]
    public void Mutants_leave_out_a_hunk_or_flip_an_operator_and_never_touch_tests()
    {
        var mutants = Mutants.Of(TwoHunks.Replace("\r\n", "\n"), max: 6);
        Assert.Equal(["partial", "partial", "operator", "operator"], mutants.Select(m => m.Kind));
        Assert.All(mutants, m => Assert.DoesNotContain("discount(100) == 90", m.Patch.Replace("+assert discount(100) == 90", "")));
        Assert.DoesNotContain("if total >= 100:", mutants[0].Patch);
        Assert.Contains("+    if total > 100:", mutants[2].Patch);
        Assert.Contains("+    return total != 0 or total >= 50", mutants[3].Patch);
        Assert.Contains("tests/test_price.py", mutants[2].Patch);
    }

    [Theory]
    [InlineData("    if a == b:", "    if a != b:")]
    [InlineData("    return x and y", "    return x or y")]
    [InlineData("    flag = True", "    flag = False")]
    [InlineData("    n = 1", "    n = 0")]
    [InlineData("    s = \"a == b\"", null)]
    [InlineData("    List<int> xs = new();", null)]
    public void One_operator_per_line_outside_strings(string line, string? mutated) =>
        Assert.Equal(mutated, Mutants.Mutate(line)?.Text);

    [Fact]
    public void A_single_hunk_fix_has_no_partial_mutant_and_docs_are_not_mutated()
    {
        const string fix = "diff --git a/CHANGES.md b/CHANGES.md\n--- a/CHANGES.md\n+++ b/CHANGES.md\n@@ -1 +1,2 @@\n x\n+- fixed == bug\n" +
                           "diff --git a/a.py b/a.py\n--- a/a.py\n+++ b/a.py\n@@ -1 +1 @@\n-    if x > 1:\n+    if x >= 1:\n";
        var m = Assert.Single(Mutants.Of(fix));
        Assert.Equal("operator", m.Kind);
        Assert.Contains("CHANGES.md", m.Patch);
    }

    [Fact]
    public void Select_keeps_whole_file_blocks()
    {
        var tests = Patch.Select(TwoHunks.Replace("\r\n", "\n"), p => p.StartsWith("tests/", StringComparison.Ordinal));
        Assert.Equal(["tests/test_price.py"], Patch.Paths(tests));
        Assert.Equal(["src/price.py"], Patch.Paths(Patch.Select(TwoHunks.Replace("\r\n", "\n"), p => p.StartsWith("src/", StringComparison.Ordinal))));
    }

    private static Prp PrpWith(params Requirement[] requirements) => new("job", "abc", "Ready", DateTime.UtcNow,
        new SpecDraft("t", "g", ["w"], requirements, [], [], ["o"], [], [], [], [], ["a"], []), [], [], [], []);

    private static readonly Requirement R1 = new("R1", "rule", ["task"], [new Check("acceptance_test", "discount(100) is 90")]);
    private static readonly Requirement R2 = new("R2", "kept", ["task"], [new Check("acceptance_test", "discount(50) is 50")]);

    private static FileChange File(string path, ChangeKind kind = ChangeKind.Added) => new(path, kind, false, [], []);

    [Fact]
    public void The_shape_needs_a_new_acceptance_file_a_reference_fix_and_a_test_per_requirement()
    {
        var ok = new AcceptanceSubmission([new("test_r1", "R1", "fail"), new("test_r2", "R2", "pass")], "fix");
        Assert.Empty(AcceptanceTools.CheckShape(ok, [File("tests/test_acceptance_price.py"), File("src/price.py", ChangeKind.Modified)], PrpWith(R1, R2), "bug"));

        var errors = AcceptanceTools.CheckShape(new AcceptanceSubmission([new("test_r2", "R2", "pass"), new("x", "R9", "maybe")], "fix"),
            [File("tests/test_price.py", ChangeKind.Modified)], PrpWith(R1, R2), "bug");
        Assert.Contains(errors, e => e.Contains("no acceptance test file", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("tests/test_price.py: leave existing", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("no reference fix", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("R9 doesn't exist", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("expect_on_base must be", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("R1 is checked by an acceptance test", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("no test is expected to fail", StringComparison.Ordinal));
    }

    [Fact]
    public void Measured_tests_must_behave_as_declared_on_the_base_and_pass_on_the_reference()
    {
        var hidden = new[] { "tests.test_acceptance_price::test_r1_at_100", "tests.test_acceptance_price::test_r2_below", "tests.test_acceptance_price::test_extra" };
        var onBase = new Dictionary<string, string> { [hidden[0]] = "passed", [hidden[1]] = "failed" };
        var onRef = new Dictionary<string, string> { [hidden[0]] = "passed", [hidden[1]] = "passed", [hidden[2]] = "failed" };
        var measured = AcceptanceTools.Measure([new("test_r1", "R1", "fail"), new("test_r2", "R2", "pass"), new("test_nope", "R1", "fail")], hidden, onBase, onRef, out var errors);
        Assert.Equal(2, measured.Count);
        Assert.Contains(errors, e => e.Contains("test_r1_at_100 (R1) passes on the code before the change", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("test_r2_below (R2) is declared to pass before the change but is failed", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("'test_nope' (R1) matches no test", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("test_extra is in your acceptance file but in no declaration", StringComparison.Ordinal));
    }

    [Fact]
    public void The_mutation_score_skips_stillborn_and_equivalent_mutants_and_needs_two()
    {
        Assert.Null(AcceptanceTools.Score([new("M1", "operator", "d", true, ["t"])]));
        Assert.Equal(0.5, AcceptanceTools.Score([
            new("M1", "operator", "d", true, ["t"]), new("M2", "operator", "d", false, []), new("M3", "partial", "d", null, []),
            new("M4", "operator", "d", false, [], "x >= hi and x > hi both return hi")]));
    }

    [Theory]
    [InlineData("tests/test_acceptance_price.py", true)]
    [InlineData("src/router/files.acceptance.test.ts", true)]
    [InlineData("test/App.Tests/AcceptanceTests.cs", true)]
    [InlineData("tests/test_price.py", false)]
    [InlineData("src/acceptance.py", false)]
    public void Hidden_test_files_are_test_files_named_acceptance(string path, bool hidden) =>
        Assert.Equal(hidden, AcceptanceTools.IsHiddenTestFile(path));
}
