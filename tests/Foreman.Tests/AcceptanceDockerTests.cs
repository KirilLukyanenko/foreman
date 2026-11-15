using System.Text.Json;
using Foreman.Core;
using Foreman.Implement;
using Foreman.Planning.Intake;
using Foreman.Planning.Spec;
using Foreman.Recon;
using Foreman.Recon.Environment;
using Foreman.Recon.Workspaces;
using Foreman.Sandbox;

namespace Foreman.Tests;

/// <summary>
/// The acceptance writer's tools and the verifier's hidden run against real Docker, without a model: on a tiny Python
/// repository whose discount starts one dollar too late. Needs Docker; pulls python:3.12-bookworm and installs pytest.
/// </summary>
[Trait("Category", "Docker")]
public class AcceptanceDockerTests
{
    private const string TestScript = ". \"$HOME/venv/bin/activate\"\npython -m pytest -q -p no:cacheprovider --junitxml=\"$OUT/pytest.xml\"";

    [Fact]
    public async Task Weak_tests_are_sent_back_strong_ones_kill_the_mutant_and_verify_runs_them()
    {
        if (!(await Proc.RunAsync("docker", ["info", "--format", "{{.ServerVersion}}"], timeout: TimeSpan.FromSeconds(20), ct: TestContext.Current.CancellationToken)).Ok) Assert.Skip("Docker is not running");
        var ct = TestContext.Current.CancellationToken;

        using var repo = new TempRepo(
            ("price.py", "def discount(total):\n    if total > 100:\n        return total * 0.9\n    return total\n"),
            ("test_price.py", "from price import discount\n\n\ndef test_small():\n    assert discount(10) == 10\n"));
        await Proc.GitAsync(repo.Root, "init", "-q");
        await Proc.GitAsync(repo.Root, "-c", "user.email=t@example.invalid", "-c", "user.name=t", "add", ".");
        await Proc.GitAsync(repo.Root, "-c", "user.email=t@example.invalid", "-c", "user.name=t", "commit", "-q", "-m", "init");
        var home = Path.Combine(Path.GetTempPath(), "foreman-tests", Guid.NewGuid().ToString("N"));
        var taskFile = Path.Combine(repo.Root, "..", "task-" + Path.GetFileName(repo.Root) + ".md");
        File.WriteAllText(taskFile, "orders of exactly 100 get no discount");
        var job = Job.Create(home, repo.Root, taskFile);
        try
        {
            await new FetchStage(new ReconOptions()).RunAsync(job, ct);
            await new TriageStage().RunAsync(job, ct);
            var spec = new EnvironmentSpec("python", "test", "python:3.12-bookworm",
                [new EnvStep("install", "python -m venv \"$HOME/venv\"\n. \"$HOME/venv/bin/activate\"\npip install --quiet pytest", Network: true, AsRoot: false, TimeoutMinutes: 10)],
                TestScript, new Dictionary<string, string>(), []);
            job.WriteJson("environment", "environment.json", new EnvironmentReport([new StackEnvironment("python", true, null, spec, null, null, null)]));
            await new BaselineStage(new ReconOptions(BaselineRuns: 1)).RunAsync(job, ct);

            job.WriteJson("intake", "brief.json", new Brief("en", "issue", "discount at 100", "100 or more gets 10% off", "bug", [], [new Criterion("discount(100) is 90", "100", true)], [], null, null, [], [], [], new FitEstimate("go", "low", [])));
            job.WriteJson("clarify", "questions.json", new List<Planning.Clarify.Gap>());
            var draft = new SpecDraft("Discount from 100", "Orders of 100 or more get 10% off.", ["the threshold is inclusive"],
                [new Requirement("R1", "When total is 100 or more, discount shall return total * 0.9.", ["C1"], [new Check("acceptance_test", "discount(100) == 90")])],
                [new InterfaceItem("discount", "price.py", "existing", "def discount(total)")], [new Example("E1", "total 100", "discount", "90", ["R1"])], ["x"], [],
                [new TouchPoint("price.py", ["discount"], ">= 100")], [], [new SpecTask("T1", "reproduce", "a failing test", "fails before")], ["y"], []);
            var prp = new Prp(job.Id, "abc", "Draft", DateTime.UtcNow, draft, [], [], [], []);
            job.WriteJson("spec", "prp.json", prp);
            job.WriteText("spec", "prp.md", prp.Markdown(job.Manifest.Source, "bug"));
            Ready.Mark(job);
            Directory.CreateDirectory(Path.Combine(job.Dir, "acceptance", "lab"));

            await using (var tools = new AcceptanceTools(job))
            {
                async Task<ToolOutput> Call(string name, object input) =>
                    await tools.CallAsync(name, JsonSerializer.SerializeToElement(input).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()), ct);

                // A reference fix and a weak test: 200 is discounted before the change too.
                Assert.StartsWith("exit 0", (await Call("lab_run", new { command = "sed -i 's/total > 100/total >= 100/' price.py && printf 'from price import discount\\n\\n\\ndef test_r1_large():\\n    assert discount(200) == 180\\n' > test_acceptance_price.py" })).Text);
                var weak = await Call("submit_tests", new { tests = new[] { new { match = "test_r1_large", requirement = "R1", expect_on_base = "fail" } }, reference_note = ">= 100" });
                Assert.True(weak.IsError, weak.Text);
                Assert.Contains("passes on the code before the change", weak.Text);

                // The boundary itself: fails before the change, passes on the reference, and kills the >= to > mutant.
                Assert.StartsWith("exit 0", (await Call("lab_run", new { command = "printf '\\n\\ndef test_r1_at_100():\\n    assert discount(100) == 90\\n' >> test_acceptance_price.py" })).Text);
                var strong = await Call("submit_tests", new
                {
                    tests = new[] { new { match = "test_r1_at_100", requirement = "R1", expect_on_base = "fail" }, new { match = "test_r1_large", requirement = "R1", expect_on_base = "pass" } },
                    reference_note = ">= 100",
                });
                Assert.False(strong.IsError, strong.Text);
                Assert.StartsWith("ACCEPTED", strong.Text);
            }
            var report = job.ReadJson<AcceptanceReport>("acceptance", "acceptance.json");
            Assert.Equal(["test_acceptance_price.py"], report.TestFiles);
            Assert.Equal(2, report.Tests.Count);
            var mutant = Assert.Single(report.Mutants);
            Assert.True(mutant.Killed);
            Assert.Contains("test_acceptance_price::test_r1_at_100", mutant.KilledBy);
            Assert.NotNull(AcceptanceStage.Hidden(job));

            // Verify runs the hidden tests next to a candidate's change: the right fix passes, an off-by-a-cent one doesn't.
            const string own = "diff --git a/test_discount.py b/test_discount.py\nnew file mode 100644\n--- /dev/null\n+++ b/test_discount.py\n@@ -0,0 +1,5 @@\n+from price import discount\n+\n+\n+def test_boundary():\n+    assert discount(100) == 90\n";
            const string right = "diff --git a/price.py b/price.py\n--- a/price.py\n+++ b/price.py\n@@ -1,4 +1,4 @@\n def discount(total):\n-    if total > 100:\n+    if total >= 100:\n         return total * 0.9\n     return total\n";
            var good = await new Verifier(null).RunAsync(job, 1, right + own, null, ct);
            Assert.True(good.Passed, string.Join("\n", good.Blockers));
            Assert.Equal(2, good.Hidden!.Passing.Count);
            Assert.Equal(["test_discount::test_boundary"], good.Repro!.Reproducing);

            // A change whose own test passes but that still leaves 100 undiscounted: only the hidden test catches it.
            var wrong = right.Replace("total >= 100", "total >= 100.5") + own.Replace("discount(100) == 90", "discount(101) == 90.9").Replace("test_boundary", "test_above");
            var bad = await new Verifier(null).RunAsync(job, 2, wrong, null, ct);
            Assert.False(bad.Passed);
            Assert.Equal(["test_acceptance_price::test_r1_at_100 (R1)"], bad.Hidden!.Failing);
            Assert.Contains(bad.Blockers, b => b.StartsWith("hidden acceptance tests: 1 of 2 fail", StringComparison.Ordinal));
        }
        finally
        {
            await new DockerSandbox(job.Id).RemoveAsync();
            await Lab.RemoveAsync(job, Lab.AcceptanceSuffix);
            await DockerSandbox.RemoveImageAsync($"foreman-env/foreman-{job.Id.ToLowerInvariant()}:python-install");
            Dirs.Delete(home);
        }
    }
}
