using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using Foreman.Core;
using Microsoft.Extensions.Hosting;

namespace Foreman.Server;

public interface IJobRunner
{
    /// <summary>Puts a job in the queue unless it is already queued or running. False if it was.</summary>
    bool Enqueue(string id);

    /// <summary><c>queued</c>, <c>running</c> or null.</summary>
    string? Activity(string id);

    /// <summary>Stops a running job (its current stage fails as cancelled) or drops a queued one.</summary>
    bool Cancel(string id);

    /// <summary>When the last job left the queue (or the runner started); null while a job is queued or running.</summary>
    DateTime? IdleSince { get; }
}

/// <summary>
/// The queue and the workers. A job runs its whole pipeline until it is done or stops for a human; the pipeline skips
/// what is done, so running a job again after an answer, an approval or a restart picks up where it stopped. Each job's
/// log goes to <c>serve.log</c> in its folder. On start, jobs the service owns that were interrupted mid-stage are queued.
/// </summary>
public sealed class JobRunner(string home, Func<Job, ServeMeta, IReadOnlyList<IStage>> pipeline, int workers = 1) : BackgroundService, IJobRunner
{
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>();
    private readonly ConcurrentDictionary<string, string> _activity = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new(StringComparer.Ordinal);
    private DateTime _idleSince = DateTime.UtcNow;

    public DateTime? IdleSince => _activity.IsEmpty ? _idleSince : null;

    public bool Enqueue(string id)
    {
        if (!_activity.TryAdd(id, "queued")) return false;
        _queue.Writer.TryWrite(id);
        return true;
    }

    public string? Activity(string id) => _activity.GetValueOrDefault(id);

    public bool Cancel(string id)
    {
        if (_running.TryGetValue(id, out var cts)) { cts.Cancel(); return true; }
        var dropped = _activity.TryRemove(new KeyValuePair<string, string>(id, "queued"));
        if (dropped) _idleSince = DateTime.UtcNow;
        return dropped;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (var id in Interrupted()) Enqueue(id);
        return Task.WhenAll(Enumerable.Range(0, Math.Max(1, workers)).Select(_ => WorkAsync(stoppingToken)));
    }

    private IEnumerable<string> Interrupted()
    {
        var jobs = Path.Combine(home, "jobs");
        if (!Directory.Exists(jobs)) yield break;
        foreach (var dir in Directory.EnumerateDirectories(jobs))
        {
            if (!File.Exists(Path.Combine(dir, ServeMeta.File))) continue;
            var job = Job.Open(home, Path.GetFileName(dir));
            if (job.ReadState().Values.Any(s => s.Status == StageStatus.Running)) yield return job.Id;
        }
    }

    private async Task WorkAsync(CancellationToken stoppingToken)
    {
        await foreach (var id in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            if (!_activity.TryUpdate(id, "running", "queued")) continue; // cancelled while queued
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            _running[id] = cts;
            try { await RunAsync(id, cts.Token); }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                // A cancelled stage is not interrupted work to resume on restart: it failed, and running the job again retries it.
                var job = Job.Open(home, id);
                foreach (var (name, state) in job.ReadState().Where(s => s.Value.Status == StageStatus.Running))
                    job.SetState(name, state with { Status = StageStatus.Failed, FinishedAt = DateTime.UtcNow, Error = "cancelled by the human" });
                Log(id, "cancelled");
            }
            catch (Exception e) when (e is not OperationCanceledException) { Log(id, $"the service failed to run the job: {e.Message}"); }
            finally
            {
                _running.TryRemove(id, out _);
                _activity.TryRemove(id, out _);
                _idleSince = DateTime.UtcNow;
            }
        }
    }

    private async Task RunAsync(string id, CancellationToken ct)
    {
        var job = Job.Open(home, id);
        var meta = ServeMeta.Read(job) ?? new ServeMeta(job.Manifest.CreatedAt);
        Log(id, "run started");
        var ok = await new Pipeline(pipeline(job, meta)).RunAsync(job, null, line => Log(id, line), ct);
        Log(id, ok ? "run finished" : $"run stopped: {JobViews.Of(job).Phase}");
    }

    private readonly Lock _log = new();

    private void Log(string id, string line)
    {
        lock (_log)
            File.AppendAllText(Path.Combine(home, "jobs", id, "serve.log"), $"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ} {line.TrimEnd()}\n", new UTF8Encoding(false));
    }
}
