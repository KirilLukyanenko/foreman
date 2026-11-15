using System.Text;
using System.Text.Json;
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

public sealed record ImplementRun(
    string Id, string Language, string? Stage, string Status, string? Reason, int Rounds, bool Verified, string? ReviewVerdict,
    HiddenGrade? Hidden, int FilesChanged, int LinesAdded, int LinesRemoved, decimal ImplementCostUsd, decimal PlanningCostUsd, double Seconds);

public sealed record ImplementEvalReport(DateTime At, string Model, int Cases, IReadOnlyList<ImplementRun> Results);

/// <summary>
/// The whole pipeline on real fixed issues, graded by the fix's own tests. Each case's job comes from the
/// localization eval (the issue at the commit before the fix, intake, clarify with defaults accepted, a PRP); here it
/// is fetched again, set up and baselined, its PRP is rewritten with the verified test command, marked Ready (this
/// harness stands in for the human), and implemented and verified with the fix loop. Then the maintainers' tests are
/// applied over the result and run: Foreman's own gate saying "verified" is a claim, the hidden tests are the check.
/// </summary>
public sealed class ImplementEval(string home, IAgentDriver driver, ImplementOptions options, Action<string>? log = null, bool keepSpec = false)
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
            results.Add(await RunCaseAsync(c, ct));
            var r = results[^1];
            log?.Invoke($"{c.Id}: {r.Status}{(r.Reason is null ? "" : $" ({Short(r.Reason)})")}; {r.Rounds} round(s); hidden tests: {(r.Hidden is { } h ? h.Error ?? (h.Resolved ? "RESOLVED" : $"not resolved, {h.FailToPassFailing.Count}/{h.FailToPass} fail-to-pass failing, {h.PassToPassBroken.Count} pass-to-pass broken") : "not run")}; ${r.ImplementCostUsd:0.00} + ${r.PlanningCostUsd:0.00} planning, {r.Seconds:0} s");
        }
        return new ImplementEvalReport(DateTime.UtcNow, options.Model, cases.Count, results);
    }

    private async Task<ImplementRun> RunCaseAsync(LocalizationCase c, CancellationToken ct)
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

        // Recon again (the earlier clone predates the line-ending fix), the rules file with the baseline, the PRP.
        var recon = new ReconOptions(Commit: c.BaseCommit);
        IStage[] planning =
        [
            new FetchStage(recon), new TriageStage(), new SecurityStage(null, Log), new CodeMapStage(null),
            new EnvironmentStage(recon, driver, Log), new BaselineStage(recon, Log),
            new DossierStage(driver, new DossierOptions(Sections: []), Log),
            .. keepSpec ? Array.Empty<IStage>() : [new SpecStage(driver, new SpecOptions(), Log)],
        ];
        var ok = await new Pipeline(planning).RunAsync(job, planning.Select(s => s.Name).ToHashSet(), Log, ct);
        var planningCost = PlanningCost(job);
        if (!ok) return new ImplementRun(c.Id, c.Language, failedStage, "recon_failed", error, 0, false, null, null, 0, 0, 0, 0, planningCost, Math.Round(clock.Elapsed.TotalSeconds, 1));

        Ready.Mark(job);
        job.Event("eval", "ready-by-harness", new { reason = "the implement eval stands in for the human" });
        await new Pipeline([new ImplementStage(driver, options, Log)]).RunAsync(job, new HashSet<string> { "implement" }, Log, ct);

        var report = File.Exists(Path.Combine(job.Dir, "implement", "implement.json")) ? job.ReadJson<ImplementReport>("implement", "implement.json") : null;
        var verify = File.Exists(Path.Combine(job.Dir, "verify", "verify.json")) ? job.ReadJson<VerifyReport>("verify", "verify.json") : null;
        var patchFile = Path.Combine(job.Dir, "implement", "patch.diff");
        HiddenGrade? hidden = null;
        try
        {
            var tests = await FetchHiddenAsync(c, ct);
            hidden = await GradeAsync(job, tests, File.Exists(patchFile) ? File.ReadAllText(patchFile) : null, ct);
        }
        catch (InvalidOperationException e)
        {
            hidden = new HiddenGrade(false, 0, [], 0, [], e.Message);
        }
        job.WriteJson("eval", "hidden.json", hidden);
        return new ImplementRun(c.Id, c.Language, report?.Status == "verified" ? null : "implement", report?.Status ?? "no_report", report?.Reason, report?.Rounds.Count ?? 0,
            report?.Status == "verified", verify?.Review?.Verdict, hidden, verify?.FilesChanged ?? 0, verify?.LinesAdded ?? 0, verify?.LinesRemoved ?? 0,
            report?.CostUsd ?? 0, planningCost, Math.Round(clock.Elapsed.TotalSeconds, 1));
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

    public static string Markdown(ImplementEvalReport r)
    {
        var graded = r.Results.Where(x => x.Hidden is { Error: null }).ToList();
        var sb = new StringBuilder("# Implement eval\n\n");
        sb.Append($"{r.At:yyyy-MM-dd}, implementer `{r.Model}`, {r.Cases} real fixed issues, one run each. Every case went through the whole pipeline at the commit before its fix; ");
        sb.Append("the PRP was marked Ready by the harness instead of a human. **Verified** is Foreman's own gate (tests against the baseline, the new tests' fail-to-pass, the diff guard, lint, secrets, the reviewer). ");
        sb.Append("**Resolved** is the maintainers' own tests from the fix, applied over Foreman's change (SWE-bench style: every FAIL_TO_PASS and PASS_TO_PASS test passes); no agent ever saw them.\n\n");
        sb.Append($"Resolved {graded.Count(x => x.Hidden!.Resolved)}/{graded.Count} graded; verified by the gate {r.Results.Count(x => x.Verified)}/{r.Results.Count}; ");
        sb.Append($"verified and resolved {graded.Count(x => x.Verified && x.Hidden!.Resolved)}, verified but not resolved {graded.Count(x => x.Verified && !x.Hidden!.Resolved)}, not verified but resolved {graded.Count(x => !x.Verified && x.Hidden!.Resolved)}. ");
        sb.Append($"Implementing and reviewing cost {r.Results.Sum(x => x.ImplementCostUsd):0.00} USD, planning (environment agent, spec) {r.Results.Sum(x => x.PlanningCostUsd):0.00} USD (list-price estimates; the runs were on a subscription).\n\n");
        sb.Append("| Case | Language | Gate | Rounds | Review | Hidden tests | Change | Cost | Time |\n|---|---|---|---|---|---|---|---|---|\n");
        foreach (var x in r.Results)
        {
            var hidden = x.Hidden switch
            {
                null => "not run",
                { Error: { } e } => $"not graded: {e}",
                { Resolved: true } h => $"**resolved** ({h.FailToPass} fail-to-pass, {h.PassToPass} pass-to-pass)",
                var h => $"not resolved: {h.FailToPassFailing.Count}/{h.FailToPass} fail-to-pass failing, {h.PassToPassBroken.Count}/{h.PassToPass} pass-to-pass broken",
            };
            sb.Append($"| {x.Id} | {x.Language} | {(x.Verified ? "verified" : x.Status.Replace('_', ' '))} | {x.Rounds} | {x.ReviewVerdict?.Replace('_', ' ') ?? ""} | {hidden} | {(x.FilesChanged == 0 ? "" : $"{x.FilesChanged} files +{x.LinesAdded} −{x.LinesRemoved}")} | ${x.ImplementCostUsd:0.00} | {x.Seconds / 60:0} min |\n");
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
