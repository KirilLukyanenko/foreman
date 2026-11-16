using System.Diagnostics;
using System.Text;
using Foreman.Core;
using Foreman.Planning.Spec;
using Foreman.Recon.Workspaces;

namespace Foreman.Implement;

public sealed record AcceptanceOptions(string Model = AcceptanceOptions.DefaultModel, decimal MaxBudgetUsd = 6m)
{
    public const string DefaultModel = "claude-opus-5-5";
}

/// <summary>
/// Stage 4, between the Ready PRP and the implementer: the hidden acceptance tests. A separate session sees the PRP and
/// the code before the change, never the implementer's work, and writes black-box tests for the PRP's acceptance-tested
/// requirements next to a reference fix of its own. Code tests the tests (<see cref="AcceptanceTools"/>): new
/// behaviour fails before the change, everything passes on the reference, and broken versions of the reference are
/// caught. The tests go to <c>acceptance/tests.diff</c> with their checksum; Verify adds them to every candidate's
/// fresh copy. The implementer never sees them; a failing one comes back to it as the requirement and the test's name.
/// </summary>
public sealed class AcceptanceStage(IAgentDriver? driver, AcceptanceOptions? options = null, Action<string>? log = null) : IStage
{
    private readonly AcceptanceOptions _options = options ?? new AcceptanceOptions();

    public string Name => "accept";

    public async Task RunAsync(Job job, CancellationToken ct)
    {
        if (Ready.Check(job) is { } notReady) throw new JobWaitingException("ready", notReady);
        var prp = job.ReadJson<Prp>("spec", "prp.json");
        var dir = Path.Combine(job.Dir, "acceptance");
        Dirs.Delete(dir);
        Directory.CreateDirectory(Path.Combine(dir, "lab"));
        if (!prp.Spec.Requirements.Any(r => r.Verify.Any(v => v.Kind == "acceptance_test")))
        {
            job.WriteJson("acceptance", "acceptance.json", new AcceptanceReport("none", "the PRP has no acceptance-tested requirement", [], null, [], [], null, [], [], 0, 0));
            log?.Invoke("  accept: the PRP has no acceptance-tested requirement; nothing to write");
            return;
        }
        if (driver is null) throw new InvalidOperationException("the acceptance tests need a model: agents are off");
        TestEnvironment.Of(job);

        var clock = Stopwatch.StartNew();
        var message = FirstMessage(job);
        File.WriteAllText(Path.Combine(dir, "task.md"), message, new UTF8Encoding(false));
        var request = new AgentRequest(
            Agent: "acceptance",
            Job: job,
            System: Prompts.Load("acceptance", new Dictionary<string, string>
            {
                ["max_tool_calls"] = AcceptanceTools.MaxToolCalls.ToString(System.Globalization.CultureInfo.InvariantCulture),
            }),
            Task: message,
            WorkDir: dir,
            Toolbox: () => new AcceptanceTools(job),
            McpArgs: ["accept", "--job", job.Id],
            Model: _options.Model,
            MaxBudgetUsd: _options.MaxBudgetUsd,
            ReadOutcome: () => File.Exists(AcceptanceTools.ResultFile(job)) ? Json.Read<AcceptanceReport>(File.ReadAllText(AcceptanceTools.ResultFile(job)))?.Status : null,
            // Claude Code ends the MCP server without a graceful shutdown: the lab is removed here.
            Cleanup: () => Lab.RemoveAsync(job, Lab.AcceptanceSuffix));

        log?.Invoke($"  accept: {driver.Name}, {_options.Model}");
        AgentRunSummary summary;
        try { summary = await driver.RunAsync(request, ct); }
        finally { await Lab.RemoveAsync(job, Lab.AcceptanceSuffix); }
        job.Event(Name, "agent", new { summary });

        if (!File.Exists(AcceptanceTools.ResultFile(job)))
            throw new InvalidOperationException($"the acceptance test writer delivered nothing: {summary.Outcome}{(summary.Error is null ? "" : $" ({summary.Error})")}");
        var report = Json.Read<AcceptanceReport>(File.ReadAllText(AcceptanceTools.ResultFile(job)))!;
        job.WriteJson("acceptance", "summary.json", new { report.Status, tests = report.Tests.Count, report.MutationScore, warnings = report.Warnings.Count, agent = summary, seconds = Math.Round(clock.Elapsed.TotalSeconds, 1) });
        job.Event(Name, "written", new { report.Status, tests = report.Tests.Count, report.MutationScore, warnings = report.Warnings.Count });
        if (report.Status == "gave_up") throw new InvalidOperationException($"the acceptance test writer stopped: {report.Reason}");
        var killed = report.Mutants.Count(m => m.Killed == true);
        var valid = report.Mutants.Count(m => m.Killed is not null);
        log?.Invoke($"  accept: {report.Tests.Count} hidden test(s) for {report.Tests.Select(t => t.Requirement).Distinct().Count()} requirement(s), " +
                    $"{report.Tests.Count(t => t.ExpectOnBase == "fail")} fail before the change; mutants killed {killed}/{valid}" +
                    $"{(report.Warnings.Count > 0 ? $"; {report.Warnings.Count} warning(s)" : "")}; {summary.ToolCalls} tool calls, ${summary.CostUsd:0.00}{(summary.CostIsEstimate ? " (estimate)" : "")}, {summary.Seconds:0} s");
    }

    /// <summary>The hidden tests to add to a candidate's copy, or null if there are none; throws if they changed since they were measured.</summary>
    public static (string Diff, AcceptanceReport Report)? Hidden(Job job)
    {
        if (!File.Exists(AcceptanceTools.ResultFile(job))) return null;
        var report = Json.Read<AcceptanceReport>(File.ReadAllText(AcceptanceTools.ResultFile(job)))!;
        if (report.Status != "ready" || report.Tests.Count == 0) return null;
        var diff = File.ReadAllText(Path.Combine(job.Dir, "acceptance", "tests.diff"));
        if (AcceptanceTools.Sha256(diff) != report.TestsSha256)
            throw new InvalidOperationException("acceptance/tests.diff changed after it was measured: rerun the accept stage");
        return (diff, report);
    }

    internal static string FirstMessage(Job job)
    {
        var sb = new StringBuilder();
        sb.Append("Write the hidden acceptance tests for this PRP, with a reference fix of your own, and submit them with `submit_tests`.\n\n");
        sb.Append("## The PRP (Ready: the contract)\n\n");
        sb.Append(Demote(File.ReadAllText(Path.Combine(job.Dir, "spec", "prp.md")))).Append("\n\n");
        var rules = Path.Combine(job.Dir, "dossier", "AGENTS.md");
        if (File.Exists(rules)) sb.Append("## The repository's rules file (from recon)\n\n").Append(Demote(File.ReadAllText(rules))).Append("\n\n");
        var env = TestEnvironment.Of(job);
        sb.Append($"## The test command (verified by recon; `lab_run_tests` runs it)\n\n```bash\n{env.TestScript.Trim()}\n```\n");
        return sb.ToString();
    }

    private static string Demote(string markdown) =>
        string.Join('\n', markdown.Replace("\r\n", "\n").Split('\n').Select(l => l.StartsWith('#') ? "#" + l : l));
}
