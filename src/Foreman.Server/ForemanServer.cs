using System.Security.Cryptography;
using System.Text;
using Foreman.Core;
using Foreman.Implement;
using Foreman.Planning.Clarify;
using Foreman.Planning.Spec;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Foreman.Server;

/// <param name="Token">Every /api call must carry <c>Authorization: Bearer &lt;token&gt;</c>.</param>
/// <param name="AllowLocalRepos">Accept a folder on this machine as the repository (tests and the developer's own use);
/// otherwise only https git URLs.</param>
public sealed record ServerOptions(string Home, string Token, string Urls = "http://127.0.0.1:8095", bool AllowLocalRepos = false);

public sealed record NewJob(string Repo, string Task, string? Commit = null, string? Ref = null, bool Go = false, bool Acceptance = true);

public sealed record AnswersRequest(IReadOnlyDictionary<string, string>? Answers, bool Defaults = false);

/// <summary>
/// <c>foreman serve</c>: the jobs over HTTP for Tower, and the runner that does them on this machine. Tower creates a job
/// from a task text and a repository; the job runs until it needs a human (the client's answers, the PRP's approval,
/// overriding a no-go) and goes on when Tower sends that. Nothing here pushes, sends or deploys: the result is a patch and
/// a PR text that the human takes from <c>/files</c>. Every /api call needs the bearer token; the service listens on
/// localhost unless told otherwise, and is meant to be reached through a private network (Tailscale), not the internet.
/// </summary>
public static class ForemanServer
{
    /// <summary>Files Tower may read from a job, by name. Nothing else in the job folder is served.</summary>
    public static readonly IReadOnlyDictionary<string, string> Files = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["brief.md"] = "intake/brief.md",
        ["questions.md"] = "clarify/questions.md",
        ["dossier.md"] = "dossier/dossier.md",
        ["prp.md"] = "spec/prp.md",
        ["pr.md"] = "implement/pr.md",
        ["report.md"] = "deliver/report.md",
        ["patch.diff"] = "implement/patch.diff",
        ["implement.json"] = "implement/implement.json",
        ["verify.json"] = "verify/verify.json",
        ["acceptance.json"] = "acceptance/acceptance.json",
        ["serve.log"] = "serve.log",
    };

    /// <summary>Sent by Tower's background refresh: such calls don't count as activity.</summary>
    public const string BackgroundHeader = "X-Foreman-Background";

    public static WebApplication Build(ServerOptions o, IJobRunner runner, IHostedService? worker = null, IHostedService? janitor = null)
    {
        if (o.Token.Length < 24) throw new InvalidOperationException("FOREMAN_TOKEN must be set to a random secret of at least 24 characters");
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(o.Urls);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddSingleton(runner);
        if (worker is not null) builder.Services.AddSingleton(worker);
        if (janitor is not null) builder.Services.AddSingleton(janitor);
        builder.Services.ConfigureHttpJsonOptions(j =>
        {
            j.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
            j.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        });
        var app = builder.Build();
        var expected = Encoding.UTF8.GetBytes("Bearer " + o.Token);
        // The last call a person made through Tower. Tower's background refresh says so in a header and doesn't count:
        // otherwise it would keep an on-demand machine from ever going idle.
        long lastCall = DateTime.MinValue.Ticks;

        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path.StartsWithSegments("/api")
                && !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(ctx.Request.Headers.Authorization.ToString()), expected))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            if (ctx.Request.Path.StartsWithSegments("/api") && ctx.Request.Headers[BackgroundHeader].Count == 0)
                Interlocked.Exchange(ref lastCall, DateTime.UtcNow.Ticks);
            await next();
        });

        // busy and idleSeconds let the machine power itself off when nothing has happened for a while (deploy/foreman-idle.sh).
        app.MapGet("/health", () =>
        {
            var call = new DateTime(Interlocked.Read(ref lastCall), DateTimeKind.Utc);
            var since = runner.IdleSince is { } idle ? (idle > call ? idle : call) : (DateTime?)null;
            return Results.Ok(new { ok = true, busy = since is null, idleSeconds = since is { } t ? (long)(DateTime.UtcNow - t).TotalSeconds : 0 });
        });

        var api = app.MapGroup("/api/jobs");
        api.MapGet("/", () => Results.Ok(ServeJobs(o.Home).Select(j => JobViews.Of(j, runner.Activity(j.Id))).OrderByDescending(v => v.Updated ?? v.Created).ToList()));

        api.MapPost("/", (NewJob r) =>
        {
            if (string.IsNullOrWhiteSpace(r.Task)) return Results.BadRequest(new { error = "task is empty" });
            if (!IsAllowedRepo(r.Repo, o.AllowLocalRepos)) return Results.BadRequest(new { error = "repo must be an https git URL" });
            var tasks = Path.Combine(o.Home, "tasks");
            Directory.CreateDirectory(tasks);
            var taskFile = Path.Combine(tasks, Guid.NewGuid().ToString("N") + ".md");
            File.WriteAllText(taskFile, r.Task, new UTF8Encoding(false));
            var job = Job.Create(o.Home, r.Repo, taskFile);
            File.Delete(taskFile);
            new ServeMeta(DateTime.UtcNow, r.Go, r.Acceptance, r.Commit, r.Ref).Write(job);
            runner.Enqueue(job.Id);
            return Results.Created($"/api/jobs/{job.Id}", JobViews.Of(job, runner.Activity(job.Id)));
        });

        api.MapGet("/{id}", (string id) => WithJob(o.Home, id, job =>
        {
            var view = JobViews.Of(job, runner.Activity(id));
            return Results.Ok(new
            {
                job = view,
                questions = view.Phase == "waiting_answers" ? JobViews.Questions(job) : null,
                prp = view.Phase == "waiting_ready" ? Read(job, "spec/prp.md") : null,
                pr = Read(job, "implement/pr.md"),
                report = Read(job, "deliver/report.md"),
                files = Files.Where(f => File.Exists(Path.Combine(job.Dir, f.Value))).Select(f => f.Key).ToList(),
                log = Tail(Read(job, "serve.log"), 40),
            });
        }));

        api.MapPost("/{id}/answers", (string id, AnswersRequest r) => WithJob(o.Home, id, job =>
        {
            Answers.Record(job, r.Answers ?? new Dictionary<string, string>(), r.Defaults);
            runner.Enqueue(id);
            return Results.Accepted($"/api/jobs/{id}", JobViews.Of(job, runner.Activity(id)));
        }));

        api.MapPost("/{id}/ready", (string id) => WithJob(o.Home, id, job =>
        {
            if (!File.Exists(Path.Combine(job.Dir, "spec", "prp.json"))) return Results.Conflict(new { error = "there is no PRP to approve yet" });
            Ready.Mark(job);
            runner.Enqueue(id);
            return Results.Accepted($"/api/jobs/{id}", JobViews.Of(job, runner.Activity(id)));
        }));

        api.MapPost("/{id}/go", (string id) => WithJob(o.Home, id, job =>
        {
            var meta = ServeMeta.Read(job) ?? new ServeMeta(DateTime.UtcNow);
            (meta with { Go = true }).Write(job);
            runner.Enqueue(id);
            return Results.Accepted($"/api/jobs/{id}", JobViews.Of(job, runner.Activity(id)));
        }));

        api.MapPost("/{id}/run", (string id) => WithJob(o.Home, id, job =>
            runner.Enqueue(id) ? Results.Accepted($"/api/jobs/{id}", JobViews.Of(job, runner.Activity(id))) : Results.Conflict(new { error = "the job is already queued or running" })));

        api.MapPost("/{id}/cancel", (string id) => WithJob(o.Home, id, job =>
            runner.Cancel(id) ? Results.Accepted($"/api/jobs/{id}") : Results.Conflict(new { error = "the job is not queued or running" })));

        api.MapGet("/{id}/files/{name}", (string id, string name) => WithJob(o.Home, id, job =>
        {
            if (!Files.TryGetValue(name, out var rel)) return Results.NotFound(new { error = $"no file '{name}'; files are {string.Join(", ", Files.Keys)}" });
            var path = Path.Combine(job.Dir, rel);
            return File.Exists(path) ? Results.Text(File.ReadAllText(path), name.EndsWith(".json", StringComparison.Ordinal) ? "application/json" : "text/plain; charset=utf-8") : Results.NotFound();
        }));
        return app;
    }

    /// <summary>The jobs this service created (they have <c>serve.json</c>); CLI and eval jobs are not listed.</summary>
    internal static IEnumerable<Job> ServeJobs(string home)
    {
        var jobs = Path.Combine(home, "jobs");
        if (!Directory.Exists(jobs)) yield break;
        foreach (var dir in Directory.EnumerateDirectories(jobs).Where(d => File.Exists(Path.Combine(d, ServeMeta.File))))
            yield return Job.Open(home, Path.GetFileName(dir));
    }

    internal static bool IsAllowedRepo(string repo, bool allowLocal) =>
        Uri.TryCreate(repo, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && !string.IsNullOrEmpty(u.Host) && string.IsNullOrEmpty(u.UserInfo)
        || allowLocal && Path.IsPathRooted(repo) && Directory.Exists(repo);

    private static IResult WithJob(string home, string id, Func<Job, IResult> action)
    {
        if (id.Length == 0 || id.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')) || id.Contains("..", StringComparison.Ordinal))
            return Results.BadRequest(new { error = "bad job id" });
        if (!File.Exists(Path.Combine(home, "jobs", id, "job.json")) || !File.Exists(Path.Combine(home, "jobs", id, ServeMeta.File)))
            return Results.NotFound(new { error = $"no job {id}" });
        try { return action(Job.Open(home, id)); }
        catch (InvalidOperationException e) { return Results.Conflict(new { error = e.Message }); }
    }

    private static string? Read(Job job, string rel) => File.Exists(Path.Combine(job.Dir, rel)) ? File.ReadAllText(Path.Combine(job.Dir, rel)) : null;

    private static string? Tail(string? text, int lines) => text is null ? null : string.Join('\n', text.TrimEnd().Split('\n').TakeLast(lines));
}
