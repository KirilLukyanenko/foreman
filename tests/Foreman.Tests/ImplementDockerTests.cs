using System.Text.Json;
using Foreman.Recon.Workspaces;
using Foreman.Core;
using Foreman.Implement;
using Foreman.Planning.Intake;
using Foreman.Planning.Spec;
using Foreman.Recon;
using Foreman.Recon.Environment;
using Foreman.Sandbox;

namespace Foreman.Tests;

public class FetchLineEndingTests
{
    [Fact]
    public async Task The_clone_keeps_line_endings_as_committed()
    {
        using var repo = new TempRepo(("run.sh", "#!/bin/sh\necho ok\n"));
        await Proc.GitAsync(repo.Root, "init", "-q");
        await Proc.GitAsync(repo.Root, "-c", "core.autocrlf=false", "-c", "user.email=t@example.invalid", "-c", "user.name=t", "add", ".");
        await Proc.GitAsync(repo.Root, "-c", "user.email=t@example.invalid", "-c", "user.name=t", "commit", "-q", "-m", "init");
        var home = Path.Combine(Path.GetTempPath(), "foreman-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var job = Job.Create(home, repo.Root, null);
            await new FetchStage(new ReconOptions()).RunAsync(job, TestContext.Current.CancellationToken);
            Assert.Equal("#!/bin/sh\necho ok\n", File.ReadAllText(Path.Combine(job.SourceDir, "run.sh")));
        }
        finally
        {
            Dirs.Delete(home);
        }
    }
}

/// <summary>
/// The implementer's tools and the verifier against real Docker, without a model: the calls a model would make, in
/// order, on a tiny Python repository with a bug. Needs Docker; pulls python:3.12-bookworm and installs pytest.
/// </summary>
[Trait("Category", "Docker")]
public class ImplementDockerTests
{
    private const string TestScript = ". \"$HOME/venv/bin/activate\"\npython -m pytest -q -p no:cacheprovider --junitxml=\"$OUT/pytest.xml\"";

    [Fact]
    public async Task Tools_then_submit_then_the_verifier_passes_the_fix()
    {
        if (!(await Proc.RunAsync("docker", ["info", "--format", "{{.ServerVersion}}"], timeout: TimeSpan.FromSeconds(20), ct: TestContext.Current.CancellationToken)).Ok) Assert.Skip("Docker is not running");
        var ct = TestContext.Current.CancellationToken;

        using var repo = new TempRepo(
            ("calc.py", "def add(a, b):\n    return a + b\n\n\ndef clamp(x, lo, hi):\n    return max(lo, min(x, lo))\n"),
            ("test_calc.py", "from calc import add\n\n\ndef test_add():\n    assert add(2, 3) == 5\n"));
        await Proc.GitAsync(repo.Root, "init", "-q");
        await Proc.GitAsync(repo.Root, "-c", "user.email=t@example.invalid", "-c", "user.name=t", "add", ".");
        await Proc.GitAsync(repo.Root, "-c", "user.email=t@example.invalid", "-c", "user.name=t", "commit", "-q", "-m", "init");
        var home = Path.Combine(Path.GetTempPath(), "foreman-tests", Guid.NewGuid().ToString("N"));
        File.WriteAllText(Path.Combine(repo.Root, "..", "task-" + Path.GetFileName(repo.Root) + ".md"), "clamp returns lo for values above it");
        var job = Job.Create(home, repo.Root, Path.Combine(repo.Root, "..", "task-" + Path.GetFileName(repo.Root) + ".md"));
        var impl = new DockerSandbox(job.Id, suffix: "impl");
        try
        {
            // Recon, as far as implementing needs it: the clone, triage, a known environment and its baseline.
            await new FetchStage(new ReconOptions()).RunAsync(job, ct);
            await new TriageStage().RunAsync(job, ct);
            var spec = new EnvironmentSpec("python", "test", "python:3.12-bookworm",
                [new EnvStep("install", "python -m venv \"$HOME/venv\"\n. \"$HOME/venv/bin/activate\"\npip install --quiet pytest", Network: true, AsRoot: false, TimeoutMinutes: 10)],
                TestScript, new Dictionary<string, string>(), []);
            job.WriteJson("environment", "environment.json", new EnvironmentReport([new StackEnvironment("python", true, null, spec, null, null, null)]));
            await new BaselineStage(new ReconOptions(BaselineRuns: 1)).RunAsync(job, ct);

            // A Ready PRP.
            job.WriteJson("intake", "brief.json", new Brief("en", "issue", "clamp is wrong", "clamp bounds x", "bug", [], [new Criterion("clamp(5, 0, 3) is 3", "clamp", true)], [], null, null, [], [], [], new FitEstimate("go", "low", [])));
            var draft = new SpecDraft("Fix clamp", "clamp(x, lo, hi) returns x bounded to [lo, hi].", ["it returns lo"],
                [new Requirement("R1", "When x > hi, clamp shall return hi.", ["C1"], [new Check("acceptance_test", "clamp(5, 0, 3) == 3")])],
                [], [], [], [], [new TouchPoint("calc.py", ["clamp"], "use hi")], [], [new SpecTask("T1", "reproduce", "a failing test", "fails before")], [], []);
            var prp = new Prp(job.Id, "abc", "Draft", DateTime.UtcNow, draft, [], [], [], []);
            job.WriteJson("spec", "prp.json", prp);
            job.WriteText("spec", "prp.md", prp.Markdown(job.Manifest.Source, "bug"));
            Ready.Mark(job);

            // The workspace the stage would prepare.
            var env = TestEnvironment.Of(job);
            await Workspace.CreateAsync(impl, env);
            string start;
            await using (var ws = await Workspace.OpenAsync(impl.Volume, env)) start = await ws.MarkStartAsync(ct);
            job.WriteJson("implement", "round-1/context.json", new ImplementContext(1, impl.Volume, start));

            await using var tools = new ImplementTools(job, 1);
            async Task<ToolOutput> Call(string name, object input) =>
                await tools.CallAsync(name, JsonSerializer.SerializeToElement(input).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()), ct);

            var who = await Call("bash", new { command = "id -u; python3 -c \"import socket; socket.create_connection(('1.1.1.1', 53), 2)\" 2>&1 | tail -1" });
            Assert.StartsWith("exit 0", who.Text);
            Assert.Contains("1000", who.Text);
            Assert.Contains("Error", who.Text); // no network

            Assert.True((await Call("edit", new { command = "view", path = "../etc/passwd" })).IsError);
            Assert.Contains("def clamp", (await Call("edit", new { command = "view", path = "calc.py" })).Text);
            Assert.False((await Call("edit", new { command = "create", path = "test_clamp.py", file_text = "from calc import clamp\n\n\ndef test_clamp_above():\n    assert clamp(5, 0, 3) == 3\n" })).IsError);

            var account = new { summary = "clamp used lo twice", pr_title = "Fix clamp's upper bound", pr_description = "clamp now uses hi.", requirements = new[] { new { id = "R1", status = "done", where = "calc.py, test_clamp.py" } } };
            var early = await Call("submit", account);
            Assert.True(early.IsError);
            Assert.Contains("new test(s) fail", early.Text);

            Assert.True((await Call("edit", new { command = "str_replace", path = "calc.py", old_str = "return", new_str = "return" })).IsError); // not unique
            Assert.False((await Call("edit", new { command = "str_replace", path = "calc.py", old_str = "min(x, lo)", new_str = "min(x, hi)" })).IsError);
            var tests = await Call("run_tests", new { });
            Assert.Contains("No regressions", tests.Text);
            Assert.Contains("1 pass, 0 fail", tests.Text);

            var accepted = await Call("submit", account);
            Assert.False(accepted.IsError, accepted.Text);
            Assert.StartsWith("ACCEPTED", accepted.Text);

            string patch;
            await using (var ws = await Workspace.OpenAsync(impl.Volume, env)) patch = await ws.DiffAsync(start, ct);
            Assert.Equal(["calc.py", "test_clamp.py"], Patch.Paths(patch));

            var report = await new Verifier(null).RunAsync(job, 1, patch, tools.Result!.Submission, ct);
            Assert.True(report.Passed, string.Join("\n", report.Blockers));
            Assert.Equal(["test_clamp::test_clamp_above"], report.Repro!.Reproducing);
            Assert.Empty(report.Tests!.Regressions);
        }
        finally
        {
            await impl.RemoveAsync();
            await new DockerSandbox(job.Id).RemoveAsync();
            foreach (var image in new[] { $"foreman-env/foreman-{job.Id.ToLowerInvariant()}:python-install" }) await DockerSandbox.RemoveImageAsync(image);
            Dirs.Delete(home);
        }
    }
}
