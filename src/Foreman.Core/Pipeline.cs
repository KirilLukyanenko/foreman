using System.Diagnostics;

namespace Foreman.Core;

public interface IStage
{
    string Name { get; }

    Task RunAsync(Job job, CancellationToken ct);
}

/// <summary>
/// Runs stages in order. A stage already marked done is skipped unless forced, so a failed job resumes from the
/// stage that failed. A failure stops the pipeline: later stages depend on earlier outputs.
/// </summary>
public sealed class Pipeline(IReadOnlyList<IStage> stages)
{
    public async Task<bool> RunAsync(Job job, ISet<string>? force = null, Action<string>? log = null, CancellationToken ct = default)
    {
        using var jobSpan = Telemetry.StartJob(job);
        foreach (var stage in stages)
        {
            var state = job.ReadState().GetValueOrDefault(stage.Name);
            if (state?.Status == StageStatus.Done && force?.Contains(stage.Name) != true)
            {
                log?.Invoke($"{stage.Name}: done earlier, skipped");
                continue;
            }

            log?.Invoke($"{stage.Name}: running");
            var started = DateTime.UtcNow;
            job.SetState(stage.Name, new StageState(StageStatus.Running, started));
            job.Event(stage.Name, "started");
            using var span = Telemetry.StartStage(job, stage.Name);
            var clock = Stopwatch.StartNew();
            try
            {
                await stage.RunAsync(job, ct);
                job.SetState(stage.Name, new StageState(StageStatus.Done, started, DateTime.UtcNow));
                job.Event(stage.Name, "finished", new { seconds = Math.Round(clock.Elapsed.TotalSeconds, 1) });
                log?.Invoke($"{stage.Name}: done in {clock.Elapsed.TotalSeconds:0.0} s");
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                job.SetState(stage.Name, new StageState(StageStatus.Failed, started, DateTime.UtcNow, e.Message));
                job.Event(stage.Name, "failed", new { error = e.Message, seconds = Math.Round(clock.Elapsed.TotalSeconds, 1) });
                span?.SetStatus(ActivityStatusCode.Error, e.Message);
                log?.Invoke($"{stage.Name}: FAILED: {e.Message}");
                return false;
            }
        }
        return true;
    }
}
