using System.Text;
using System.Text.Json;
using Foreman.Core;
using Foreman.Planning.Clarify;
using Foreman.Planning.Spec;

namespace Foreman.Evals;

/// <param name="Localization">A real issue: its job comes from the localization eval and has gold files.</param>
/// <param name="Note">What the case tests; written tasks reuse the clarify eval's job.</param>
public sealed record SpecCase(string Id, bool Localization = false, string? Note = null);

public sealed record SpecRun(
    string Id, int Run, bool Delivered, int Submissions, int Warnings, int Requirements, int Executable, int AcceptanceTests,
    int Examples, int Tasks, int AllowedEdits, IReadOnlyList<string> TouchPoints, double? GoldRecall, IReadOnlyList<string> GoldMissed,
    int Questions, decimal CostUsd, bool CostIsEstimate, double Seconds, int ToolCalls, string? Error);

public sealed record SpecEvalReport(DateTime At, string Model, int Cases, int Runs, IReadOnlyList<SpecRun> Results);

/// <summary>
/// The spec stage on the clarify eval's tasks: clarify again, the defaults accepted (no client here), then the PRP.
/// Code already enforces the done contract, so the eval reports what code can't: how many requirements are checked
/// by something executable, and on the real issues whether the PRP's touch points include the files the real fix
/// changed (the PRP can be right with another file; the gold files are a check, not the only answer).
/// </summary>
public sealed class SpecEval(string home, IAgentDriver driver, string? model = null, Action<string>? log = null)
{
    public static IReadOnlyList<SpecCase> ReadCases(string path) =>
        JsonSerializer.Deserialize<List<SpecCase>>(File.ReadAllText(path), LocalizationEval.CaseJson) ?? [];

    public async Task<SpecEvalReport> RunAsync(IReadOnlyList<SpecCase> cases, string casesFile, int runs, CancellationToken ct)
    {
        var localization = LocalizationEval.ReadCases(Path.Combine(Path.GetDirectoryName(casesFile)!, "..", "localization", "cases.json"))
            .ToDictionary(c => c.Id, StringComparer.Ordinal);
        var results = new List<SpecRun>();
        foreach (var c in cases)
        {
            var id = c.Localization ? LocalizationEval.JobId(localization[c.Id]) : $"eval-clarify-{c.Id.ToLowerInvariant()}";
            if (!Directory.Exists(Path.Combine(home, "jobs", id))) throw new InvalidOperationException($"{c.Id}: job {id} is missing; run the localization and clarify evals first");
            var job = Job.Open(home, id);
            var gold = c.Localization ? localization[c.Id].GoldFiles : null;
            for (var run = 1; run <= runs; run++)
            {
                string? error = null;
                void Log(string s) { if (s.Contains("FAILED", StringComparison.Ordinal)) error = s; }
                var ok = await new Pipeline([new ClarifyStage(driver)]).RunAsync(job, new HashSet<string> { "clarify" }, Log, ct);
                var questions = ok ? job.ReadJson<List<Gap>>("clarify", "questions.json").Count(g => g.Resolution == "ask") : 0;
                if (ok && questions > 0) Answers.Record(job, new Dictionary<string, string>(), defaults: true);
                ok = ok && await new Pipeline([new SpecStage(driver, new SpecOptions(model ?? SpecOptions.DefaultModel))]).RunAsync(job, new HashSet<string> { "spec" }, Log, ct);
                results.Add(Score(c, run, job, gold, questions, ok ? null : error ?? "failed"));
                var r = results[^1];
                log?.Invoke($"{c.Id} #{run}: {(r.Delivered ? $"{r.Requirements} requirements ({r.Executable} executable, {r.AcceptanceTests} by acceptance test), {r.Examples} examples, {r.Tasks} tasks, {r.Submissions} submissions{(r.GoldRecall is { } g ? $", gold files {g:P0}" : "")}, {r.Seconds:0} s" : r.Error)}" +
                    (r.GoldMissed.Count > 0 ? $"\n    missed {string.Join(", ", r.GoldMissed)}; touched {string.Join(", ", r.TouchPoints)}" : ""));
            }
        }
        return new SpecEvalReport(DateTime.UtcNow, model ?? SpecOptions.DefaultModel, cases.Count, runs, results);
    }

    internal static SpecRun Score(SpecCase c, int run, Job job, IReadOnlyList<string>? gold, int questions, string? error)
    {
        var reportFile = Path.Combine(job.Dir, "spec", "spec.json");
        if (error is not null || !File.Exists(reportFile))
            return new SpecRun(c.Id, run, false, 0, 0, 0, 0, 0, 0, 0, 0, [], null, [], questions, 0, false, 0, 0, error ?? "no PRP");
        var report = job.ReadJson<SpecReport>("spec", "spec.json");
        var d = job.ReadJson<Prp>("spec", "prp.json").Spec;
        var touched = d.TouchPoints.Select(t => t.Path).ToList();
        var missed = gold?.Where(g => !touched.Contains(g, StringComparer.Ordinal)).ToList() ?? [];
        return new SpecRun(c.Id, run, true, report.Submissions, report.Warnings.Count, d.Requirements.Count,
            d.Requirements.Count(r => r.Verify.Any(v => v.Kind != "review")), d.Requirements.Count(r => r.Verify.Any(v => v.Kind == "acceptance_test")),
            d.Examples.Count, d.Tasks.Count, d.AllowedEdits.Count, touched, gold is null ? null : (gold.Count - missed.Count) / (double)gold.Count, missed,
            questions, report.Agent.CostUsd, report.Agent.CostIsEstimate, report.Agent.Seconds, report.Agent.ToolCalls, null);
    }

    public static string Markdown(SpecEvalReport r, IReadOnlyList<SpecCase> cases)
    {
        var done = r.Results.Where(x => x.Delivered).ToList();
        var real = done.Where(x => x.GoldRecall is not null).ToList();
        var sb = new StringBuilder("# Spec eval\n\n");
        sb.Append($"{r.At:yyyy-MM-dd}, `{r.Model}`, {r.Cases} cases × {r.Runs} runs; clarify ran first and its defaults were accepted. ");
        sb.Append($"Delivered {done.Count}/{r.Results.Count}; accepted on the first submission {done.Count(x => x.Submissions == 1)}, kept with warnings {done.Count(x => x.Warnings > 0)}. ");
        if (done.Count > 0)
            sb.Append($"Requirements checked by something executable: {done.Sum(x => x.Executable)}/{done.Sum(x => x.Requirements)}. ");
        if (real.Count > 0)
            sb.Append($"On the real issues the touch points include the fix's files: {real.Average(x => x.GoldRecall!.Value):P0} on average, all of them in {real.Count(x => x.GoldRecall == 1)}/{real.Count} runs. ");
        sb.Append($"Cost {r.Results.Sum(x => x.CostUsd):0.00} USD{(r.Results.Any(x => x.CostIsEstimate) ? " (list-price estimate; the runs were on a subscription)" : "")}, ");
        sb.Append($"{(done.Count == 0 ? 0 : done.Average(x => x.Seconds)):0} s per PRP on average.\n\n");
        sb.Append("| Case | What it is | Requirements (executable / acceptance test) | Examples | Tasks | Fix's files in touch points | Submissions | Time |\n|---|---|---|---|---|---|---|---|\n");
        foreach (var x in r.Results)
        {
            var c = cases.First(k => k.Id == x.Id);
            sb.Append($"| {x.Id}{(r.Runs > 1 ? $" #{x.Run}" : "")} | {(c.Localization ? "real issue" : c.Note)} | ");
            sb.Append(x.Delivered
                ? $"{x.Requirements} ({x.Executable} / {x.AcceptanceTests}) | {x.Examples} | {x.Tasks} | {(x.GoldRecall is { } g ? $"{g:P0}{(x.GoldMissed.Count > 0 ? $" (missed `{string.Join("`, `", x.GoldMissed)}`)" : "")}" : "")} | {x.Submissions}{(x.Warnings > 0 ? $", {x.Warnings} warnings" : "")} | {x.Seconds:0} s |\n"
                : $"failed: {x.Error} | | | | | |\n");
        }
        sb.Append("\nThe written tasks are synthetic (see `../clarify/tasks`); the issues are fetched from GitHub by `foreman eval localization` and are not stored here. The PRPs themselves stay in the job folders.\n");
        return sb.ToString();
    }
}
