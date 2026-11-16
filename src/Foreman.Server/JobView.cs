using Foreman.Core;
using Foreman.Implement;
using Foreman.Planning.Clarify;
using Foreman.Planning.Intake;

namespace Foreman.Server;

/// <summary>How the service started a job; kept in <c>serve.json</c> in the job's folder.</summary>
/// <param name="Go">Go on past intake's no-go (the human said so).</param>
/// <param name="Acceptance">Write hidden acceptance tests before implementing.</param>
/// <param name="Commit">Check out this commit instead of the default branch's tip; <paramref name="Ref"/> a branch or tag.</param>
public sealed record ServeMeta(DateTime CreatedAt, bool Go = false, bool Acceptance = true, string? Commit = null, string? Ref = null)
{
    public const string File = "serve.json";

    public static ServeMeta? Read(Job job) =>
        System.IO.File.Exists(Path.Combine(job.Dir, File)) ? Json.Read<ServeMeta>(System.IO.File.ReadAllText(Path.Combine(job.Dir, File))) : null;

    public void Write(Job job) => System.IO.File.WriteAllText(Path.Combine(job.Dir, File), Json.Write(this));
}

/// <summary>
/// Where a job stands, as Tower shows it.
/// <c>queued</c> and <c>running</c> come from the service; the rest from the job's files:
/// <c>waiting_answers</c> (the client's answers to clarify's questions), <c>waiting_ready</c> (the PRP to be approved),
/// <c>waiting_go</c> (intake said no-go), <c>verified</c> (a patch and a PR text are ready for the human),
/// <c>not_verified</c> or <c>gave_up</c> (implement stopped with a reason), <c>failed</c> (a stage failed),
/// <c>interrupted</c> (a stage was running when the service stopped), <c>new</c>.
/// </summary>
public sealed record JobView(string Id, string Source, string? Title, string Phase, string? Stage, string? Detail, DateTime Created, DateTime? Updated);

public static class JobViews
{
    /// <summary>The pipeline's stage names in order, for "which stage is it at".</summary>
    public static readonly string[] Order = ["intake", "go", "fetch", "triage", "security", "codemap", "environment", "baseline", "dossier", "clarify", "spec", "accept", "implement", "deliver"];

    public static JobView Of(Job job, string? activity = null)
    {
        var states = job.ReadState();
        var title = System.IO.File.Exists(Path.Combine(job.Dir, "intake", "brief.json")) ? job.ReadJson<Brief>("intake", "brief.json").Title : null;
        var updated = states.Values.Select(s => s.FinishedAt ?? s.StartedAt).Where(t => t is not null).DefaultIfEmpty(null).Max();
        JobView View(string phase, string? stage, string? detail) => new(job.Id, job.Manifest.Source, title, phase, stage, detail, job.Manifest.CreatedAt, updated);

        var ordered = Order.Where(states.ContainsKey).Select(n => (Name: n, State: states[n])).ToList();
        if (activity is "queued" or "running")
            return View(activity, activity == "running" ? ordered.LastOrDefault(s => s.State.Status == StageStatus.Running).Name : null, null);

        if (ordered.FirstOrDefault(s => s.State.Status == StageStatus.Waiting) is { Name: not null } w)
            return View(w.Name switch { "go" => "waiting_go", "spec" => "waiting_answers", _ => "waiting_ready" }, w.Name, w.State.Error);
        if (ordered.FirstOrDefault(s => s.State.Status == StageStatus.Failed) is { Name: not null } f)
        {
            if (f.Name == "implement" && System.IO.File.Exists(Path.Combine(job.Dir, "implement", "implement.json")))
            {
                var report = job.ReadJson<ImplementReport>("implement", "implement.json");
                return View(report.Status == "gave_up" ? "gave_up" : "not_verified", f.Name, report.Reason ?? f.State.Error);
            }
            return View("failed", f.Name, f.State.Error);
        }
        if (ordered.FirstOrDefault(s => s.State.Status == StageStatus.Running) is { Name: not null } r)
            return View("interrupted", r.Name, "the service stopped while this stage was running; running the job again resumes it");
        if (states.TryGetValue("implement", out var impl) && impl.Status == StageStatus.Done)
            return View("verified", states.TryGetValue("deliver", out var deliver) && deliver.Status == StageStatus.Done ? "deliver" : "implement", null);
        return View("new", ordered.LastOrDefault().Name, null);
    }

    /// <summary>The client's questions while the job waits for answers.</summary>
    public static IReadOnlyList<object> Questions(Job job) =>
        System.IO.File.Exists(Path.Combine(job.Dir, "clarify", "questions.json"))
            ? job.ReadJson<List<Gap>>("clarify", "questions.json").Where(g => g.Resolution == "ask")
                .Select(g => (object)new { id = g.Id, question = g.Question, defaultAnswer = g.ClientDefault, options = g.Options, topic = g.Topic }).ToList()
            : [];
}
