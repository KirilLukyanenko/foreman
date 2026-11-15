using System.Globalization;
using System.Text;
using System.Text.Json;
using Foreman.Core;
using Foreman.Planning.Intake;

namespace Foreman.Evals;

public sealed record IntakeExpect(
    IReadOnlyList<string>? Verdict = null,
    IReadOnlyList<string>? TaskType = null,
    string? Language = null,
    IReadOnlyList<string>? Kind = null,
    IReadOnlyList<string>? Audiences = null,
    IReadOnlyList<string>? NoAudiences = null,
    bool? Deadline = null,
    bool? Budget = null,
    int? MinUnknowns = null);

/// <param name="Post">A job post written for this eval (synthetic, committed), relative to the cases file.</param>
/// <param name="Localization">The task is a real issue from the localization eval, already fetched into its job.</param>
public sealed record IntakeCase(string Id, IntakeExpect Expect, string? Post = null, bool Localization = false, string? Repo = null, string? Note = null);

public sealed record IntakeCheck(string Field, bool Pass, string Got);

public sealed record IntakeRun(string Id, int Run, bool Delivered, int Submissions, int Warnings, IReadOnlyList<IntakeCheck> Checks, decimal CostUsd, bool CostIsEstimate, double Seconds, string? Error);

public sealed record IntakeEvalReport(DateTime At, string Model, int Cases, int Runs, int Checks, int Passed, int CleanSubmissions, IReadOnlyList<IntakeRun> Results);

/// <summary>
/// Intake on labelled tasks: synthetic job posts written for this eval (an AI trap in a proposal request, a Russian
/// post, a hidden instruction to an agent, a no-go, a vague post, a deceptive request) and the real issues of the
/// localization eval. Each run is checked field by field against the labels; "clean" means accepted on the first
/// submission with every quote found in the task.
/// </summary>
public sealed class IntakeEval(string home, IAgentDriver driver, string? model = null, Action<string>? log = null)
{
    public static IReadOnlyList<IntakeCase> ReadCases(string path) =>
        JsonSerializer.Deserialize<List<IntakeCase>>(File.ReadAllText(path), LocalizationEval.CaseJson) ?? [];

    public async Task<IntakeEvalReport> RunAsync(IReadOnlyList<IntakeCase> cases, string casesFile, int runs, CancellationToken ct)
    {
        var localization = LocalizationEval.ReadCases(Path.Combine(Path.GetDirectoryName(casesFile)!, "..", "localization", "cases.json"))
            .ToDictionary(c => c.Id, StringComparer.Ordinal);
        var stage = new IntakeStage(driver, new IntakeOptions(model ?? IntakeOptions.DefaultModel));
        var results = new List<IntakeRun>();
        foreach (var c in cases)
        {
            string task, source;
            if (c.Localization)
            {
                var l = localization[c.Id];
                var origin = Path.Combine(home, "jobs", LocalizationEval.JobId(l), "task.md");
                if (!File.Exists(origin)) throw new InvalidOperationException($"{c.Id}: run 'foreman eval localization' first, it fetches the issue text");
                task = File.ReadAllText(origin);
                source = l.Repo;
            }
            else
            {
                task = File.ReadAllText(Path.Combine(Path.GetDirectoryName(casesFile)!, c.Post!));
                source = c.Repo ?? "https://example.invalid/no-repository.git";
            }

            for (var run = 1; run <= runs; run++)
            {
                var id = $"eval-intake-{c.Id.ToLowerInvariant()}-{run}";
                var dir = Path.Combine(home, "jobs", id);
                if (Directory.Exists(dir)) Dirs.Delete(dir);
                var taskFile = Path.Combine(Path.GetTempPath(), $"foreman-{id}-task.md");
                File.WriteAllText(taskFile, task, new UTF8Encoding(false));
                var job = Job.Create(home, source, taskFile, id);
                File.Delete(taskFile);

                string? error = null;
                try { await stage.RunAsync(job, ct); }
                catch (InvalidOperationException e) { error = e.Message; }
                results.Add(Score(c, run, job, error));
                var r = results[^1];
                log?.Invoke($"{c.Id} #{run}: {r.Checks.Count(x => x.Pass)}/{r.Checks.Count} checks, {r.Submissions} submissions, {r.Seconds:0} s{(error is null ? "" : $", {error}")}" +
                    string.Concat(r.Checks.Where(x => !x.Pass).Select(x => $"\n    FAIL {x.Field}: {x.Got}")));
            }
        }
        var all = results.SelectMany(r => r.Checks).ToList();
        return new IntakeEvalReport(DateTime.UtcNow, model ?? IntakeOptions.DefaultModel, cases.Count, runs, all.Count, all.Count(x => x.Pass),
            results.Count(r => r.Delivered && r.Submissions == 1 && r.Warnings == 0), results);
    }

    internal static IntakeRun Score(IntakeCase c, int run, Job job, string? error)
    {
        var reportFile = Path.Combine(job.Dir, "intake", "intake.json");
        var briefFile = Path.Combine(job.Dir, "intake", "brief.json");
        if (!File.Exists(briefFile))
            return new IntakeRun(c.Id, run, false, 0, 0, [new IntakeCheck("delivered", false, error ?? "no brief")], 0, false, 0, error);
        var brief = Json.Read<Brief>(File.ReadAllText(briefFile))!;
        var report = Json.Read<IntakeReport>(File.ReadAllText(reportFile))!;
        var e = c.Expect;
        var checks = new List<IntakeCheck>();
        void Check(string field, bool pass, string got) => checks.Add(new IntakeCheck(field, pass, got));
        if (e.Verdict is not null) Check("verdict", e.Verdict.Contains(brief.Fit.Verdict), brief.Fit.Verdict);
        if (e.TaskType is not null) Check("task_type", e.TaskType.Contains(brief.TaskType), brief.TaskType);
        if (e.Language is not null) Check("language", e.Language == brief.Language, brief.Language);
        if (e.Kind is not null) Check("kind", e.Kind.Contains(brief.Kind), brief.Kind);
        var audiences = brief.EmbeddedInstructions.Select(x => x.Audience).Distinct().ToList();
        foreach (var a in e.Audiences ?? []) Check($"finds {a} text", audiences.Contains(a), string.Join(", ", audiences));
        foreach (var a in e.NoAudiences ?? []) Check($"no {a} text", !audiences.Contains(a), string.Join(", ", audiences));
        if (e.Deadline is { } d) Check("deadline", (brief.Deadline is not null) == d, brief.Deadline?.Text ?? "none");
        if (e.Budget is { } b) Check("budget", (brief.Budget is not null) == b, brief.Budget?.Text ?? "none");
        if (e.MinUnknowns is { } u) Check($"at least {u} unknowns", brief.Unknowns.Count >= u, brief.Unknowns.Count.ToString(CultureInfo.InvariantCulture));
        return new IntakeRun(c.Id, run, true, report.Submissions, report.Warnings.Count, checks, report.Agent.CostUsd, report.Agent.CostIsEstimate, report.Agent.Seconds, error);
    }

    public static string Markdown(IntakeEvalReport r, IReadOnlyList<IntakeCase> cases)
    {
        var sb = new StringBuilder();
        sb.Append("# Intake eval\n\n");
        sb.Append($"{r.At:yyyy-MM-dd}, `{r.Model}`, {r.Cases} cases × {r.Runs} runs. Checks passed: **{r.Passed}/{r.Checks}**. ");
        sb.Append($"Accepted on the first submission with every quote found: {r.CleanSubmissions}/{r.Results.Count}. ");
        var cost = r.Results.Sum(x => x.CostUsd);
        sb.Append($"Cost {cost:0.000} USD{(r.Results.Any(x => x.CostIsEstimate) ? " (list-price estimate; the runs were on a subscription)" : "")}, ");
        sb.Append($"median {Median(r.Results.Select(x => x.Seconds)):0} s per run.\n\n");
        sb.Append("| Case | What it tests | Checks | Submissions | Failed |\n|---|---|---|---|---|\n");
        foreach (var g in r.Results.GroupBy(x => x.Id))
        {
            var c = cases.First(x => x.Id == g.Key);
            var checks = g.SelectMany(x => x.Checks).ToList();
            var failed = string.Join("; ", g.SelectMany(x => x.Checks.Where(k => !k.Pass).Select(k => $"#{x.Run} {k.Field} = {k.Got}")));
            sb.Append($"| {g.Key} | {(c.Localization ? "real issue" : c.Note)} | {checks.Count(k => k.Pass)}/{checks.Count} | {string.Join(", ", g.Select(x => x.Submissions))} | {(failed.Length == 0 ? "" : failed)} |\n");
        }
        sb.Append("\nThe job posts are synthetic, written for this eval; the issues are fetched from GitHub by `foreman eval localization` and are not stored here.\n");
        return sb.ToString();
    }

    private static double Median(IEnumerable<double> values)
    {
        var v = values.OrderBy(x => x).ToList();
        return v.Count == 0 ? 0 : v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2;
    }
}
