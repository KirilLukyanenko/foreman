using System.Text;
using System.Text.Json;
using Foreman.Recon.Workspaces;
using Foreman.Core;
using Foreman.Implement;
using Foreman.Planning.Spec;
using Foreman.Recon;
using Foreman.Recon.Dossier;
using Foreman.Recon.Reports;
using Foreman.Recon.Security;
using Foreman.Recon.CodeMap;
using Foreman.Recon.Environment;
using Foreman.Sandbox;

namespace Foreman.Evals;

public sealed record ImplementCase(string Id, string? Note = null);

/// <summary>
/// The fix's own tests, SWE-bench style: what the maintainers' merged change did to the test files and to the code,
/// as diffs against the case's base commit. Fetched once from GitHub and cached; never shown to any agent.
/// </summary>
public sealed record HiddenTests(string MergeCommit, IReadOnlyList<string> TestFiles, string TestPatch, string GoldPatch);

/// <summary>
/// The hidden tests' verdict on a patch. FAIL_TO_PASS: tests of the fix that fail on the base commit and pass with the
/// maintainers' change. PASS_TO_PASS: tests that pass both ways. Resolved means every one of both passes with ours.
/// </summary>
public sealed record HiddenGrade(bool Resolved, int FailToPass, IReadOnlyList<string> FailToPassFailing, int PassToPass, IReadOnlyList<string> PassToPassBroken, string? Error = null);

/// <summary>
/// Foreman's own hidden acceptance tests, graded against the maintainers' fix: tests that fail on the real fix assert
/// something the maintainers didn't do (a different but maybe defensible PRP, or a wrong test).
/// </summary>
/// <param name="PassOnGold">How many of the tests pass with the maintainers' code change applied to the base commit.</param>
public sealed record AcceptanceGrade(string Status, int Tests, int FailOnBase, double? MutationScore, int? PassOnGold, IReadOnlyList<string> FailingOnGold, decimal CostUsd, string? Error = null);

public sealed record ImplementRun(
    string Id, string Language, string? Stage, string Status, string? Reason, int Rounds, bool Verified, string? ReviewVerdict,
    HiddenGrade? Hidden, int FilesChanged, int LinesAdded, int LinesRemoved, decimal ImplementCostUsd, decimal PlanningCostUsd, double Seconds,
    AcceptanceGrade? Acceptance = null, int Run = 1, int Candidates = 1);

/// <param name="Runs">Implement runs per case on the same PRP; with more than one the page reports pass@k and pass^k.</param>
/// <param name="Label">What this run is (baseline, no-review, candidates-3...), for comparing versions.</param>
public sealed record ImplementEvalReport(DateTime At, string Model, int Cases, IReadOnlyList<ImplementRun> Results, int Runs = 1, string? Label = null);

/// <summary>
/// The whole pipeline on real fixed issues, graded by the fix's own tests. Each case's job comes from the
/// localization eval (the issue at the commit before the fix, intake, clarify with defaults accepted, a PRP); here it
/// is fetched again, set up and baselined, its PRP is rewritten with the verified test command, marked Ready (this
/// harness stands in for the human), and implemented and verified with the fix loop. Then the maintainers' tests are
/// applied over the result and run: Foreman's own gate saying "verified" is a claim, the hidden tests are the check.
/// </summary>
public sealed class ImplementEval(string home, IAgentDriver driver, ImplementOptions options, Action<string>? log = null, bool keepSpec = false, AcceptanceOptions? acceptance = null,
    int runs = 1, bool reusePlanning = false, string? label = null)
{
    public static IReadOnlyList<ImplementCase> ReadCases(string path) =>
        JsonSerializer.Deserialize<List<ImplementCase>>(File.ReadAllText(path), LocalizationEval.CaseJson) ?? [];

    public async Task<ImplementEvalReport> RunAsync(IReadOnlyList<ImplementCase> cases, string casesFile, CancellationToken ct)
    {
        var localization = LocalizationEval.ReadCases(Path.Combine(Path.GetDirectoryName(casesFile)!, "..", "localization", "cases.json"))
            .ToDictionary(c => c.Id, StringComparer.Ordinal);
        var results = new List<ImplementRun>();
        foreach (var k in cases)
        {
            var c = localization[k.Id];
            log?.Invoke($"{c.Id} ({c.Language})");
            await foreach (var r in RunCaseAsync(c, ct))
            {
                results.Add(r);
                log?.Invoke($"{c.Id}{(runs > 1 ? $" run {r.Run}" : "")}: {r.Status}{(r.Reason is null ? "" : $" ({Short(r.Reason)})")}; {r.Rounds} round(s); hidden tests: {(r.Hidden is { } h ? h.Error ?? (h.Resolved ? "RESOLVED" : $"not resolved, {h.FailToPassFailing.Count}/{h.FailToPass} fail-to-pass failing, {h.PassToPassBroken.Count} pass-to-pass broken") : "not run")}; ${r.ImplementCostUsd:0.00} + ${r.PlanningCostUsd:0.00} planning, {r.Seconds:0} s");
            }
        }
        return new ImplementEvalReport(DateTime.UtcNow, options.Model, cases.Count, results, runs, label);
    }

    private async IAsyncEnumerable<ImplementRun> RunCaseAsync(LocalizationCase c, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var id = LocalizationEval.JobId(c);
        if (!Directory.Exists(Path.Combine(home, "jobs", id))) throw new InvalidOperationException($"{c.Id}: job {id} is missing; run the localization and spec evals first");
        var job = Job.Open(home, id);
        string? failedStage = null, error = null;
        void Log(string s)
        {
            log?.Invoke($"  {s.Trim()}");
            if (s.Contains("FAILED", StringComparison.Ordinal) && failedStage is null) { failedStage = s.Split(':')[0].Trim(); error = s; }
        }

        // Planning once per case: recon again (the earlier clone predates the line-ending fix), the rules file with the
        // baseline, the PRP, the hidden acceptance tests. With reusePlanning a case that has them all keeps them, so an
        // ablation changes only what it is about.
        var reuse = reusePlanning && Reusable(job, acceptance is not null);
        if (reuse) log?.Invoke("  planning: reused (environment, baseline, PRP and acceptance tests from the last run)");
        else
        {
            var recon = new ReconOptions(Commit: c.BaseCommit);
            IStage[] planning =
            [
                new FetchStage(recon), new TriageStage(), new SecurityStage(null, Log), new CodeMapStage(null),
                new EnvironmentStage(recon, driver, Log), new BaselineStage(recon, Log),
                new DossierStage(driver, new DossierOptions(Sections: []), Log),
                .. keepSpec ? Array.Empty<IStage>() : [new SpecStage(driver, new SpecOptions(), Log)],
            ];
            var ok = await new Pipeline(planning).RunAsync(job, planning.Select(s => s.Name).ToHashSet(), Log, ct);
            if (!ok)
            {
                yield return new ImplementRun(c.Id, c.Language, failedStage, "recon_failed", error, 0, false, null, null, 0, 0, 0, 0, PlanningCost(job), Math.Round(clock.Elapsed.TotalSeconds, 1));
                yield break;
            }
            Ready.Mark(job);
            job.Event("eval", "ready-by-harness", new { reason = "the implement eval stands in for the human" });
            if (acceptance is not null)
                await new Pipeline([new AcceptanceStage(driver, acceptance, Log)]).RunAsync(job, new HashSet<string> { "accept" }, Log, ct);
            else Dirs.Delete(Path.Combine(job.Dir, "acceptance"));
        }
        var planningCost = reuse ? 0 : PlanningCost(job);

        HiddenTests? tests = null;
        AcceptanceGrade? accepted = null;
        string? gradeError = null;
        try
        {
            tests = await FetchHiddenAsync(c, ct);
            if (acceptance is not null) accepted = await GradeAcceptanceAsync(job, tests, ct);
        }
        catch (InvalidOperationException e)
        {
            gradeError = e.Message;
        }
        if (accepted is not null) job.WriteJson("eval", "acceptance.json", accepted);

        for (var run = 1; run <= runs; run++)
        {
            var runClock = System.Diagnostics.Stopwatch.StartNew();
            await new Pipeline([new ImplementStage(driver, options, Log)]).RunAsync(job, new HashSet<string> { "implement" }, Log, ct);
            var report = File.Exists(Path.Combine(job.Dir, "implement", "implement.json")) ? job.ReadJson<ImplementReport>("implement", "implement.json") : null;
            var verify = File.Exists(Path.Combine(job.Dir, "verify", "verify.json")) ? job.ReadJson<VerifyReport>("verify", "verify.json") : null;
            var patchFile = Path.Combine(job.Dir, "implement", "patch.diff");
            HiddenGrade hidden;
            try
            {
                hidden = tests is null ? new HiddenGrade(false, 0, [], 0, [], gradeError ?? "no hidden tests") : await GradeAsync(job, tests, File.Exists(patchFile) ? File.ReadAllText(patchFile) : null, ct);
            }
            catch (InvalidOperationException e)
            {
                hidden = new HiddenGrade(false, 0, [], 0, [], e.Message);
            }
            job.WriteJson("eval", runs == 1 ? "hidden.json" : $"hidden-run-{run}.json", hidden);
            if (runs > 1) KeepRun(job, run);
            yield return new ImplementRun(c.Id, c.Language, report?.Status == "verified" ? null : "implement", report?.Status ?? "no_report", report?.Reason, report?.Rounds.Count ?? 0,
                report?.Status == "verified", verify?.Review?.Verdict, hidden, verify?.FilesChanged ?? 0, verify?.LinesAdded ?? 0, verify?.LinesRemoved ?? 0,
                report?.CostUsd ?? 0, run == 1 ? planningCost : 0, Math.Round((run == 1 ? clock : runClock).Elapsed.TotalSeconds, 1), accepted, run, report?.Candidates?.Count ?? 1);
        }
    }

    /// <summary>A job whose planning a rerun can keep: a Ready PRP, a baseline volume, and acceptance tests if they are wanted.</summary>
    private static bool Reusable(Job job, bool needAcceptance)
    {
        if (Ready.Check(job) is not null) return false;
        try { TestEnvironment.Of(job); }
        catch (InvalidOperationException) { return false; }
        catch (IOException) { return false; }
        if (!DockerSandbox.VolumeExistsAsync(new DockerSandbox(job.Id).Volume).GetAwaiter().GetResult()) return false;
        return !needAcceptance || File.Exists(AcceptanceTools.ResultFile(job));
    }

    /// <summary>With several runs, each run's implement and verify folders are kept under eval/run-N before the next one clears them.</summary>
    private static void KeepRun(Job job, int run)
    {
        foreach (var stage in new[] { "implement", "verify" })
        {
            var from = Path.Combine(job.Dir, stage);
            var to = Path.Combine(job.Dir, "eval", $"run-{run}", stage);
            if (!Directory.Exists(from)) continue;
            Dirs.Delete(to);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            CopyDirectory(from, to);
        }
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), overwrite: true);
        foreach (var d in Directory.GetDirectories(from)) CopyDirectory(d, Path.Combine(to, Path.GetFileName(d)));
    }

    /// <summary>Foreman's hidden tests on the maintainers' fix: the base commit, their code change, our acceptance tests.</summary>
    private async Task<AcceptanceGrade> GradeAcceptanceAsync(Job job, HiddenTests hidden, CancellationToken ct)
    {
        var file = AcceptanceTools.ResultFile(job);
        var cost = File.Exists(Path.Combine(job.Dir, "acceptance", "summary.json"))
            ? JsonDocument.Parse(File.ReadAllText(Path.Combine(job.Dir, "acceptance", "summary.json"))).RootElement.TryGetProperty("agent", out var a) && a.TryGetProperty("costUsd", out var c) ? c.GetDecimal() : 0
            : 0;
        if (!File.Exists(file)) return new AcceptanceGrade("none", 0, 0, null, null, [], cost, "the accept stage wrote nothing");
        var report = Json.Read<AcceptanceReport>(File.ReadAllText(file))!;
        var failOnBase = report.Tests.Count(t => t.OnBase != "passed");
        if (AcceptanceStage.Hidden(job) is not { } set) return new AcceptanceGrade(report.Status, report.Tests.Count, failOnBase, report.MutationScore, null, [], cost, report.Reason);
        var env = TestEnvironment.Of(job);
        var onGold = await RunWithAsync(job, env, [hidden.GoldPatch, set.Diff], [], Path.Combine(job.StageDir("eval"), "acceptance-gold"), ct);
        var failing = report.Tests.Where(t => onGold.GetValueOrDefault(t.Id, TestStatus.Failed) != TestStatus.Passed).Select(t => $"{t.Id} ({t.Requirement})").ToList();
        return new AcceptanceGrade(report.Status, report.Tests.Count, failOnBase, report.MutationScore, report.Tests.Count - failing.Count, failing, cost);
    }

    private static decimal PlanningCost(Job job)
    {
        decimal cost = 0;
        if (File.Exists(Path.Combine(job.Dir, "spec", "spec.json"))) cost += job.ReadJson<SpecReport>("spec", "spec.json").Agent.CostUsd;
        if (File.Exists(Path.Combine(job.Dir, "environment", "environment.json")))
            cost += job.ReadJson<EnvironmentReport>("environment", "environment.json").Stacks.Sum(s => s.Agent?.CostUsd ?? 0);
        return cost;
    }

    // ------------------------------------------------------------------ hidden tests

    /// <summary>The fix's diffs against the base commit, from a scratch clone of the two commits (no checkout, nothing runs).</summary>
    public async Task<HiddenTests> FetchHiddenAsync(LocalizationCase c, CancellationToken ct)
    {
        var dir = Path.Combine(home, "eval-cache", c.Id);
        var file = Path.Combine(dir, "hidden.json");
        if (File.Exists(file)) return Json.Read<HiddenTests>(File.ReadAllText(file))!;
        if (c.TestFilesChanged is not { Count: > 0 } testFiles) throw new InvalidOperationException($"{c.Id}: the case lists no test files");

        var pr = c.Fix.TrimEnd('/').Split('/');
        var (owner, repo, number) = (pr[^4], pr[^3], pr[^1]);
        var api = await Proc.RunAsync("gh", ["api", $"repos/{owner}/{repo}/pulls/{number}", "--jq", ".merge_commit_sha"], timeout: TimeSpan.FromMinutes(1), ct: ct);
        var merge = api.Stdout.Trim();
        if (!api.Ok || merge.Length < 40) throw new InvalidOperationException($"{c.Id}: no merge commit for {c.Fix}: {api.Tail(3)}");

        var git = Path.Combine(dir, "repo");
        Dirs.Delete(git);
        Directory.CreateDirectory(git);
        string[] safe = ["-c", "core.hooksPath=.foreman-no-hooks", "-c", "core.autocrlf=false"];
        async Task<string> Git(params string[] args)
        {
            var r = await Proc.RunAsync("git", [.. safe, .. args], git, TimeSpan.FromMinutes(10), ct: ct);
            if (!r.Ok) throw new InvalidOperationException($"{c.Id}: git {args[0]} failed: {r.Tail(3)}");
            return r.Stdout;
        }
        await Git("init", "--quiet", "--bare");
        await Git("fetch", "--quiet", "--depth", "1", "--no-tags", c.Repo, c.BaseCommit, merge);
        var testPatch = await Git(["diff", "--binary", "--no-renames", c.BaseCommit, merge, "--", .. testFiles]);
        var goldPatch = await Git(["diff", "--binary", "--no-renames", c.BaseCommit, merge, "--", .. c.GoldFiles]);
        var hidden = new HiddenTests(merge, testFiles, testPatch, goldPatch);
        File.WriteAllText(file, Json.Write(hidden), new UTF8Encoding(false));
        Dirs.Delete(git);
        return hidden;
    }

    /// <summary>
    /// Runs the hidden tests on the base commit and with the maintainers' change (once per case, cached), then with
    /// ours: the test files the fix touched are reset to the base commit first, as SWE-bench does, so our own edits to
    /// them don't decide the grade.
    /// </summary>
    public async Task<HiddenGrade> GradeAsync(Job job, HiddenTests hidden, string? patch, CancellationToken ct)
    {
        var env = TestEnvironment.Of(job);
        var envKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join('\n', [env.Image, env.TestScript, .. env.Env.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => $"{e.Key}={e.Value}")]))))[..16];
        var dir = job.StageDir("eval");
        var referenceFile = Path.Combine(home, "eval-cache", job.Id, "reference.json");
        Dictionary<string, TestStatus> before, gold;
        if (File.Exists(referenceFile) && Json.Read<Reference>(File.ReadAllText(referenceFile)) is { } cached && cached.Merge == hidden.MergeCommit && cached.Environment == envKey)
            (before, gold) = (cached.Before, cached.Gold);
        else
        {
            before = await RunWithAsync(job, env, [hidden.TestPatch], hidden.TestFiles, Path.Combine(dir, "hidden-base"), ct);
            gold = await RunWithAsync(job, env, [hidden.GoldPatch, hidden.TestPatch], hidden.TestFiles, Path.Combine(dir, "hidden-gold"), ct);
            Directory.CreateDirectory(Path.GetDirectoryName(referenceFile)!);
            File.WriteAllText(referenceFile, Json.Write(new Reference(hidden.MergeCommit, envKey, before, gold)), new UTF8Encoding(false));
        }
        // As in SWE-bench, a test the base run doesn't report (its module no longer imports or compiles with the fix's
        // tests) counts as failing there: it can't be told apart from an old test the fix rewrote. Resolved is the same
        // either way (every test that passes with the fix must pass with ours); only the split between the two counts
        // moves.
        var failToPass = gold.Where(g => g.Value == TestStatus.Passed && before.GetValueOrDefault(g.Key, TestStatus.Failed) != TestStatus.Passed).Select(g => g.Key).ToList();
        var passToPass = gold.Where(g => g.Value == TestStatus.Passed && before.GetValueOrDefault(g.Key, TestStatus.Failed) == TestStatus.Passed).Select(g => g.Key).ToList();
        if (failToPass.Count == 0) return new HiddenGrade(false, 0, [], passToPass.Count, [], "the fix's tests have no fail-to-pass test in this environment");
        if (patch is null) return new HiddenGrade(false, failToPass.Count, failToPass, passToPass.Count, [], "no patch");

        var ours = await RunWithAsync(job, env, [patch, hidden.TestPatch], hidden.TestFiles, Path.Combine(dir, "hidden-ours"), ct, resetTestFilesAfterFirst: true);
        var f2pFailing = failToPass.Where(t => ours.GetValueOrDefault(t, TestStatus.Failed) != TestStatus.Passed).ToList();
        var p2pBroken = passToPass.Where(t => ours.GetValueOrDefault(t, TestStatus.Failed) != TestStatus.Passed).ToList();
        return new HiddenGrade(f2pFailing.Count == 0 && p2pBroken.Count == 0, failToPass.Count, f2pFailing, passToPass.Count, p2pBroken);
    }

    /// <summary>The base and gold runs, valid for one merge commit and one environment (image and test script): a rebuilt environment runs them again.</summary>
    private sealed record Reference(string Merge, string? Environment, Dictionary<string, TestStatus> Before, Dictionary<string, TestStatus> Gold);

    private static async Task<Dictionary<string, TestStatus>> RunWithAsync(Job job, TestEnvironment env, IReadOnlyList<string> patches, IReadOnlyList<string> testFiles,
        string hostDir, CancellationToken ct, bool resetTestFilesAfterFirst = false)
    {
        var sandbox = new DockerSandbox(job.Id, suffix: "grade");
        try
        {
            await Workspace.CreateAsync(sandbox, env);
            await using var ws = await Workspace.OpenAsync(sandbox.Volume, env);
            for (var i = 0; i < patches.Count; i++)
            {
                if (patches[i].Trim().Length == 0) continue;
                if (i == patches.Count - 1 && resetTestFilesAfterFirst)
                {
                    // Our edits to the fix's test files give way to the fix's versions.
                    var reset = string.Join('\n', testFiles.Select(f => $"if git cat-file -e HEAD:{Workspace.Quote(f)} 2>/dev/null; then {Workspace.Git} checkout HEAD -- {Workspace.Quote(f)}; else rm -f {Workspace.Quote(f)}; fi"));
                    await ws.BashAsync(reset, TimeSpan.FromMinutes(2), ct);
                }
                if (await ws.ApplyAsync(patches[i], ct: ct) is { } e)
                    throw new InvalidOperationException($"patch {i + 1} doesn't apply for the hidden tests: {e}");
            }
            var run = await ws.RunTestsAsync(hostDir, ct);
            return run.Outcomes.GroupBy(o => o.Id).ToDictionary(g => g.Key, g => g.First().Status, StringComparer.Ordinal);
        }
        finally
        {
            await sandbox.RemoveAsync();
        }
    }

    // ------------------------------------------------------------------ report

    /// <summary>
    /// Two runs of the eval on the same cases, compared pair by pair (the same case and run number in both): how many
    /// each resolved and verified, the discordant pairs, McNemar's exact p-value, and the cost. With nine cases a
    /// difference of a case or two is noise; the p-value says so.
    /// </summary>
    public static string Compare(ImplementEvalReport a, ImplementEvalReport b)
    {
        string Name(ImplementEvalReport r, string fallback) => r.Label ?? fallback;
        var (na, nb) = (Name(a, "A"), Name(b, "B"));
        var pairs = a.Results.Join(b.Results, x => (x.Id, x.Run), y => (y.Id, y.Run), (x, y) => (A: x, B: y))
            .Where(p => p.A.Hidden is { Error: null } && p.B.Hidden is { Error: null }).ToList();
        var onlyA = pairs.Count(p => p.A.Hidden!.Resolved && !p.B.Hidden!.Resolved);
        var onlyB = pairs.Count(p => !p.A.Hidden!.Resolved && p.B.Hidden!.Resolved);
        var sb = new StringBuilder($"# Implement eval: {na} vs {nb}\n\n");
        sb.Append($"{pairs.Count} paired runs (the same case and run number, graded in both).\n\n");
        sb.Append($"| | {na} | {nb} |\n|---|---|---|\n");
        sb.Append($"| Resolved | {pairs.Count(p => p.A.Hidden!.Resolved)}/{pairs.Count} | {pairs.Count(p => p.B.Hidden!.Resolved)}/{pairs.Count} |\n");
        sb.Append($"| Verified by the gate | {pairs.Count(p => p.A.Verified)}/{pairs.Count} | {pairs.Count(p => p.B.Verified)}/{pairs.Count} |\n");
        sb.Append($"| Verified but not resolved | {pairs.Count(p => p.A.Verified && !p.A.Hidden!.Resolved)} | {pairs.Count(p => p.B.Verified && !p.B.Hidden!.Resolved)} |\n");
        sb.Append($"| Rounds | {pairs.Sum(p => p.A.Rounds)} | {pairs.Sum(p => p.B.Rounds)} |\n");
        sb.Append($"| Implement and review cost | ${pairs.Sum(p => p.A.ImplementCostUsd):0.00} | ${pairs.Sum(p => p.B.ImplementCostUsd):0.00} |\n");
        sb.Append($"| Time | {pairs.Sum(p => p.A.Seconds) / 60:0} min | {pairs.Sum(p => p.B.Seconds) / 60:0} min |\n\n");
        sb.Append($"Resolved by only {na}: {onlyA}; only {nb}: {onlyB}. McNemar's exact test: p = {Stats.McNemar(onlyA, onlyB):0.###}");
        sb.Append(Stats.McNemar(onlyA, onlyB) < 0.05 ? " (a real difference at the 5% level).\n\n" : " (not distinguishable from chance on this many cases).\n\n");
        sb.Append($"| Case | Run | {na} | {nb} |\n|---|---|---|---|\n");
        foreach (var p in pairs)
            sb.Append($"| {p.A.Id} | {p.A.Run} | {Cell(p.A)} | {Cell(p.B)} |\n");
        return sb.ToString();

        static string Cell(ImplementRun r) => $"{(r.Hidden!.Resolved ? "resolved" : "not resolved")}, {(r.Verified ? "verified" : r.Status.Replace('_', ' '))}, {r.Rounds} round(s)";
    }

    public static string Markdown(ImplementEvalReport r)
    {
        var graded = r.Results.Where(x => x.Hidden is { Error: null }).ToList();
        var sb = new StringBuilder("# Implement eval\n\n");
        if (r.Label is not null) sb.Append($"Run: **{r.Label}**.\n\n");
        sb.Append($"{r.At:yyyy-MM-dd}, implementer `{r.Model}`, {r.Cases} real fixed issues, {(r.Runs == 1 ? "one run each" : $"{r.Runs} implement runs each on the same PRP")}. Every case went through the whole pipeline at the commit before its fix; ");
        sb.Append("the PRP was marked Ready by the harness instead of a human. **Verified** is Foreman's own gate (tests against the baseline, the new tests' fail-to-pass, the diff guard, lint, secrets, the reviewer). ");
        sb.Append("**Resolved** is the maintainers' own tests from the fix, applied over Foreman's change (SWE-bench style: every FAIL_TO_PASS and PASS_TO_PASS test passes); no agent ever saw them.\n\n");
        sb.Append($"Resolved {graded.Count(x => x.Hidden!.Resolved)}/{graded.Count} graded; verified by the gate {r.Results.Count(x => x.Verified)}/{r.Results.Count}; ");
        sb.Append($"verified and resolved {graded.Count(x => x.Verified && x.Hidden!.Resolved)}, verified but not resolved {graded.Count(x => x.Verified && !x.Hidden!.Resolved)}, not verified but resolved {graded.Count(x => !x.Verified && x.Hidden!.Resolved)}. ");
        sb.Append($"Implementing and reviewing cost {r.Results.Sum(x => x.ImplementCostUsd):0.00} USD, planning (environment agent, spec) {r.Results.Sum(x => x.PlanningCostUsd):0.00} USD (list-price estimates; the runs were on a subscription).\n\n");
        var resolved = graded.Count(x => x.Hidden!.Resolved);
        sb.Append($"Resolved rate {Stats.Percent(graded.Count == 0 ? 0 : (double)resolved / graded.Count)} of graded runs (95% Wilson interval {Stats.Interval(Stats.Wilson(resolved, graded.Count))}).\n\n");
        if (r.Runs > 1)
        {
            var k = r.Runs;
            sb.Append($"Per case, over {k} runs (pass@1 and pass@{k}: at least one of 1 or {k} runs resolves; pass^{k}: all {k} do, which is what one client gets when they need it every time):\n\n");
            sb.Append($"| Case | Verified | Resolved | pass@1 | pass@{k} | pass^{k} |\n|---|---|---|---|---|---|\n");
            var perCase = graded.GroupBy(x => x.Id).Select(g => (Id: g.Key, N: g.Count(), C: g.Count(x => x.Hidden!.Resolved), V: g.Count(x => x.Verified))).ToList();
            foreach (var c in perCase)
                sb.Append($"| {c.Id} | {c.V}/{c.N} | {c.C}/{c.N} | {Stats.Percent(Stats.PassAtK(c.N, c.C, 1))} | {(c.N >= k ? Stats.Percent(Stats.PassAtK(c.N, c.C, k)) : "")} | {(c.N >= k ? Stats.Percent(Stats.PassHatK(c.N, c.C, k)) : "")} |\n");
            var full = perCase.Where(c => c.N >= k).ToList();
            if (full.Count > 0)
                sb.Append($"| **mean** | | | {Stats.Percent(full.Average(c => Stats.PassAtK(c.N, c.C, 1)))} | {Stats.Percent(full.Average(c => Stats.PassAtK(c.N, c.C, k)))} | {Stats.Percent(full.Average(c => Stats.PassHatK(c.N, c.C, k)))} |\n");
            sb.Append('\n');
        }
        var acc = r.Results.Where(x => x.Acceptance is not null).GroupBy(x => x.Id).Select(g => g.First()).ToList();
        if (acc.Count > 0)
        {
            var onGold = acc.Where(x => x.Acceptance!.PassOnGold is not null).ToList();
            sb.Append($"**Foreman's own hidden acceptance tests** (written from the PRP by a separate session, tested by code on the base commit, a reference fix and its mutants): written for {acc.Count(x => x.Acceptance!.Status == "ready")}/{acc.Count} cases; ");
            sb.Append($"{onGold.Sum(x => x.Acceptance!.PassOnGold)} of {onGold.Sum(x => x.Acceptance!.Tests)} pass on the maintainers' fix, all of them in {onGold.Count(x => x.Acceptance!.FailingOnGold.Count == 0)}/{onGold.Count} cases; ");
            sb.Append($"writing them cost {acc.Sum(x => x.Acceptance!.CostUsd):0.00} USD.\n\n");
        }
        sb.Append(acc.Count > 0
            ? "| Case | Language | Gate | Rounds | Review | Hidden tests | Acceptance tests | Change | Cost | Time |\n|---|---|---|---|---|---|---|---|---|---|\n"
            : "| Case | Language | Gate | Rounds | Review | Hidden tests | Change | Cost | Time |\n|---|---|---|---|---|---|---|---|---|\n");
        foreach (var x in r.Results)
        {
            var accepted = x.Acceptance switch
            {
                null => "",
                { Status: not "ready" } a => $"{a.Status.Replace('_', ' ')}{(a.Error is null ? "" : $": {Short(a.Error, 80)}")}",
                var a => $"{a.Tests} tests, {a.FailOnBase} fail before; mutants {(a.MutationScore is { } m ? $"{m:P0} killed" : "n/a")}{(a.PassOnGold is { } g ? $"; {g}/{a.Tests} pass on the maintainers' fix" : "")}",
            };
            var hidden = x.Hidden switch
            {
                null => "not run",
                { Error: { } e } => $"not graded: {e}",
                { Resolved: true } h => $"**resolved** ({h.FailToPass} fail-to-pass, {h.PassToPass} pass-to-pass)",
                var h => $"not resolved: {h.FailToPassFailing.Count}/{h.FailToPass} fail-to-pass failing, {h.PassToPassBroken.Count}/{h.PassToPass} pass-to-pass broken",
            };
            sb.Append($"| {x.Id} | {x.Language} | {(x.Verified ? "verified" : x.Status.Replace('_', ' '))} | {x.Rounds} | {x.ReviewVerdict?.Replace('_', ' ') ?? ""} | {hidden} |{(acc.Count > 0 ? $" {accepted} |" : "")} {(x.FilesChanged == 0 ? "" : $"{x.FilesChanged} files +{x.LinesAdded} −{x.LinesRemoved}")} | ${x.ImplementCostUsd:0.00} | {x.Seconds / 60:0} min |\n");
        }
        var notes = r.Results.Where(x => x.Reason is not null).ToList();
        if (notes.Count > 0)
        {
            sb.Append("\nWhy the others stopped:\n\n");
            foreach (var x in notes) sb.Append($"- **{x.Id}** ({x.Status.Replace('_', ' ')}): {Short(x.Reason!, 400)}\n");
        }
        sb.Append("\nThe issues, the PRPs, the patches and the hidden tests stay in the job folders and the eval cache, not in this repository.\n");
        return sb.ToString();
    }

    private static string Short(string s, int max = 160) => s.Length <= max ? s : s[..max] + "…";
}
