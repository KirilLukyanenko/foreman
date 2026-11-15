using System.Globalization;
using System.Text;
using System.Text.Json;
using Foreman.Core;
using Foreman.Planning.Clarify;
using Foreman.Planning.Intake;
using Foreman.Recon;
using Foreman.Recon.CodeMap;

namespace Foreman.Evals;

public sealed record ClarifyExpect(
    IReadOnlyList<string>? Verdict = null,
    int? MinQuestions = null,
    int? MaxQuestions = null,
    int? MinRepository = null,
    string? Language = null);

/// <param name="Task">A task written for this eval (synthetic, committed), relative to the cases file.</param>
/// <param name="RepoFrom">The localization case whose repository (at its base commit) the task is about.</param>
/// <param name="Localization">The task is the real issue of that localization case.</param>
public sealed record ClarifyCase(string Id, ClarifyExpect Expect, string? Task = null, string? RepoFrom = null, bool Localization = false, string? Note = null);

public sealed record ClarifyRun(
    string Id, int Run, bool Delivered, string? Verdict, int Questions, int Assumptions, int FromRepository, int Warnings,
    IReadOnlyList<IntakeCheck> Checks, IReadOnlyList<string> Asked, decimal CostUsd, bool CostIsEstimate, double Seconds, int ToolCalls, string? Error);

public sealed record ClarifyEvalReport(DateTime At, string Model, int Cases, int Runs, int Checks, int Passed, IReadOnlyList<ClarifyRun> Results);

/// <summary>
/// Clarify on labelled tasks. Two kinds: tasks written for this eval against real repositories, where the right
/// behaviour is known (the library already has what is asked for; a feature with real open choices; a Russian
/// client), and the real issues of the localization eval, which were fixed as written, so asking a lot there is
/// over-asking: the failure "Ambig-SWE" (ICLR 2026) found models make most, telling clear tasks from unclear ones.
/// Each run is intake then clarify on the same repository; recon's code map is reused between runs.
/// </summary>
public sealed class ClarifyEval(string home, IAgentDriver driver, string? model = null, Action<string>? log = null)
{
    public static IReadOnlyList<ClarifyCase> ReadCases(string path) =>
        JsonSerializer.Deserialize<List<ClarifyCase>>(File.ReadAllText(path), LocalizationEval.CaseJson) ?? [];

    public async Task<ClarifyEvalReport> RunAsync(IReadOnlyList<ClarifyCase> cases, string casesFile, int runs, CancellationToken ct)
    {
        var localization = LocalizationEval.ReadCases(Path.Combine(Path.GetDirectoryName(casesFile)!, "..", "localization", "cases.json"))
            .ToDictionary(c => c.Id, StringComparer.Ordinal);
        var clarify = new ClarifyStage(driver, new ClarifyOptions(model ?? ClarifyOptions.DefaultModel));
        var results = new List<ClarifyRun>();
        foreach (var c in cases)
        {
            var job = await PrepareAsync(c, casesFile, localization, ct);
            for (var run = 1; run <= runs; run++)
            {
                string? error = null;
                var ok = await new Pipeline([new IntakeStage(driver), clarify]).RunAsync(job, new HashSet<string> { "intake", "clarify" },
                    s => { if (s.Contains("FAILED", StringComparison.Ordinal)) error = s; }, ct);
                results.Add(Score(c, run, job, ok ? null : error ?? "failed"));
                var r = results[^1];
                log?.Invoke($"{c.Id} #{run}: {r.Verdict}, {r.Questions} asked, {r.Assumptions} assumed, {r.FromRepository} from code; {r.Checks.Count(x => x.Pass)}/{r.Checks.Count} checks, {r.ToolCalls} calls, {r.Seconds:0} s" +
                    string.Concat(r.Checks.Where(x => !x.Pass).Select(x => $"\n    FAIL {x.Field}: {x.Got}")) + string.Concat(r.Asked.Select(q => $"\n    ? {q}")));
            }
        }
        var all = results.SelectMany(r => r.Checks).ToList();
        return new ClarifyEvalReport(DateTime.UtcNow, model ?? ClarifyOptions.DefaultModel, cases.Count, runs, all.Count, all.Count(x => x.Pass), results);
    }

    /// <summary>A real issue reuses its localization job; a written task gets a job on a local clone of that repository.</summary>
    private async Task<Job> PrepareAsync(ClarifyCase c, string casesFile, IReadOnlyDictionary<string, LocalizationCase> localization, CancellationToken ct)
    {
        if (c.Localization)
        {
            var id = LocalizationEval.JobId(localization[c.Id]);
            if (!Directory.Exists(Path.Combine(home, "jobs", id))) throw new InvalidOperationException($"{c.Id}: run 'foreman eval localization' first");
            return Job.Open(home, id);
        }
        var origin = Job.Open(home, LocalizationEval.JobId(localization[c.RepoFrom!]));
        var jobId = $"eval-clarify-{c.Id.ToLowerInvariant()}";
        var dir = Path.Combine(home, "jobs", jobId);
        Job job;
        if (Directory.Exists(dir)) job = Job.Open(home, jobId);
        else
        {
            var taskFile = Path.Combine(Path.GetTempPath(), $"foreman-{jobId}-task.md");
            File.Copy(Path.Combine(Path.GetDirectoryName(casesFile)!, c.Task!), taskFile, overwrite: true);
            job = Job.Create(home, origin.SourceDir, taskFile, jobId);
            File.Delete(taskFile);
        }
        var ok = await new Pipeline([new FetchStage(new ReconOptions()), new TriageStage(), new CodeMapStage(new CodeMapOptions())])
            .RunAsync(job, log: s => log?.Invoke($"  {c.Id}: {s}"), ct: ct);
        if (!ok) throw new InvalidOperationException($"{c.Id}: recon failed, see {job.Dir}");
        return job;
    }

    internal static ClarifyRun Score(ClarifyCase c, int run, Job job, string? error)
    {
        var reportFile = Path.Combine(job.Dir, "clarify", "clarify.json");
        if (error is not null || !File.Exists(reportFile))
            return new ClarifyRun(c.Id, run, false, null, 0, 0, 0, 0, [new IntakeCheck("delivered", false, error ?? "no result")], [], 0, false, 0, 0, error);
        var r = Json.Read<ClarifyReport>(File.ReadAllText(reportFile))!;
        var gaps = job.ReadJson<List<Gap>>("clarify", "questions.json");
        var brief = job.ReadJson<Brief>("intake", "brief.json");
        var intake = job.ReadJson<IntakeReport>("intake", "intake.json");
        var e = c.Expect;
        var checks = new List<IntakeCheck>();
        void Check(string field, bool pass, string got) => checks.Add(new IntakeCheck(field, pass, got));
        Check("accepted cleanly", r.Warnings.Count == 0, r.Warnings.Count == 0 ? "yes" : string.Join("; ", r.Warnings));
        if (e.Verdict is not null) Check("verdict", e.Verdict.Contains(r.Verdict), r.Verdict);
        if (e.MinQuestions is { } min) Check($"at least {min} questions", r.Questions >= min, Num(r.Questions));
        if (e.MaxQuestions is { } max) Check($"at most {max} questions", r.Questions <= max, Num(r.Questions));
        if (e.MinRepository is { } repo) Check($"at least {repo} answered by the code", r.FromRepository >= repo, Num(r.FromRepository));
        if (e.Language is not null) Check("client language", brief.Language == e.Language, brief.Language);
        var asked = gaps.Where(g => g.Resolution == "ask").Select(g => $"{g.Id} ({g.Impact}) {g.Question}").ToList();
        return new ClarifyRun(c.Id, run, true, r.Verdict, r.Questions, r.Assumptions, r.FromRepository, r.Warnings.Count, checks, asked,
            r.Agent.CostUsd + intake.Agent.CostUsd, r.Agent.CostIsEstimate, r.Agent.Seconds, r.Agent.ToolCalls, null);
    }

    private static string Num(int n) => n.ToString(CultureInfo.InvariantCulture);

    public static string Markdown(ClarifyEvalReport r, IReadOnlyList<ClarifyCase> cases)
    {
        var sb = new StringBuilder();
        sb.Append("# Clarify eval\n\n");
        sb.Append($"{r.At:yyyy-MM-dd}, `{r.Model}` (intake on its default), {r.Cases} cases × {r.Runs} runs. Checks passed: **{r.Passed}/{r.Checks}**. ");
        var real = r.Results.Where(x => cases.First(c => c.Id == x.Id).Localization).ToList();
        if (real.Count > 0)
            sb.Append($"On the real issues (fixed as written): {real.Average(x => x.Questions):0.0} questions per run on average, {real.Count(x => x.Questions == 0)}/{real.Count} runs asked nothing. ");
        sb.Append($"Cost {r.Results.Sum(x => x.CostUsd):0.00} USD{(r.Results.Any(x => x.CostIsEstimate) ? " (list-price estimate; the runs were on a subscription)" : "")}.\n\n");
        sb.Append("| Case | What it tests | Verdicts | Asked / assumed / from code per run | Checks | Failed |\n|---|---|---|---|---|---|\n");
        foreach (var g in r.Results.GroupBy(x => x.Id))
        {
            var c = cases.First(x => x.Id == g.Key);
            var checks = g.SelectMany(x => x.Checks).ToList();
            var failed = string.Join("; ", g.SelectMany(x => x.Checks.Where(k => !k.Pass).Select(k => $"#{x.Run} {k.Field} = {k.Got}")));
            sb.Append($"| {g.Key} | {(c.Localization ? "real issue" : c.Note)} | {string.Join(", ", g.Select(x => x.Verdict ?? "none"))} | {string.Join("; ", g.Select(x => $"{x.Questions}/{x.Assumptions}/{x.FromRepository}"))} | {checks.Count(k => k.Pass)}/{checks.Count} | {failed} |\n");
        }
        sb.Append("\n## Questions asked\n\n");
        foreach (var x in r.Results.Where(x => x.Asked.Count > 0))
        {
            sb.Append($"- **{x.Id}** #{x.Run}\n");
            foreach (var q in x.Asked) sb.Append($"  - {q.Replace("\n", " ")}\n");
        }
        sb.Append("\nThe written tasks are synthetic, made for this eval; the issues are fetched from GitHub by `foreman eval localization` and are not stored here.\n");
        return sb.ToString();
    }
}
