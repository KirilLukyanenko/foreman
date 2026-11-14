using Foreman.Agents;
using Foreman.Core;
using Foreman.Recon;
using Foreman.Recon.Baseline;
using Foreman.Recon.Environment;
using Foreman.Recon.Triage;

// foreman recon <repo path or git url> [--task file.md] [--ref branch] [--runs 3] [--agent auto|claude-code|api|none]
//               [--model id] [--effort low|medium|high] [--budget usd] [--home dir]
// foreman recon --job <id> [--force stage,stage]       resume or rerun stages of an existing job
// foreman show <id>                                     summary of a job's recon
// foreman mcp-environment --job <id> --stack <s> --image <i>   MCP server for the environment agent (stdio)

var home = Arg("--home") ?? Environment.GetEnvironmentVariable("FOREMAN_HOME")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Foreman");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

switch (args.FirstOrDefault())
{
    case "recon":
        return await Recon();
    case "show":
        return Show(args.ElementAtOrDefault(1));
    case "mcp-environment":
        // stdout carries the MCP protocol from here on: nothing else may be written to it.
        await EnvironmentMcpServer.RunAsync(home, Required("--job"), Required("--stack"), Required("--image"));
        return 0;
    default:
        Console.Error.WriteLine("""
            usage:
              foreman recon <repo path or git url> [--task file.md] [--ref branch] [--runs 3]
                            [--agent auto|claude-code|api|none] [--model id] [--effort low|medium|high] [--budget usd]
              foreman recon --job <id> [--force fetch,triage,environment,baseline]
              foreman show <job id>
            """);
        return 2;
}

async Task<int> Recon()
{
    var options = new ReconOptions(
        BaselineRuns: int.TryParse(Arg("--runs"), out var n) && n > 0 ? n : 3,
        Ref: Arg("--ref"));

    Job job;
    if (Arg("--job") is { } id)
    {
        job = Job.Open(home, id);
    }
    else
    {
        var source = args.ElementAtOrDefault(1);
        if (source is null || source.StartsWith("--", StringComparison.Ordinal)) { Console.Error.WriteLine("recon: repository path or URL required"); return 2; }
        job = Job.Create(home, source, Arg("--task"));
    }
    var driver = Driver();
    Console.WriteLine($"job {job.Id}\n    {job.Dir}\n    environment agent: {(driver is null ? "off" : $"{driver.Name}, {driver.Model}")}");

    var force = Arg("--force")?.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
    var pipeline = new Pipeline([
        new FetchStage(options),
        new TriageStage(),
        new EnvironmentStage(options, driver, Console.WriteLine),
        new BaselineStage(options, Console.WriteLine),
    ]);
    var ok = await pipeline.RunAsync(job, force, Console.WriteLine, cts.Token);
    Show(job.Id);
    return ok ? 0 : 1;
}

IEnvironmentDriver? Driver()
{
    var model = Arg("--model") ?? ApiEnvironmentDriver.DefaultModel;
    var budget = decimal.TryParse(Arg("--budget"), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var b) ? b : 10m;
    var kind = Arg("--agent") ?? "auto";
    if (kind == "auto")
        kind = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") is { Length: > 0 } ? "api" : "claude-code";
    return kind switch
    {
        "api" => new ApiEnvironmentDriver(model, Arg("--effort") ?? "medium"),
        "claude-code" => new ClaudeCodeEnvironmentDriver(model, budget),
        _ => null,
    };
}

int Show(string? id)
{
    if (id is null) { Console.Error.WriteLine("show: job id required"); return 2; }
    var job = Job.Open(home, id);
    Console.WriteLine();
    foreach (var (stage, state) in job.ReadState())
        Console.WriteLine($"{stage,-12} {state.Status}{(state.Error is null ? "" : $": {state.Error}")}");

    if (File.Exists(Path.Combine(job.Dir, "triage", "triage.json")))
    {
        var t = job.ReadJson<TriageReport>("triage", "triage.json");
        Console.WriteLine($"""

            triage      {t.Size.Files} files, {string.Join(", ", t.Languages.Take(4).Select(l => $"{l.Language} {l.Files}"))}
                        stacks: {string.Join(", ", new[] { t.Dotnet is null ? null : $".NET ({t.Dotnet.TestProjects.Count} test projects, {t.Dotnet.TestRunner})", t.Node is null ? null : $"Node ({t.Node.PackageManager}, {t.Node.TestFramework ?? "no runner"})", t.Python is null ? null : $"Python ({t.Python.PackageManager}, {t.Python.TestFramework ?? "no tests"})" }.Where(s => s is not null).Concat(t.UnsupportedStacks.Select(u => u + " (no plan)")))}
                        CI commands: {t.CiCommands.Count}, instruction files: {t.InstructionFiles.Count} ({t.InstructionFiles.Sum(f => f.HiddenCharacters)} hidden characters), install scripts: {t.InstallScripts.Count}
                        agent config removed from the sandbox copy: {(t.RemovedAgentConfig.Count == 0 ? "none" : string.Join(", ", t.RemovedAgentConfig))}
            """);
    }
    if (File.Exists(Path.Combine(job.Dir, "environment", "environment.json")))
    {
        var e = job.ReadJson<EnvironmentReport>("environment", "environment.json");
        foreach (var s in e.Stacks)
        {
            var how = s.Spec is null ? s.Reason : $"{s.Spec.Source} on {s.Spec.Image}";
            var agent = s.Agent is { } a ? $"\n                        agent: {a.Driver} {a.Model}, {a.Outcome}, {a.ToolCalls} tool calls, {a.Turns} turns, ${a.CostUsd:0.00}{(a.CostIsEstimate ? " (estimate)" : "")}, {a.Seconds:0} s" : "";
            Console.WriteLine($"environment {s.Stack,-7} {(s.Ready ? "ready" : "NOT READY")}: {how}{agent}");
        }
    }
    if (File.Exists(Path.Combine(job.Dir, "baseline", "baseline.json")))
    {
        var b = job.ReadJson<BaselineReport>("baseline", "baseline.json");
        Console.WriteLine($"baseline    {b.Runs} runs, network off");
        foreach (var s in b.Stacks)
        {
            var steps = string.Join(", ", s.Steps.Concat(s.TestRuns).Select(x => $"{x.Name} {x.Seconds:0}s{(x.ExitCode == 0 ? "" : $" exit {x.ExitCode}")}"));
            var counts = s.Counts is { } c ? $"{c.Total} tests: {c.Pass} pass, {c.KnownRed} known-red, {c.Flaky} flaky, {c.Skipped} skipped" : s.Reason ?? "";
            Console.WriteLine($"            {s.Stack,-7} {s.Status}: {counts}\n                    {s.Image} · {steps}");
        }
    }
    return 0;
}

string? Arg(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

string Required(string name) => Arg(name) ?? throw new ArgumentException($"{name} is required");
