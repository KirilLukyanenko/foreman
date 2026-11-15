using System.Diagnostics;
using System.Text;
using Foreman.Recon.Workspaces;
using Foreman.Core;
using Foreman.Planning.Spec;
using Foreman.Recon.CodeMap;
using Foreman.Recon.Security;
using Foreman.Sandbox;

namespace Foreman.Implement;

/// <summary>
/// Fail-to-pass for the implementer's own tests: the new tests must pass with the change and must not pass with only
/// the test files applied to the code before it. A test that passes either way shows nothing about the fix.
/// </summary>
/// <param name="Required">The PRP has an acceptance-tested requirement or a reproduction task.</param>
/// <param name="Reproducing">New tests that fail (or don't run) before the change and pass after it.</param>
/// <param name="PassBefore">New tests that already pass before the change: they don't reproduce anything.</param>
public sealed record ReproCheck(bool Required, IReadOnlyList<string> Reproducing, IReadOnlyList<string> PassBefore, string? Error = null)
{
    public bool Ok => Error is null && (!Required || Reproducing.Count > 0);
}

/// <param name="Before">pass, fail or timeout on the code before the change; a command that fails before is not usable here.</param>
public sealed record LintResult(string Command, string Source, string Before, string After, string? Tail = null)
{
    public bool Broke => Before == "pass" && After != "pass";
}

/// <summary>A secret gitleaks found on a line the change adds. The value is never stored.</summary>
public sealed record SecretHit(string Rule, string Path, int Line);

/// <summary><c>verify/round-N/verify.json</c>: everything the orchestrator checked, and whether the change passes.</summary>
public sealed record VerifyReport(
    int Round,
    bool Passed,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<GuardFinding> Guard,
    TestDelta? Tests,
    ReproCheck? Repro,
    IReadOnlyList<LintResult> Lint,
    IReadOnlyList<SecretHit> Secrets,
    ReviewReport? Review,
    int FilesChanged,
    int LinesAdded,
    int LinesRemoved,
    double Seconds);

/// <summary>
/// Stage 6, run by the orchestrator and never by the agent, on fresh copies of the baseline volume with only the
/// patch carried over (whatever the agent did elsewhere in its sandbox, a changed installed package or a test report,
/// doesn't come along): the diff guard, pass-to-pass against the baseline (a regression must fail twice), the new
/// tests' fail-to-pass, the repository's CI lint commands before and after (only a command that passed before can
/// fail the change), gitleaks on the added lines, and, when all of that holds, the reviewer.
/// </summary>
public sealed class Verifier(IAgentDriver? reviewer, ReviewOptions? reviewOptions = null, SandboxLimits? limits = null, Action<string>? log = null)
{
    public const int MaxLintCommands = 4;
    private static readonly TimeSpan LintTimeout = TimeSpan.FromMinutes(5);
    private const int MaxChangedFileBytes = 2_000_000;

    public async Task<VerifyReport> RunAsync(Job job, int round, string patch, ImplementSubmission? submission, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        if (Ready.Check(job) is { } notReady) throw new InvalidOperationException(notReady);
        var prp = job.ReadJson<Prp>("spec", "prp.json");
        var env = TestEnvironment.Of(job);
        var dir = job.StageDir(Path.Combine("verify", ImplementTools.RoundDir(round)));
        Dirs.Delete(dir);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "patch.diff"), patch, new UTF8Encoding(false));

        var changes = Patch.Parse(patch);
        var guard = DiffGuard.Check(changes, GuardPolicy.Of(prp));
        var blockers = guard.Where(g => g.Blocks).Select(g => $"diff guard, {g.Rule}: {g.Path}{(g.Line is null ? "" : $":{g.Line}")}: {g.Detail}").ToList();
        if (changes.Count == 0) blockers.Add("the patch is empty");

        var lintCommands = SpecStage.Validation(job).Where(v => !v.Verified).Take(MaxLintCommands).ToList();
        TestDelta? tests = null;
        ReproCheck? repro = null;
        var lint = new List<LintResult>();
        var secrets = new List<SecretHit>();

        var after = new DockerSandbox(job.Id, limits, $"verify-{round}");
        var before = new DockerSandbox(job.Id, limits, $"verify-{round}-base");
        try
        {
            // The change on a fresh copy.
            log?.Invoke("  verify: the change on a fresh copy");
            await Workspace.CreateAsync(after, env);
            await using (var ws = await Workspace.OpenAsync(after.Volume, env, limits))
            {
                if (await ws.ApplyAsync(patch, ct: ct) is { } applyError)
                    blockers.Add($"the patch doesn't apply to a fresh copy of the repository: {applyError}");
                else
                {
                    var run = await ws.RunTestsAsync(Path.Combine(dir, "tests-after"), ct);
                    tests = run.Delta.Allowing(id => SpecTools.Accounts(prp.Spec.AllowedEdits, id));
                    if (run.Outcomes.Count == 0) blockers.Add($"the test command produced no report with the change (exit {run.ExitCode}): {Last(run.LogTail)}");
                    else if (tests.Regressions.Count > 0)
                    {
                        log?.Invoke($"  verify: {tests.Regressions.Count} regression(s), running the tests again to rule out flakes");
                        var again = await ws.RunTestsAsync(Path.Combine(dir, "tests-after-again"), ct);
                        tests = tests.Confirmed(again.Delta);
                    }
                    if (tests.Regressions.Count > 0)
                        blockers.Add($"pass-to-pass: {tests.Regressions.Count} test(s) passed in every baseline run and fail with the change, twice: {string.Join(", ", tests.Regressions.Take(10).Select(r => $"{r.Id} ({r.Now})"))}");
                    if (tests.NewFailing.Count > 0) blockers.Add($"new tests fail with the change: {string.Join(", ", tests.NewFailing.Take(10))}");

                    await CopyChangedFilesAsync(ws, changes, Path.Combine(dir, "after"), ct);
                    foreach (var c in lintCommands)
                    {
                        var r = await ws.BashAsync(LintScript(env, c.Command), LintTimeout, ct);
                        lint.Add(new LintResult(c.Command, c.Source, "?", r.TimedOut ? "timeout" : r.Ok ? "pass" : "fail", r.Ok ? null : Last(r.Tail(15))));
                    }
                }
            }
            if (tests is not null)
                secrets.AddRange(await ScanSecretsAsync(after, changes, Path.Combine(dir, "gitleaks"), ct));

            // The code before the change, with only the new and changed test files.
            var testFiles = changes.Where(c => c.Kind != ChangeKind.Deleted && CodePaths.IsTest(c.Path)).Select(c => c.Path).ToList();
            if (tests is not null && (testFiles.Count > 0 || lintCommands.Count > 0))
            {
                log?.Invoke("  verify: the code before the change" + (testFiles.Count > 0 ? ", with the new tests" : ""));
                await Workspace.CreateAsync(before, env);
                await using var ws = await Workspace.OpenAsync(before.Volume, env, limits);
                for (var i = 0; i < lint.Count; i++)
                {
                    var r = await ws.BashAsync(LintScript(env, lint[i].Command), LintTimeout, ct);
                    lint[i] = lint[i] with { Before = r.TimedOut ? "timeout" : r.Ok ? "pass" : "fail" };
                }
                var newTests = tests.NewPassing;
                if (testFiles.Count > 0)
                {
                    if (await ws.ApplyAsync(patch, testFiles, ct) is { } e)
                        repro = new ReproCheck(ImplementTools.NeedsTest(prp), [], [], $"the test files don't apply on their own: {e}");
                    else
                    {
                        var run = await ws.RunTestsAsync(Path.Combine(dir, "tests-before"), ct);
                        var passedBefore = run.Outcomes.Where(o => o.Status == Recon.Reports.TestStatus.Passed).Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
                        repro = new ReproCheck(ImplementTools.NeedsTest(prp), newTests.Where(t => !passedBefore.Contains(t)).ToList(), newTests.Where(passedBefore.Contains).ToList());
                    }
                }
            }
            repro ??= tests is null ? null : new ReproCheck(ImplementTools.NeedsTest(prp), [], []);
            if (repro is { Ok: false })
                blockers.Add(repro.Error ?? (tests!.NewPassing.Count == 0
                    ? "fail-to-pass: the PRP needs a test, and the change adds no new passing test"
                    : $"fail-to-pass: none of the {tests.NewPassing.Count} new test(s) fails on the code before the change, so none shows the change is needed"));
            foreach (var l in lint.Where(l => l.Broke)) blockers.Add($"lint: `{l.Command}` passed before the change and fails with it: {l.Tail}");
            foreach (var s in secrets) blockers.Add($"secret: gitleaks rule {s.Rule} on an added line, {s.Path}:{s.Line}");
        }
        finally
        {
            await after.RemoveAsync();
            await before.RemoveAsync();
        }

        ReviewReport? review = null;
        if (blockers.Count == 0 && reviewer is not null)
        {
            var facts = new VerifyReport(round, true, [], guard, tests, repro, lint, secrets, null, changes.Count, changes.Sum(c => c.Added.Count), changes.Sum(c => c.Removed.Count), 0);
            review = await new Reviewer(reviewer, reviewOptions ?? new ReviewOptions(), log).RunAsync(job, round, patch, facts, submission, ct);
            blockers.AddRange(review.Blocking());
        }

        var report = new VerifyReport(round, blockers.Count == 0, blockers, guard, tests, repro, lint, secrets, review,
            changes.Count, changes.Sum(c => c.Added.Count), changes.Sum(c => c.Removed.Count), Math.Round(clock.Elapsed.TotalSeconds, 1));
        job.WriteJson("verify", ImplementTools.RoundDir(round) + "/verify.json", report);
        job.WriteJson("verify", "verify.json", report);
        job.Event("verify", "verified", new { round, report.Passed, blockers = blockers.Count, regressions = tests?.Regressions.Count, reproducing = repro?.Reproducing.Count, review = review?.Verdict });
        return report;
    }

    /// <summary>A CI lint command, run the way the test script runs: its environment lines (a virtualenv) first.</summary>
    internal static string LintScript(TestEnvironment env, string command)
    {
        var prelude = env.TestScript.Replace("\r\n", "\n").Split('\n')
            .Where(l => l.TrimStart() is var t && (t.StartsWith(". ", StringComparison.Ordinal) || t.StartsWith("source ", StringComparison.Ordinal) || t.StartsWith("export ", StringComparison.Ordinal)));
        return string.Join('\n', [.. prelude, command]);
    }

    /// <summary>The changed files as they are after the change, for the reviewer: text only, a size cap.</summary>
    private static async Task CopyChangedFilesAsync(Workspace ws, IReadOnlyList<FileChange> changes, string hostDir, CancellationToken ct)
    {
        var total = 0;
        foreach (var c in changes.Where(c => c.Kind != ChangeKind.Deleted && !c.Binary))
        {
            var (rel, _) = ImplementTools.Resolve(c.Path);
            if (rel is null || rel == ".") continue;
            var r = await ws.BashAsync($"base64 -w0 {Workspace.Quote($"{DockerSandbox.RepoPath}/{rel}")}", TimeSpan.FromSeconds(60), ct);
            if (!r.Ok) continue;
            var bytes = Convert.FromBase64String(r.Stdout.Trim());
            if ((total += bytes.Length) > MaxChangedFileBytes) break;
            var target = Path.GetFullPath(Path.Combine(hostDir, rel));
            if (!target.StartsWith(Path.GetFullPath(hostDir), StringComparison.Ordinal)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, bytes);
        }
    }

    /// <summary>gitleaks over each changed file in the sandbox, network off; only findings on added lines count.</summary>
    private static async Task<IReadOnlyList<SecretHit>> ScanSecretsAsync(DockerSandbox sandbox, IReadOnlyList<FileChange> changes, string hostDir, CancellationToken ct)
    {
        var files = changes.Where(c => c.Kind != ChangeKind.Deleted && !c.Binary && c.Added.Count > 0).ToList();
        if (files.Count == 0) return [];
        var image = await SecurityStage.EnsureImageAsync(ct);
        var end = "FOREMAN_PATHS_" + Guid.NewGuid().ToString("N");
        var script = new StringBuilder("cd /work/repo\ni=0\nwhile IFS= read -r f; do i=$((i+1)); [ -f \"$f\" ] && gitleaks dir --redact --no-banner --exit-code 0 --report-format json --report-path \"$OUT/leaks-$i.json\" --log-level warn \"$f\"; done <<'" + end + "'\n");
        foreach (var f in files) script.Append(f.Path).Append('\n');
        script.Append(end).Append('\n');
        Dirs.Delete(hostDir);
        await sandbox.RunAsync(new SandboxRun(image, script.ToString(), Network.None, TimeSpan.FromMinutes(5)), hostDir, ct);
        var hits = new List<SecretHit>();
        for (var i = 1; i <= files.Count; i++)
        {
            var report = Path.Combine(hostDir, $"leaks-{i}.json");
            if (!File.Exists(report)) continue;
            var added = files[i - 1].Added.Select(a => a.Line).ToHashSet();
            hits.AddRange(SecurityStage.Gitleaks(File.ReadAllText(report)).Where(s => added.Contains(s.Line)).Select(s => new SecretHit(s.Rule, files[i - 1].Path, s.Line)));
        }
        return hits;
    }

    private static string Last(string s) => s.Length <= 1500 ? s.Trim() : "…" + s[^1500..].Trim();
}
