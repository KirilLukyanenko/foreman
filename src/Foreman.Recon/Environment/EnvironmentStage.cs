using System.Text;
using System.Text.Json;
using Foreman.Core;
using Foreman.Recon.Baseline;
using Foreman.Recon.Triage;
using Foreman.Sandbox;

namespace Foreman.Recon.Environment;

/// <summary>
/// Makes each stack build and run its tests, proven in a fresh copy. The deterministic plan goes first because it is
/// free and fast; the agent is called only for what the plan can't do, so simple repositories cost nothing.
/// </summary>
public sealed class EnvironmentStage(ReconOptions options, IAgentDriver? driver, Action<string>? log = null, string? model = null, decimal? maxBudgetUsd = null) : IStage
{
    /// <summary>Starting image for stacks the planner doesn't know: Debian with git, curl and compilers.</summary>
    public const string GenericImage = "buildpack-deps:bookworm";

    public string Name => "environment";

    public async Task RunAsync(Job job, CancellationToken ct)
    {
        var triage = job.ReadJson<TriageReport>("triage", "triage.json");
        await DockerSandbox.EnsureDockerAsync();

        var results = new List<StackEnvironment>();
        var plans = BaselinePlanner.Plan(triage);
        foreach (var plan in plans)
            results.Add(await ForPlanAsync(job, triage, plan, ct));

        // Stacks with no deterministic plan at all go straight to the agent.
        if (plans.Count == 0)
        {
            var stack = triage.UnsupportedStacks.FirstOrDefault() ?? "unknown";
            results.Add(await WithAgentAsync(job, triage, stack, GenericImage, deterministic: null, $"Foreman has no deterministic plan for this repository (detected: {(triage.UnsupportedStacks.Count == 0 ? "no known build files" : string.Join(", ", triage.UnsupportedStacks))}).", ct));
        }

        job.WriteJson(Name, "environment.json", new EnvironmentReport(results));
        if (!results.Any(r => r.Ready))
            throw new InvalidOperationException("no stack has a working environment: " + string.Join("; ", results.Select(r => $"{r.Stack}: {r.Reason}")));
    }

    private async Task<StackEnvironment> ForPlanAsync(Job job, TriageReport triage, StackPlan plan, CancellationToken ct)
    {
        if (plan.Unsupported is not null)
            return await WithAgentAsync(job, triage, plan.Stack, plan.Image, null, $"The deterministic planner doesn't handle this: {plan.Unsupported}.", ct);
        if (plan.TestScript is null)
            return await WithAgentAsync(job, triage, plan.Stack, plan.Image, null, $"The deterministic planner found no test command. Notes: {string.Join("; ", plan.Notes)}", ct);

        var spec = new EnvironmentSpec(plan.Stack, "deterministic", plan.Image,
            plan.Setup.Select(s => new EnvStep(s.Name, s.Script, s.Network == Network.Open, AsRoot: false, (int)s.Timeout.TotalMinutes)).ToList(),
            plan.TestScript, plan.Env, plan.Notes);

        log?.Invoke($"  {plan.Stack}: deterministic plan on {plan.Image}");
        var validator = new EnvironmentValidator(job, triage.RemovedAgentConfig, options.Limits);
        var v = await validator.ValidateAsync(spec, Path.Combine(job.StageDir(Path.Combine(Name, plan.Stack)), "deterministic"), ct);
        job.Event(Name, "deterministic", new { stack = plan.Stack, v.Ok, v.Reason, v.Tests, v.Passed });
        // Same bar as the agent's: per-test results, and at least half of them passing. A plan that runs but
        // fails most tests is far more likely to be missing something than to have found a broken project.
        if (v.Ok && v.PassRate >= EnvironmentTools.MinPassRateWithoutReason)
        {
            log?.Invoke($"  {plan.Stack}: works without an agent ({v.Tests} tests, {v.Passed} passed)");
            return new StackEnvironment(plan.Stack, true, null, spec, v, v, null);
        }

        var why = v.Ok ? $"only {v.Passed} of {v.Tests} tests passed ({v.Failed} failed, {v.Skipped} skipped)" : v.Reason;
        log?.Invoke($"  {plan.Stack}: deterministic plan not good enough: {why}");
        var context = $"""
            Foreman's deterministic plan was not good enough: {why}.
            Image: {spec.Image}
            Setup steps:
            {string.Join("\n", spec.Setup.Select(s => $"--- {s.Name} (network {(s.Network ? "on" : "off")})\n{s.Script}"))}
            Test script:
            {spec.TestScript}
            Last output:
            {v.LogTail}
            """;
        return await WithAgentAsync(job, triage, plan.Stack, plan.Image, v, context, ct);
    }

    private async Task<StackEnvironment> WithAgentAsync(Job job, TriageReport triage, string stack, string image, Validation? deterministic, string context, CancellationToken ct)
    {
        if (driver is null)
            return new StackEnvironment(stack, false, "needs the environment agent, which is not enabled (pass --agent)", null, deterministic, null, null);

        var prompt = Prompts.Load("environment", new Dictionary<string, string> { ["max_tool_calls"] = EnvironmentTools.MaxToolCalls.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        var dir = job.StageDir(Path.Combine(Name, stack));
        var resultFile = Path.Combine(dir, "agent-result.json");
        if (File.Exists(resultFile)) File.Delete(resultFile);
        var request = new AgentRequest(
            Agent: "environment",
            Job: job,
            System: prompt,
            Task: Task(triage, stack, image, context),
            WorkDir: dir,
            Toolbox: () => new EnvironmentTools(job, stack, triage.RemovedAgentConfig, null, options.Limits, image),
            McpArgs: ["environment", "--job", job.Id, "--stack", stack, "--image", image],
            Model: model,
            MaxBudgetUsd: maxBudgetUsd,
            ReadOutcome: () => EnvironmentTools.OutcomeOf(ReadResult(resultFile)),
            // Claude Code ends its MCP server without a graceful shutdown, so the server can't clean up after itself.
            Cleanup: async () =>
            {
                var scratch = new DockerSandbox(job.Id, options.Limits, $"agent-{stack}");
                await Proc.RunAsync("docker", ["rm", "-f", $"{scratch.Volume}-session"], timeout: TimeSpan.FromMinutes(1));
                await scratch.RemoveAsync();
            });
        File.WriteAllText(Path.Combine(dir, "task.md"), request.Task, new UTF8Encoding(false));

        // Pull before the agent starts, so its first tool call doesn't spend minutes on a download.
        await DockerSandbox.EnsureImageAsync(image);
        log?.Invoke($"  {stack}: environment agent ({driver.Name}, {model ?? driver.Model}) starting on {image}");
        var summary = await driver.RunAsync(request, ct);
        job.Event(Name, "agent", summary);
        log?.Invoke($"  {stack}: agent finished: {summary.Outcome}, {summary.ToolCalls} tool calls, {summary.Turns} turns, ${summary.CostUsd:0.00}{(summary.CostIsEstimate ? " (estimate)" : "")}, {summary.Seconds:0} s");

        var result = ReadResult(resultFile);
        if (result?.Accepted is { } spec)
            return new StackEnvironment(stack, true, null, spec, deterministic, result.Validation, summary);
        var reason = result?.GaveUp is { } g ? $"the agent gave up: {g}" : $"the agent finished without an accepted environment ({summary.Outcome}{(summary.Error is null ? "" : $": {summary.Error}")})";
        return new StackEnvironment(stack, false, reason, null, deterministic, null, summary);
    }

    private static AgentResult? ReadResult(string file) => File.Exists(file) ? Json.Read<AgentResult>(File.ReadAllText(file)) : null;

    /// <summary>The first user message: the stack, what triage found, and what was tried. All of it is data.</summary>
    internal static string Task(TriageReport triage, string stack, string image, string context)
    {
        var facts = new
        {
            stack,
            startImage = image,
            triage.Languages,
            triage.Dotnet,
            triage.Node,
            triage.Python,
            triage.UnsupportedStacks,
            ciCommands = triage.CiCommands.Take(40),
            triage.EnvironmentFiles,
            instructionFiles = triage.InstructionFiles.Select(f => f.Path),
            installScripts = triage.InstallScripts,
        };
        return $"""
            Set up the environment for the `{stack}` part of this repository. The sandbox starts on `{image}`.

            {context}

            What Foreman's triage found (from the repository's files, nothing executed):
            ```json
            {JsonSerializer.Serialize(facts, Json.Options)}
            ```
            """;
    }
}
