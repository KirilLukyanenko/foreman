using Foreman.Agents;
using Foreman.Core;
using Foreman.Evals;
using Foreman.Implement;
using Foreman.Planning.Clarify;
using Foreman.Planning.Intake;
using Foreman.Planning.Spec;
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
// foreman verify <id> [--round n] [--no-review]          rerun verification of implement/patch.diff
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
    case "ready":
        return MarkReady(args.ElementAtOrDefault(1));
    case "verify":
        return await Verify(args.ElementAtOrDefault(1));
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
                            [--intake-model id] [--clarify-model id] [--spec-model id] [--go]
                            [--acceptance-model id] [--no-acceptance] [--implement-model id] [--review-model id] [--rounds 3] [--candidates 1] [--no-review]
              foreman recon --job <id> [--force intake,go,fetch,triage,security,codemap,environment,baseline,dossier,clarify,spec,accept,implement]
              foreman answer <job id> [--set Q1="..."]... [--defaults]
              foreman ready <job id>                      mark the PRP (spec/prp.md) as reviewed and approved
              foreman verify <job id> [--round n] [--candidate k] [--no-review]   verify implement/patch.diff again
              foreman show <job id>
              foreman eval intake [--runs 3] [--only id,id] [--agent claude-code|api] [--intake-model id]
              foreman eval clarify [--runs 3] [--only id,id] [--agent claude-code|api] [--clarify-model id]
              foreman eval spec [--runs 1] [--only id,id] [--agent claude-code|api] [--spec-model id]
              foreman eval implement [--only id,id] [--runs 1] [--label name] [--reuse-planning] [--keep-spec] [--no-acceptance] [--no-review]
                                     [--candidates 1] [--implement-model id] [--review-model id] [--rounds 3]
              foreman eval compare <results-a.json> <results-b.json> [--out file.md]
              foreman eval redteam [--only id,id]          prompt injection planted in a repository and a task
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
        .. hasTask ? TaskStages() : [],
    ];
    IStage[] TaskStages() =>
    [
        new ClarifyStage(driver, new ClarifyOptions(Arg("--clarify-model") ?? ClarifyOptions.DefaultModel), Console.WriteLine),
        // Waits (fails with the reason) until the client's answers are recorded; rerun the job after 'foreman answer'.
        new SpecStage(driver, new SpecOptions(Arg("--spec-model") ?? SpecOptions.DefaultModel), Console.WriteLine),
        // Waits (fails with the reason) until the human marks the PRP Ready with 'foreman ready'.
        .. args.Contains("--no-acceptance") ? Array.Empty<IStage>() : [new AcceptanceStage(driver, AcceptanceOptionsFromArgs(), Console.WriteLine)],
        new ImplementStage(driver, ImplementOptionsFromArgs(), Console.WriteLine),
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
    if (args.ElementAtOrDefault(1) == "spec") return await EvalSpec();
    if (args.ElementAtOrDefault(1) == "implement") return await EvalImplement();
    if (args.ElementAtOrDefault(1) == "compare") return EvalCompare();
    if (args.ElementAtOrDefault(1) == "redteam") return await EvalRedTeam();
    if (args.ElementAtOrDefault(1) != "localization") { Console.Error.WriteLine("eval: 'localization', 'intake', 'clarify', 'spec' or 'implement'"); return 2; }
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

async Task<int> EvalSpec()
{
    var casesFile = Arg("--cases") ?? Path.Combine("evals", "spec", "cases.json");
    var outDir = Arg("--out") ?? Path.GetDirectoryName(casesFile)!;
    var cases = SpecEval.ReadCases(casesFile);
    if (Arg("--only") is { } only) cases = cases.Where(c => only.Split(',').Contains(c.Id)).ToList();
    var driver = Driver() ?? throw new InvalidOperationException("eval spec needs a model");
    var runs = int.TryParse(Arg("--runs"), out var n) && n > 0 ? n : 1;
    var report = await new SpecEval(home, driver, Arg("--spec-model"), Console.WriteLine).RunAsync(cases, casesFile, runs, cts.Token);
    File.WriteAllText(Path.Combine(outDir, "results.json"), Json.Write(report));
    var md = SpecEval.Markdown(report, cases);
    File.WriteAllText(Path.Combine(outDir, "results.md"), md);
    Console.WriteLine();
    Console.WriteLine(md);
    return 0;
}

async Task<int> EvalImplement()
{
    var casesFile = Arg("--cases") ?? Path.Combine("evals", "implement", "cases.json");
    var outDir = Arg("--out") ?? Path.GetDirectoryName(casesFile)!;
    var cases = ImplementEval.ReadCases(casesFile);
    if (Arg("--only") is { } only) cases = cases.Where(c => only.Split(',').Contains(c.Id)).ToList();
    var driver = Driver() ?? throw new InvalidOperationException("eval implement needs a model");
    var runs = int.TryParse(Arg("--runs"), out var n) && n > 0 ? n : 1;
    var label = Arg("--label");
    var report = await new ImplementEval(home, driver, ImplementOptionsFromArgs(), Console.WriteLine, args.Contains("--keep-spec"),
        args.Contains("--no-acceptance") ? null : AcceptanceOptionsFromArgs(), runs, args.Contains("--reuse-planning"), label).RunAsync(cases, casesFile, cts.Token);
    var name = (label is null ? "results" : $"results-{label}") + (Arg("--only") is null ? "" : "-partial");
    File.WriteAllText(Path.Combine(outDir, name + ".json"), Json.Write(report));
    var md = ImplementEval.Markdown(report);
    File.WriteAllText(Path.Combine(outDir, name + ".md"), md);
    Console.WriteLine();
    Console.WriteLine(md);
    return 0;
}

async Task<int> EvalRedTeam()
{
    var casesFile = Arg("--cases") ?? Path.Combine("evals", "redteam", "cases.json");
    var outDir = Arg("--out") ?? Path.GetDirectoryName(casesFile)!;
    var cases = RedTeamEval.ReadCases(casesFile);
    if (Arg("--only") is { } only) cases = cases.Where(c => only.Split(',').Contains(c.Id)).ToList();
    var driver = Driver() ?? throw new InvalidOperationException("eval redteam needs a model");
    var report = await new RedTeamEval(home, driver, Console.WriteLine).RunAsync(cases, casesFile, cts.Token);
    var name = Arg("--only") is null ? "results" : "results-partial";
    File.WriteAllText(Path.Combine(outDir, name + ".json"), Json.Write(report));
    var md = RedTeamEval.Markdown(report, cases);
    File.WriteAllText(Path.Combine(outDir, name + ".md"), md);
    Console.WriteLine();
    Console.WriteLine(md);
    return 0;
}

int EvalCompare()
{
    if (args.ElementAtOrDefault(2) is not { } a || args.ElementAtOrDefault(3) is not { } b) { Console.Error.WriteLine("eval compare: two results files"); return 2; }
    var md = ImplementEval.Compare(Json.Read<ImplementEvalReport>(File.ReadAllText(a))!, Json.Read<ImplementEvalReport>(File.ReadAllText(b))!);
    if (Arg("--out") is { } outFile) File.WriteAllText(outFile, md);
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
        "spec" => new SpecTools(job),
        "accept" => new AcceptanceTools(job),
        "implement" => new ImplementTools(job, AttemptFromArgs()),
        "review" => new ReviewTools(job, AttemptFromArgs()),
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

Attempt AttemptFromArgs() => new(int.Parse(Required("--round"), System.Globalization.CultureInfo.InvariantCulture),
    int.TryParse(Arg("--candidate"), out var c) && c > 0 ? c : 0);

AcceptanceOptions AcceptanceOptionsFromArgs() => new(Arg("--acceptance-model") ?? AcceptanceOptions.DefaultModel);

ImplementOptions ImplementOptionsFromArgs() => new(
    Model: Arg("--implement-model") ?? ImplementOptions.DefaultModel,
    MaxRounds: int.TryParse(Arg("--rounds"), out var rounds) && rounds > 0 ? rounds : 3,
    Review: new ReviewOptions(Arg("--review-model") ?? ReviewOptions.DefaultModel),
    SkipReview: args.Contains("--no-review"),
    Candidates: int.TryParse(Arg("--candidates"), out var n) && n > 0 ? n : 1,
    SkipAcceptance: args.Contains("--no-acceptance"));

async Task<int> Verify(string? id)
{
    if (id is null) { Console.Error.WriteLine("verify: job id required"); return 2; }
    var job = Job.Open(home, id);
    var patchFile = Path.Combine(job.Dir, "implement", "patch.diff");
    if (!File.Exists(patchFile)) { Console.Error.WriteLine("verify: no implement/patch.diff; run the implement stage first"); return 1; }
    var options = ImplementOptionsFromArgs();
    var round = new Attempt(int.TryParse(Arg("--round"), out var r) && r > 0 ? r : 1, int.TryParse(Arg("--candidate"), out var c) && c > 0 ? c : 0);
    var resultFile = ImplementTools.ResultFile(job, round);
    var submission = File.Exists(resultFile) ? Json.Read<ImplementResult>(File.ReadAllText(resultFile))?.Submission : null;
    var verifier = new Verifier(options.SkipReview ? null : Driver(), options.Review, null, Console.WriteLine);
    var report = await verifier.RunAsync(job, round, File.ReadAllText(patchFile), submission, cts.Token);
    Console.WriteLine(report.Passed ? "PASSED" : "FAILED");
    foreach (var b in report.Blockers) Console.WriteLine($"  - {b}");
    return report.Passed ? 0 : 1;
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

int MarkReady(string? id)
{
    if (id is null) { Console.Error.WriteLine("ready: job id required"); return 2; }
    var job = Job.Open(home, id);
    if (!File.Exists(Path.Combine(job.Dir, "spec", "prp.json"))) { Console.Error.WriteLine("ready: no PRP yet; run the spec stage first"); return 1; }
    var warnings = job.ReadJson<Prp>("spec", "prp.json").Warnings;
    var mark = Ready.Mark(job);
    Console.WriteLine($"PRP marked Ready ({mark.PrpSha256[..12]}){(mark.EditedByHand ? "; prp.md was edited by hand and is the contract" : "")}");
    foreach (var w in warnings) Console.WriteLine($"  note, not fully checked: {w}");
    return 0;
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
    if (File.Exists(Path.Combine(job.Dir, "spec", "spec.json")))
    {
        var r = job.ReadJson<SpecReport>("spec", "spec.json");
        Console.WriteLine($"spec        {r.Requirements} requirements ({string.Join(", ", r.Checks.Select(c => $"{c.Value} {c.Key.Replace('_', ' ')}"))}), {r.Examples} examples, {r.Tasks} tasks; {r.Submissions} submissions{(r.Warnings.Count > 0 ? $", {r.Warnings.Count} warnings" : "")}; {r.Agent.ToolCalls} tool calls, ${r.Agent.CostUsd:0.00}{(r.Agent.CostIsEstimate ? " (estimate)" : "")}, {r.Agent.Seconds:0} s");
        Console.WriteLine($"            {Ready.Check(job) ?? "Ready"}");
    }
    if (File.Exists(Path.Combine(job.Dir, "implement", "implement.json")))
    {
        var r = job.ReadJson<ImplementReport>("implement", "implement.json");
        Console.WriteLine($"implement   {r.Status}{(r.Reason is null ? "" : $": {r.Reason}")}; {r.Rounds.Count} round(s), ${r.CostUsd:0.00} (estimate), {r.Seconds:0} s");
        foreach (var x in r.Rounds)
            Console.WriteLine($"            round {x.Round}: {x.Outcome}{(x.Verified is null ? "" : x.Verified.Value ? ", verified" : $", {x.Blockers} blocker(s)")}; {x.Agent?.ToolCalls} tool calls, ${x.CostUsd:0.00}, {x.Seconds:0} s");
    }
    if (File.Exists(Path.Combine(job.Dir, "verify", "verify.json")))
    {
        var v = job.ReadJson<VerifyReport>("verify", "verify.json");
        Console.WriteLine($"verify      round {v.Round}: {(v.Passed ? "PASSED" : "FAILED")}; {v.FilesChanged} files +{v.LinesAdded} -{v.LinesRemoved}; {(v.Tests is { } t ? $"{t.Passed} pass, {t.Regressions.Count} regressions, {t.NewPassing.Count} new passing" : "no test run")}; {(v.Repro is { } rp ? $"{rp.Reproducing.Count} new test(s) fail before and pass after" : "")}");
        if (v.Review is { } rv) Console.WriteLine($"            review: {rv.Verdict}, {rv.Requirements.Count(q => q.Verdict == "met")}/{rv.Requirements.Count} requirements met, {rv.Findings.Count} findings, {rv.Notes.Count} notes");
        foreach (var b in v.Blockers.Take(5)) Console.WriteLine($"            - {b}");
    }
    return 0;
}

string? Arg(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

string Required(string name) => Arg(name) ?? throw new ArgumentException($"{name} is required");
