using Foreman.Agents;
using Foreman.Core;
using Foreman.Evals;
using Foreman.Planning.Clarify;
using Foreman.Planning.Intake;
using Foreman.Recon;
using Foreman.Recon.Baseline;
using Foreman.Recon.CodeMap;
using Foreman.Recon.Dossier;
using Foreman.Recon.Security;
using Foreman.Recon.Environment;
using Foreman.Recon.Triage;

// foreman recon <repo path or git url> [--task file.md] [--ref branch | --commit sha] [--stages a,b] [--runs 3] [--agent auto|claude-code|api|none]
//               [--model id] [--effort low|medium|high] [--budget usd] [--home dir]
// foreman recon --job <id> [--force stage,stage]       resume or rerun stages of an existing job
// foreman answer <id> [--set Q1=text ...] [--defaults]   record the client's answers to clarify's questions
// foreman show <id>                                     summary of a job's recon
// foreman mcp environment --job <id> --stack <s> --image <i>  MCP server for an agent's toolbox (stdio)
// foreman mcp reader --job <id> --section <name>

var home = Arg("--home") ?? Environment.GetEnvironmentVariable("FOREMAN_HOME")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Foreman");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

switch (args.FirstOrDefault())
{
    case "recon":
    case "run":
        return await Recon();
    case "answer":
        return Answer(args.ElementAtOrDefault(1));
    case "eval":
        return await Eval();
    case "show":
        return Show(args.ElementAtOrDefault(1));
    case "mcp":
        // stdout carries the MCP protocol from here on: nothing else may be written to it.
        await ToolboxMcpServer.RunAsync(Toolbox(args.ElementAtOrDefault(1)));
        return 0;
    default:
        Console.Error.WriteLine("""
            usage:
              foreman recon <repo path or git url> [--task file.md] [--ref branch | --commit sha] [--stages a,b] [--runs 3]
                            [--agent auto|claude-code|api|none] [--model id] [--effort low|medium|high] [--budget usd]
                            [--reader-model id] [--sections architecture,conventions,task,risks]
                            [--intake-model id] [--clarify-model id] [--go]
              foreman recon --job <id> [--force intake,go,fetch,triage,security,codemap,environment,baseline,dossier,clarify]
              foreman answer <job id> [--set Q1="..."]... [--defaults]
              foreman show <job id>
              foreman eval intake [--runs 3] [--only id,id] [--agent claude-code|api] [--intake-model id]
              foreman eval clarify [--runs 3] [--only id,id] [--agent claude-code|api] [--clarify-model id]
              foreman eval localization [--cases evals/localization/cases.json] [--out evals/localization] [--force]
                                    [--readers [--agent claude-code|api] [--reader-model id]] [--only id,id]
            """);
        return 2;
}

async Task<int> Recon()
{
    var options = new ReconOptions(
        BaselineRuns: int.TryParse(Arg("--runs"), out var n) && n > 0 ? n : 3,
        Ref: Arg("--ref"),
        Commit: Arg("--commit"));

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
    Console.WriteLine($"job {job.Id}\n    {job.Dir}\n    agents: {(driver is null ? "off" : $"{driver.Name}; environment on {Arg("--model") ?? driver.Model}, readers on {Arg("--reader-model") ?? DossierOptions.DefaultModel}")}");

    var force = Arg("--force")?.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
    var hasTask = job.Manifest.TaskFile is not null;
    IStage[] stages =
    [
        // Intake and the go/no-go gate need no repository: they run first, before recon spends anything.
        .. hasTask ? new IStage[] { new IntakeStage(driver, new IntakeOptions(Arg("--intake-model") ?? IntakeOptions.DefaultModel), Console.WriteLine), new GoStage(args.Contains("--go"), Console.WriteLine) } : [],
        new FetchStage(options),
        new TriageStage(),
        new SecurityStage(options.Limits, Console.WriteLine),
        new CodeMapStage(null, options.Limits),
        new EnvironmentStage(options, driver, Console.WriteLine, Arg("--model")),
        new BaselineStage(options, Console.WriteLine),
        new DossierStage(driver, new DossierOptions(Arg("--reader-model") ?? DossierOptions.DefaultModel,
            Sections: Arg("--sections")?.Split(',', StringSplitOptions.RemoveEmptyEntries)), Console.WriteLine),
        .. hasTask ? new IStage[] { new ClarifyStage(driver, new ClarifyOptions(Arg("--clarify-model") ?? ClarifyOptions.DefaultModel), Console.WriteLine) } : [],
    ];
    // --stages runs only the named stages (their inputs must exist), e.g. fetch,triage,codemap for an evaluation.
    if (Arg("--stages")?.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal) is { } only)
        stages = stages.Where(s => only.Contains(s.Name)).ToArray();
    var pipeline = new Pipeline(stages);
    var ok = await pipeline.RunAsync(job, force, Console.WriteLine, cts.Token);
    Show(job.Id);
    return ok ? 0 : 1;
}

async Task<int> Eval()
{
    if (args.ElementAtOrDefault(1) == "intake") return await EvalIntake();
    if (args.ElementAtOrDefault(1) == "clarify") return await EvalClarify();
    if (args.ElementAtOrDefault(1) != "localization") { Console.Error.WriteLine("eval: 'localization', 'intake' or 'clarify'"); return 2; }
    var casesFile = Arg("--cases") ?? Path.Combine("evals", "localization", "cases.json");
    var outDir = Arg("--out") ?? Path.GetDirectoryName(casesFile)!;
    var cases = LocalizationEval.ReadCases(casesFile);
    if (Arg("--only") is { } only) cases = cases.Where(c => only.Split(',').Contains(c.Id)).ToList();
    var eval = new LocalizationEval(home, log: Console.WriteLine, reader: args.Contains("--readers") ? Driver() : null, readerModel: Arg("--reader-model"));
    var report = await eval.RunAsync(cases, args.Contains("--force"), cts.Token);
    File.WriteAllText(Path.Combine(outDir, "results.json"), Json.Write(report));
    var md = LocalizationEval.Markdown(report, cases);
    File.WriteAllText(Path.Combine(outDir, "results.md"), md);
    Console.WriteLine();
    Console.WriteLine(md);
    return 0;
}

async Task<int> EvalIntake()
{
    var casesFile = Arg("--cases") ?? Path.Combine("evals", "intake", "cases.json");
    var outDir = Arg("--out") ?? Path.GetDirectoryName(casesFile)!;
    var cases = IntakeEval.ReadCases(casesFile);
    if (Arg("--only") is { } only) cases = cases.Where(c => only.Split(',').Contains(c.Id)).ToList();
    var driver = Driver() ?? throw new InvalidOperationException("eval intake needs a model");
    var runs = int.TryParse(Arg("--runs"), out var n) && n > 0 ? n : 1;
    var report = await new IntakeEval(home, driver, Arg("--intake-model"), Console.WriteLine).RunAsync(cases, casesFile, runs, cts.Token);
    File.WriteAllText(Path.Combine(outDir, "results.json"), Json.Write(report));
    var md = IntakeEval.Markdown(report, cases);
    File.WriteAllText(Path.Combine(outDir, "results.md"), md);
    Console.WriteLine();
    Console.WriteLine(md);
    return 0;
}

async Task<int> EvalClarify()
{
    var casesFile = Arg("--cases") ?? Path.Combine("evals", "clarify", "cases.json");
    var outDir = Arg("--out") ?? Path.GetDirectoryName(casesFile)!;
    var cases = ClarifyEval.ReadCases(casesFile);
    if (Arg("--only") is { } only) cases = cases.Where(c => only.Split(',').Contains(c.Id)).ToList();
    var driver = Driver() ?? throw new InvalidOperationException("eval clarify needs a model");
    var runs = int.TryParse(Arg("--runs"), out var n) && n > 0 ? n : 1;
    var report = await new ClarifyEval(home, driver, Arg("--clarify-model"), Console.WriteLine).RunAsync(cases, casesFile, runs, cts.Token);
    File.WriteAllText(Path.Combine(outDir, "results.json"), Json.Write(report));
    var md = ClarifyEval.Markdown(report, cases);
    File.WriteAllText(Path.Combine(outDir, "results.md"), md);
    Console.WriteLine();
    Console.WriteLine(md);
    return 0;
}

IToolbox Toolbox(string? kind)
{
    var job = Job.Open(home, Required("--job"));
    return kind switch
    {
        "environment" => new EnvironmentTools(job, Required("--stack"), job.ReadJson<TriageReport>("triage", "triage.json").RemovedAgentConfig, startImage: Required("--image")),
        "reader" => new ReaderTools(job, Required("--section")),
        "intake" => new IntakeTools(job),
        "clarify" => new ClarifyTools(job, job.ReadJson<Brief>("intake", "brief.json").Language),
        _ => throw new ArgumentException($"unknown toolbox '{kind}'"),
    };
}

IAgentDriver? Driver()
{
    var model = ApiDriver.DefaultModel;
    var budget = decimal.TryParse(Arg("--budget"), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var b) ? b : 10m;
    var kind = Arg("--agent") ?? "auto";
    if (kind == "auto")
        kind = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") is { Length: > 0 } ? "api" : "claude-code";
    return kind switch
    {
        "api" => new ApiDriver(model, Arg("--effort") ?? "medium"),
        "claude-code" => new ClaudeCodeDriver(model, budget),
        _ => null,
    };
}

int Answer(string? id)
{
    if (id is null) { Console.Error.WriteLine("answer: job id required"); return 2; }
    var job = Job.Open(home, id);
    var given = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i] != "--set") continue;
        var eq = args[i + 1].IndexOf('=');
        if (eq <= 0) { Console.Error.WriteLine($"answer: --set needs Q1=text, got '{args[i + 1]}'"); return 2; }
        given[args[i + 1][..eq].Trim()] = args[i + 1][(eq + 1)..].Trim();
    }
    try
    {
        foreach (var a in Answers.Record(job, given, args.Contains("--defaults")))
            Console.WriteLine($"{a.Id} ({a.Source}): {a.Value}");
        return 0;
    }
    catch (InvalidOperationException e)
    {
        Console.Error.WriteLine($"answer: {e.Message}");
        return 1;
    }
}

int Show(string? id)
{
    if (id is null) { Console.Error.WriteLine("show: job id required"); return 2; }
    var job = Job.Open(home, id);
    Console.WriteLine();
    foreach (var (stage, state) in job.ReadState())
        Console.WriteLine($"{stage,-12} {state.Status}{(state.Error is null ? "" : $": {state.Error}")}");

    if (File.Exists(Path.Combine(job.Dir, "intake", "brief.json")))
    {
        var b = job.ReadJson<Brief>("intake", "brief.json");
        var r = job.ReadJson<IntakeReport>("intake", "intake.json");
        Console.WriteLine($"""

            intake      {b.Title}
                        {b.TaskType}, {b.Kind.Replace('_', ' ')}, client language {b.Language}; {b.Fit.Verdict.Replace('_', '-')}, {b.Fit.Risk} risk
                        {b.Deliverables.Count} deliverables, {b.Acceptance.Count(c => c.Explicit)} explicit and {b.Acceptance.Count(c => !c.Explicit)} implied acceptance criteria, {b.Unknowns.Count} unknowns, {b.EmbeddedInstructions.Count(e => e.Audience != "none")} embedded instructions
                        {r.Agent.Model} via {r.Agent.Driver}, {r.Submissions} submissions{(r.Warnings.Count > 0 ? $", {r.Warnings.Count} warnings" : "")}, ${r.Agent.CostUsd:0.000}{(r.Agent.CostIsEstimate ? " (estimate)" : "")}, {r.Agent.Seconds:0} s
            """);
        foreach (var reason in b.Fit.Reasons.Take(4)) Console.WriteLine($"            - {reason}");
    }
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
    if (File.Exists(Path.Combine(job.Dir, "security", "security.json")))
    {
        var x = job.ReadJson<SecurityReport>("security", "security.json");
        Console.WriteLine($"security    secrets: {x.SecretsInFiles} in files, {x.Secrets.Count - x.SecretsInFiles} only in history ({x.HistoryCommits} commits); vulnerable packages: {x.Vulnerabilities.Select(v => (v.Package, v.Version)).Distinct().Count()} ({x.Vulnerabilities.Count} advisories); SAST ({x.SastFilesScanned} files): {x.Sast.Count(f => !f.InTestPath && SecurityStage.SeverityOrder(f.Severity) < 2)} in code, {x.Sast.Count(f => SecurityStage.SeverityOrder(f.Severity) == 2)} informational, {x.Sast.Count(f => f.InTestPath)} in tests");
        foreach (var w in x.Warnings.Take(3)) Console.WriteLine($"            {w}");
        if (x.RemovedIgnoreFiles.Count > 0) Console.WriteLine($"            ignored the repository's own allowlists: {string.Join(", ", x.RemovedIgnoreFiles)}");
        foreach (var t in x.Tools.Where(t => t.Error is not null)) Console.WriteLine($"            {t.Tool} FAILED: {t.Error}");
    }
    if (File.Exists(Path.Combine(job.Dir, "codemap", "codemap.json")))
    {
        var m = job.ReadJson<CodeMapReport>("codemap", "codemap.json");
        Console.WriteLine($"codemap     {m.FilesParsed} files, {m.Definitions} definitions ({string.Join(", ", m.Languages.Select(l => $"{l.Language} {l.Files}"))}); {m.Generated.Count} generated, {m.Vendored.Count} vendored left out");
        Console.WriteLine($"            map {m.General.Tokens} tokens{(m.Task is { } t ? $", task map {t.Tokens} tokens" : "")}; {string.Join(", ", m.Extractors.Select(x => $"{x.Key} {x.Value}"))}");
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
    if (File.Exists(Path.Combine(job.Dir, "dossier", "dossier.json")))
    {
        var d = job.ReadJson<DossierReport>("dossier", "dossier.json");
        Console.WriteLine($"dossier     {d.DossierTokens} tokens, rules file {d.RulesTokens} tokens; {d.Seconds:0} s, ${d.CostUsd:0.00}{(d.CostIsEstimate ? " (estimate)" : "")}");
        foreach (var r in d.Readers)
            Console.WriteLine($"            {r.Section,-13} {(r.Accepted ? "accepted" : "MISSING")}, {r.Words} words, {r.Agent.ToolCalls} tool calls{(r.Warnings > 0 ? $", {r.Warnings} warnings" : "")}");
        foreach (var x in d.Skipped) Console.WriteLine($"            skipped {x}");
    }
    if (File.Exists(Path.Combine(job.Dir, "clarify", "clarify.json")))
    {
        var c = job.ReadJson<ClarifyReport>("clarify", "clarify.json");
        var answered = File.Exists(Path.Combine(job.Dir, "clarify", "answers.json"));
        Console.WriteLine($"clarify     {c.Verdict}: {c.Questions} questions, {c.Assumptions} assumptions, {c.FromRepository} answered by the code{(c.HadDossier ? "" : " (no dossier)")}; {c.Agent.ToolCalls} tool calls, ${c.Agent.CostUsd:0.00}{(c.Agent.CostIsEstimate ? " (estimate)" : "")}, {c.Agent.Seconds:0} s");
        Console.WriteLine($"            {(c.Questions == 0 ? "nothing to send" : answered ? "answers recorded" : $"waiting for answers: send clarify/questions.md, then foreman answer {job.Id}")}");
    }
    return 0;
}

string? Arg(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

string Required(string name) => Arg(name) ?? throw new ArgumentException($"{name} is required");
