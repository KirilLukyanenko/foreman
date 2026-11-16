using Foreman.Agents;
using Foreman.Core;
using Foreman.Recon.Environment;
using Foreman.Recon.Triage;

namespace Foreman.Tests;

public class EnvironmentUnitTests
{
    [Theory]
    [InlineData("node:22-bookworm", true)]
    [InlineData("python:3.11-slim", true)]
    [InlineData("buildpack-deps:bookworm", true)]
    [InlineData("mcr.microsoft.com/dotnet/sdk:9.0", true)]
    [InlineData("node", false)]                          // no tag
    [InlineData("someuser/tools:1.0", false)]             // not an official image
    [InlineData("ghcr.io/x/y:1", false)]
    [InlineData("mcr.microsoft.com/mssql/server:2022", false)]
    [InlineData("node:22; rm -rf /", false)]
    public void Only_official_images_with_a_tag(string image, bool allowed) => Assert.Equal(allowed, EnvironmentTools.IsAllowedImage(image));

    [Fact]
    public void Long_output_keeps_the_head_and_the_tail()
    {
        var text = new string('a', 3_000) + new string('m', 10_000) + new string('z', 7_000);
        var clipped = EnvironmentTools.Clip(text);
        Assert.StartsWith(new string('a', 2_000), clipped);
        Assert.EndsWith(new string('z', 6_000), clipped);
        Assert.Contains("[12000 characters cut]", clipped);
        Assert.Equal("short", EnvironmentTools.Clip("short"));
    }

    [Fact]
    public void Tool_definitions_match_the_dispatcher()
    {
        Assert.Equal(["run", "use_image", "submit", "give_up"], EnvironmentTools.Definitions.Select(d => d.Name));
        foreach (var d in EnvironmentTools.Definitions)
            Assert.Equal("object", d.Schema.GetProperty("type").GetString());
    }

    [Fact]
    public void Prompt_is_embedded_filled_and_hashed()
    {
        var p = Prompts.Load("environment", new Dictionary<string, string> { ["max_tool_calls"] = "80" });
        Assert.Contains("You have 80 tool calls in total.", p.Text);
        Assert.DoesNotContain("{{", p.Text);
        Assert.Equal(64, p.Sha256.Length);
        Assert.Equal(p.Sha256, Prompts.Load("environment", new Dictionary<string, string> { ["max_tool_calls"] = "80" }).Sha256);
    }

    [Fact]
    public void Task_carries_triage_facts_and_the_failed_attempt()
    {
        var triage = new TriageReport("abc", new RepoSize(3, 10, false), [new("Go", 3)], null, null, null, ["go"],
            [new CiCommand(".github/workflows/ci.yml", "go test ./...")], [], [], [], [], new GitFacts("abc", 1, 0, null, 1));

        var task = EnvironmentStage.Task(triage, "go", "buildpack-deps:bookworm", "Foreman has no deterministic plan.");

        Assert.Contains("`go` part", task);
        Assert.Contains("go test ./...", task);
        Assert.Contains("Foreman has no deterministic plan.", task);
    }

    [Fact]
    public void Claude_code_stream_gives_turns_cost_usage_and_tool_calls()
    {
        const string stream = """
            {"type":"system","subtype":"init"}
            {"type":"assistant","message":{"content":[{"type":"text","text":"Looking."},{"type":"tool_use","name":"mcp__foreman__run"}]}}
            {"type":"user","message":{"content":[{"type":"tool_result"}]}}
            {"type":"assistant","message":{"content":[{"type":"tool_use","name":"mcp__foreman__run"},{"type":"tool_use","name":"mcp__foreman__submit"}]}}
            not json
            {"type":"result","subtype":"success","num_turns":4,"total_cost_usd":0.4321,"usage":{"input_tokens":10,"output_tokens":2000,"cache_read_input_tokens":50000,"cache_creation_input_tokens":8000}}
            """;

        var s = ClaudeCodeDriver.ParseStream(stream);

        Assert.Equal(4, s.Turns);
        Assert.Equal(3, s.ToolCalls);
        Assert.Equal(0.4321m, s.CostUsd);
        Assert.Equal(new TokenUsage(10, 2000, 50000, 8000), s.Usage);
        Assert.Equal("success", s.Subtype);
    }

    [Fact]
    public void Pricing_counts_cache_reads_and_writes()
    {
        // Opus 5.5: $4 in, $20 out, $0.20 cache read, cache write 1.25x input.
        Assert.Equal(4m + 20m + 0.20m + 5m, Pricing.Cost("claude-opus-5-5", new TokenUsage(1_000_000, 1_000_000, 1_000_000, 1_000_000)));
        Assert.Null(Pricing.Cost("unknown-model", TokenUsage.Zero));
    }
}

/// <summary>
/// The environment tools against real Docker, without a model: the calls a model would make, in order.
/// Needs Docker and pulls python:3.12-bookworm on first run.
/// </summary>
[Trait("Category", "Docker")]
public class EnvironmentToolsDockerTests
{
    [Fact]
    public async Task Run_then_failed_submit_then_accepted_submit()
    {
        if (!await DockerCheck.AvailableAsync()) Assert.Skip("Docker is not running");

        using var repo = new TempRepo(
            ("calc.py", "def add(a, b):\n    return a + b\n"),
            ("test_calc.py", "from calc import add\n\ndef test_add():\n    assert add(2, 3) == 5\n\ndef test_add_negative():\n    assert add(-1, 1) == 0\n"),
            (".claude/settings.json", "{\"hooks\": {}}"));
        await Proc.GitAsync(repo.Root, "init", "-q");
        await Proc.GitAsync(repo.Root, "-c", "user.email=t@example.invalid", "-c", "user.name=t", "add", ".");
        await Proc.GitAsync(repo.Root, "-c", "user.email=t@example.invalid", "-c", "user.name=t", "commit", "-q", "-m", "init");

        var home = Path.Combine(Path.GetTempPath(), "foreman-tests", Guid.NewGuid().ToString("N"));
        var job = Job.Create(home, repo.Root, null);
        await new Foreman.Recon.FetchStage(new Foreman.Recon.ReconOptions()).RunAsync(job, TestContext.Current.CancellationToken);

        await using var tools = new EnvironmentTools(job, "python", [".claude"]);
        await tools.StartAsync("python:3.12-bookworm");

        var run = await tools.RunAsync("ls -a && test ! -e .claude && echo no-agent-config", 60, TestContext.Current.CancellationToken);
        Assert.Contains("calc.py", run.Text);
        Assert.Contains("no-agent-config", run.Text);

        // No report written: verification must say so, not accept.
        var noReport = await tools.SubmitAsync("python:3.12-bookworm", "pip install --quiet pytest", "python -m pytest -q", null, TestContext.Current.CancellationToken);
        Assert.True(noReport.IsError);
        Assert.Contains("no test report", noReport.Text);
        Assert.False(tools.Finished);

        var refused = await tools.UseImageAsync("someuser/evil:1", TestContext.Current.CancellationToken);
        Assert.True(refused.IsError);

        var ok = await tools.SubmitAsync("python:3.12-bookworm", "pip install --quiet pytest", "python -m pytest -q --junitxml=\"$OUT/pytest.xml\"", null, TestContext.Current.CancellationToken);
        Assert.False(ok.IsError, ok.Text);
        Assert.StartsWith("ACCEPTED: 2 tests", ok.Text);
        Assert.True(tools.Finished);

        var saved = job.ReadJson<AgentResult>("environment/python", "agent-result.json");
        Assert.Equal("agent", saved.Accepted!.Source);
        Assert.Equal(2, saved.Validation!.Passed);
        Assert.Equal(4, File.ReadAllLines(Path.Combine(job.Dir, "environment", "python", "tools.jsonl")).Length);

        var after = await tools.RunAsync("echo late", 10, TestContext.Current.CancellationToken);
        Assert.True(after.IsError);
    }

}
