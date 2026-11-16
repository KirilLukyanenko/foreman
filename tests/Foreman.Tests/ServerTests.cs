using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Foreman.Core;
using Foreman.Planning.Clarify;
using Foreman.Planning.Intake;
using Foreman.Planning.Spec;
using Foreman.Server;
using Microsoft.AspNetCore.Builder;

namespace Foreman.Tests;

public sealed class ServerTests : IAsyncLifetime
{
    private const string Token = "test-token-0123456789abcdef";
    private readonly string _home = Path.Combine(Path.GetTempPath(), "foreman-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeRunner _runner = new();
    private WebApplication _app = null!;
    private HttpClient _http = null!;

    private sealed class FakeRunner : IJobRunner
    {
        public List<string> Queued { get; } = [];
        public bool Enqueue(string id) { if (Queued.Contains(id)) return false; Queued.Add(id); return true; }
        public string? Activity(string id) => Queued.Contains(id) ? "queued" : null;
        public bool Cancel(string id) => Queued.Remove(id);
    }

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_home);
        _app = ForemanServer.Build(new ServerOptions(_home, Token, "http://127.0.0.1:0"), _runner);
        await _app.StartAsync(TestContext.Current.CancellationToken);
        _http = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
        _http.DefaultRequestHeaders.Authorization = new("Bearer", Token);
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _app.DisposeAsync();
        try { Directory.Delete(_home, recursive: true); } catch (IOException) { }
    }

    private async Task<string> CreateAsync()
    {
        var r = await _http.PostAsJsonAsync("/api/jobs", new { repo = "https://github.com/example/shop.git", task = "The discount starts one item too late." }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Every_api_call_needs_the_token_and_health_does_not()
    {
        var ct = TestContext.Current.CancellationToken;
        using var anonymous = new HttpClient { BaseAddress = _http.BaseAddress };
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/health", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/jobs", ct)).StatusCode);
        anonymous.DefaultRequestHeaders.Authorization = new("Bearer", Token + "x");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/jobs", ct)).StatusCode);
        Assert.Throws<InvalidOperationException>(() => ForemanServer.Build(new ServerOptions(_home, "short"), _runner));
    }

    [Theory]
    [InlineData("http://github.com/example/shop.git")]
    [InlineData("https://user:secret@github.com/example/shop.git")]
    [InlineData("C:\\repos\\shop")]
    [InlineData("file:///etc")]
    public async Task Only_https_repositories_without_credentials_are_accepted(string repo)
    {
        var r = await _http.PostAsJsonAsync("/api/jobs", new { repo, task = "x" }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task A_new_job_is_stored_queued_and_listed()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = await CreateAsync();
        Assert.Equal([id], _runner.Queued);
        var job = Job.Open(_home, id);
        Assert.Equal("The discount starts one item too late.", File.ReadAllText(Path.Combine(job.Dir, "task.md")));
        Assert.True(ServeMeta.Read(job)!.Acceptance);
        var list = await _http.GetFromJsonAsync<JsonElement>("/api/jobs", ct);
        Assert.Equal("queued", list[0].GetProperty("phase").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await _http.PostAsync($"/api/jobs/{id}/run", null, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await _http.PostAsync($"/api/jobs/{id}/cancel", null, ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await _http.PostAsync($"/api/jobs/{id}/run", null, ct)).StatusCode);
    }

    [Fact]
    public async Task Waiting_for_answers_shows_the_questions_and_answers_resume_the_job()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = await CreateAsync();
        _runner.Queued.Clear();
        var job = Job.Open(_home, id);
        job.WriteJson("clarify", "questions.json", new List<Gap> { new("scope", "is 10 included", "ask", "yes", "high", "Does an order of exactly 10 get the discount?", "Yes", ["Yes", "No"], null, "Q1") });
        job.SetState("spec", new StageState(StageStatus.Waiting, DateTime.UtcNow, DateTime.UtcNow, "waiting for the client"));

        var detail = await _http.GetFromJsonAsync<JsonElement>($"/api/jobs/{id}", ct);
        Assert.Equal("waiting_answers", detail.GetProperty("job").GetProperty("phase").GetString());
        Assert.Equal("Q1", detail.GetProperty("questions")[0].GetProperty("id").GetString());

        var r = await _http.PostAsJsonAsync($"/api/jobs/{id}/answers", new { answers = new Dictionary<string, string> { ["Q1"] = "Yes, 10 or more" } }, ct);
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        Assert.Equal("Yes, 10 or more", job.ReadJson<List<Answer>>("clarify", "answers.json").Single().Value);
        Assert.Equal([id], _runner.Queued);
    }

    [Fact]
    public async Task Ready_approves_the_prp_and_files_are_served_from_an_allowlist()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = await CreateAsync();
        _runner.Queued.Clear();
        var job = Job.Open(_home, id);
        Assert.Equal(HttpStatusCode.Conflict, (await _http.PostAsync($"/api/jobs/{id}/ready", null, ct)).StatusCode);

        job.WriteJson("intake", "brief.json", new Brief("en", "issue", "Discount from 10", "10 or more get 10% off", "bug", [], [], [], null, null, [], [], [], new FitEstimate("go", "low", [])));
        var prp = new Prp(id, "abc", "Draft", DateTime.UtcNow, new SpecDraft("Discount from 10", "g", ["w"], [], [], [], ["o"], [], [], [], [], ["a"], []), [], [], [], []);
        job.WriteJson("spec", "prp.json", prp);
        job.WriteText("spec", "prp.md", prp.Markdown(job.Manifest.Source, "bug"));
        job.SetState("spec", new StageState(StageStatus.Done, DateTime.UtcNow, DateTime.UtcNow));
        job.SetState("implement", new StageState(StageStatus.Waiting, DateTime.UtcNow, DateTime.UtcNow, "the PRP is not Ready"));
        var detail = await _http.GetFromJsonAsync<JsonElement>($"/api/jobs/{id}", ct);
        Assert.Equal("waiting_ready", detail.GetProperty("job").GetProperty("phase").GetString());
        Assert.Contains("# PRP: Discount from 10", detail.GetProperty("prp").GetString());

        Assert.Equal(HttpStatusCode.Accepted, (await _http.PostAsync($"/api/jobs/{id}/ready", null, ct)).StatusCode);
        Assert.Null(Ready.Check(job));
        Assert.Contains("| Status | Ready |", await _http.GetStringAsync($"/api/jobs/{id}/files/prp.md", ct));
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync($"/api/jobs/{id}/files/job.json", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync($"/api/jobs/{id}/files/..%2Fjob.json", ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.GetAsync("/api/jobs/..%2F..%2Fx", ct)).StatusCode);
    }

    [Fact]
    public void Jobs_made_outside_the_service_are_not_listed_or_reachable()
    {
        var task = Path.Combine(_home, "t.md");
        File.WriteAllText(task, "x");
        Job.Create(_home, "https://example.invalid/cli.git", task);
        Assert.Empty(ForemanServer.ServeJobs(_home));
    }
}

public sealed class JobViewTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "foreman-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch (IOException) { }
    }

    private Job NewJob() => Job.Create(_home, "https://example.invalid/r.git", null);

    private static void Set(Job job, string stage, StageStatus status, string? error = null) =>
        job.SetState(stage, new StageState(status, DateTime.UtcNow, DateTime.UtcNow, error));

    [Fact]
    public void The_phase_follows_the_stages()
    {
        var job = NewJob();
        Assert.Equal("new", JobViews.Of(job).Phase);
        Assert.Equal("queued", JobViews.Of(job, "queued").Phase);

        Set(job, "intake", StageStatus.Done);
        Set(job, "go", StageStatus.Waiting, "no-go");
        Assert.Equal("waiting_go", JobViews.Of(job).Phase);
        Set(job, "go", StageStatus.Done);
        Set(job, "fetch", StageStatus.Running);
        Assert.Equal("interrupted", JobViews.Of(job).Phase);
        Assert.Equal(("running", "fetch"), (JobViews.Of(job, "running").Phase, JobViews.Of(job, "running").Stage));
        Set(job, "fetch", StageStatus.Failed, "clone failed");
        Assert.Equal(("failed", "clone failed"), (JobViews.Of(job).Phase, JobViews.Of(job).Detail));
        Set(job, "fetch", StageStatus.Done);
        Set(job, "spec", StageStatus.Waiting, "waiting for the client");
        Assert.Equal("waiting_answers", JobViews.Of(job).Phase);
        Set(job, "spec", StageStatus.Done);
        Set(job, "accept", StageStatus.Waiting, "not Ready");
        Assert.Equal("waiting_ready", JobViews.Of(job).Phase);
        Set(job, "accept", StageStatus.Done);
        Set(job, "implement", StageStatus.Done);
        Assert.Equal("verified", JobViews.Of(job).Phase);
    }
}

public sealed class JobRunnerTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "foreman-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch (IOException) { }
    }

    private sealed class Step(string name, Func<Job, CancellationToken, Task> run) : IStage
    {
        public string Name => name;
        public Task RunAsync(Job job, CancellationToken ct) => run(job, ct);
    }

    private Job NewServeJob()
    {
        var job = Job.Create(_home, "https://example.invalid/r.git", null);
        new ServeMeta(DateTime.UtcNow).Write(job);
        return job;
    }

    private static async Task UntilIdle(JobRunner runner, string id)
    {
        for (var i = 0; i < 200 && runner.Activity(id) is not null; i++) await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Null(runner.Activity(id));
    }

    [Fact]
    public async Task A_job_runs_until_it_waits_and_resumes_after_the_human_acts()
    {
        var ct = TestContext.Current.CancellationToken;
        var answered = false;
        var runs = 0;
        IReadOnlyList<IStage> Pipeline(Job job, ServeMeta meta) =>
        [
            new Step("intake", (_, _) => { runs++; return Task.CompletedTask; }),
            new Step("spec", (_, _) => answered ? Task.CompletedTask : throw new JobWaitingException("answers", "waiting for the client")),
            new Step("implement", (_, _) => Task.CompletedTask),
        ];
        var runner = new JobRunner(_home, Pipeline);
        await runner.StartAsync(ct);
        var job = NewServeJob();

        Assert.True(runner.Enqueue(job.Id));
        await UntilIdle(runner, job.Id);
        Assert.Equal("waiting_answers", JobViews.Of(job).Phase);

        answered = true;
        Assert.True(runner.Enqueue(job.Id));
        await UntilIdle(runner, job.Id);
        Assert.Equal("verified", JobViews.Of(job).Phase);
        Assert.Equal(1, runs); // intake was done and skipped on the second run
        Assert.Contains("run stopped: waiting_answers", File.ReadAllText(Path.Combine(job.Dir, "serve.log")));
        await runner.StopAsync(ct);
    }

    [Fact]
    public async Task Cancel_stops_the_running_stage_and_a_restart_resumes_interrupted_jobs()
    {
        var ct = TestContext.Current.CancellationToken;
        var started = new TaskCompletionSource();
        IReadOnlyList<IStage> Pipeline(Job job, ServeMeta meta) =>
            [new Step("fetch", async (_, c) => { started.TrySetResult(); await Task.Delay(Timeout.Infinite, c); })];
        var runner = new JobRunner(_home, Pipeline);
        await runner.StartAsync(ct);
        var job = NewServeJob();
        runner.Enqueue(job.Id);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.Equal("running", runner.Activity(job.Id));
        Assert.True(runner.Cancel(job.Id));
        await UntilIdle(runner, job.Id);
        Assert.Equal(("failed", "cancelled by the human"), (JobViews.Of(job).Phase, JobViews.Of(job).Detail));
        await runner.StopAsync(ct);

        // A stage left running by a crash is picked up when the service starts again.
        var crashed = NewServeJob();
        crashed.SetState("fetch", new StageState(StageStatus.Running, DateTime.UtcNow));
        var resumed = new TaskCompletionSource<string>();
        var again = new JobRunner(_home, (j, _) => [new Step("fetch", (x, _) => { resumed.TrySetResult(x.Id); return Task.CompletedTask; })]);
        await again.StartAsync(ct);
        Assert.Equal(crashed.Id, await resumed.Task.WaitAsync(TimeSpan.FromSeconds(10), ct));
        await again.StopAsync(ct);
    }
}

public class JanitorTests
{
    [Theory]
    [InlineData("foreman-20261116-160558-shop", "20261116-160558-shop")]
    [InlineData("foreman-20261116-160558-shop-impl", "20261116-160558-shop")]
    [InlineData("foreman-20261116-160558-shop-2", "20261116-160558-shop-2")]
    [InlineData("foreman-20261116-160558-shop-2-verify-c1-1", "20261116-160558-shop-2")]
    [InlineData("foreman-20261116-160558-other", null)]
    [InlineData("postgres-data", null)]
    public void A_name_belongs_to_the_longest_job_id_it_starts_with(string name, string? owner) =>
        Assert.Equal(owner, Janitor.Owner(name, ["20261116-160558-shop", "20261116-160558-shop-2"]));
}

public class JanitorSharedTests
{
    [Theory]
    [InlineData("foreman-osv-db", false)]
    [InlineData("foreman-tools", false)]
    [InlineData("foreman-20261116-160558-shop-impl", true)]
    [InlineData("foreman-eval-unjs-defu-119", true)]
    [InlineData("foreman-redteam-rules-curl-spec-lab", true)]
    public void Only_names_shaped_like_job_ids_can_be_leftovers(string name, bool job) => Assert.Equal(job, Janitor.LooksLikeJob(name));
}
