using Foreman.Core;
using Microsoft.Extensions.Hosting;

namespace Foreman.Server;

/// <summary>What a cleanup removed (or would remove, on a dry run).</summary>
public sealed record GcReport(IReadOnlyList<string> Jobs, IReadOnlyList<string> Volumes, IReadOnlyList<string> Images, IReadOnlyList<string> Containers, IReadOnlyList<string> Orphans);

/// <summary>
/// Old jobs' Docker leftovers. Every job keeps a volume with its set-up repository and an image of its environment
/// (gigabytes for a .NET or Node project) so a later stage can start without setting up again. On a server that fills
/// the disk. The janitor removes them for jobs idle longer than a cutoff, and anything named after a job that no longer
/// exists. The job's folder stays (the record, the PRP, the patch, the report: small); its environment and baseline
/// stages are reset, so running the job again sets them up again instead of failing on a missing volume.
/// Names are matched to the longest job id they start with, so job <c>x-2</c>'s volume is never taken for job <c>x</c>'s.
/// </summary>
public static partial class Janitor
{
    public static async Task<GcReport> RunAsync(string home, TimeSpan olderThan, bool dryRun, Func<string, bool>? busy = null, CancellationToken ct = default)
    {
        var jobsDir = Path.Combine(home, "jobs");
        var jobs = Directory.Exists(jobsDir)
            ? Directory.EnumerateDirectories(jobsDir).Where(d => File.Exists(Path.Combine(d, "job.json"))).Select(d => Job.Open(home, Path.GetFileName(d))).ToList()
            : [];
        var ids = jobs.ToDictionary(j => j.Id.ToLowerInvariant(), j => j, StringComparer.Ordinal);
        var cutoff = DateTime.UtcNow - olderThan;
        var stale = jobs.Where(j => !(busy?.Invoke(j.Id) ?? false) && LastActivity(j) < cutoff).Select(j => j.Id.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);

        var volumes = await ListAsync(["volume", "ls", "-q", "--filter", "name=foreman-"], ct);
        var images = await ListAsync(["images", "--format", "{{.Repository}}:{{.Tag}}", "--filter", "reference=foreman-env/*"], ct);
        var containers = await ListAsync(["ps", "-a", "--format", "{{.Names}}", "--filter", "name=foreman-"], ct);

        List<string> removeVolumes = [], removeImages = [], removeContainers = [], orphans = [];
        var touched = new HashSet<string>(StringComparer.Ordinal);
        void Sort(string name, string owned, List<string> into)
        {
            var owner = Owner(owned, ids.Keys);
            // Shared things (the OSV database volume, the tool images) aren't any job's: only a name shaped like a job
            // id with no job left is a leftover.
            if (owner is null) { if (LooksLikeJob(owned)) { into.Add(name); orphans.Add(name); } }
            else if (stale.Contains(owner)) { into.Add(name); touched.Add(owner); }
        }
        foreach (var c in containers) Sort(c, c, removeContainers);
        foreach (var v in volumes) Sort(v, v, removeVolumes);
        foreach (var i in images) Sort(i, i["foreman-env/".Length..].Split(':')[0], removeImages);

        if (!dryRun)
        {
            foreach (var c in removeContainers) await DockerAsync(["rm", "-f", c], ct);
            foreach (var v in removeVolumes) await DockerAsync(["volume", "rm", "-f", v], ct);
            foreach (var i in removeImages) await DockerAsync(["rmi", "-f", i], ct);
            foreach (var id in touched)
            {
                var job = ids[id];
                foreach (var stage in new[] { "environment", "baseline" }) job.ClearState(stage);
                job.Event("gc", "removed", new { reason = $"idle since {LastActivity(job):yyyy-MM-dd}", stages = new[] { "environment", "baseline" } });
            }
        }
        return new GcReport(touched.Select(t => ids[t].Id).Order().ToList(), removeVolumes, removeImages, removeContainers, orphans);
    }

    /// <summary>The job whose Docker names this is: the longest job id it equals or starts with followed by "-".</summary>
    internal static string? Owner(string name, IEnumerable<string> jobIds)
    {
        const string prefix = "foreman-";
        if (!name.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var rest = name[prefix.Length..];
        return jobIds.Where(id => rest == id || rest.StartsWith(id + "-", StringComparison.Ordinal)).OrderByDescending(id => id.Length).FirstOrDefault();
    }

    /// <summary>A Docker name made from a job id: a timestamped job (<c>20261116-160558-…</c>), an eval job or a red-team job.</summary>
    internal static bool LooksLikeJob(string name) => JobName().IsMatch(name);

    [System.Text.RegularExpressions.GeneratedRegex(@"^foreman-(\d{8}-\d{6}-|eval-|redteam-)")]
    private static partial System.Text.RegularExpressions.Regex JobName();

    /// <summary>The last time anything happened to the job: a stage started or finished, or an event.</summary>
    internal static DateTime LastActivity(Job job)
    {
        var last = job.Manifest.CreatedAt;
        foreach (var s in job.ReadState().Values)
            foreach (var t in new[] { s.StartedAt, s.FinishedAt })
                if (t is { } v && v > last) last = v;
        var events = Path.Combine(job.Dir, "events.jsonl");
        if (File.Exists(events) && File.GetLastWriteTimeUtc(events) > last) last = File.GetLastWriteTimeUtc(events);
        return last;
    }

    private static async Task<List<string>> ListAsync(string[] args, CancellationToken ct)
    {
        var r = await Proc.RunAsync("docker", args, timeout: TimeSpan.FromMinutes(1), ct: ct);
        if (!r.Ok) throw new InvalidOperationException($"docker {string.Join(' ', args.Take(2))} failed: {r.Tail(3)}");
        return r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(n => n != "<none>:<none>").ToList();
    }

    private static Task DockerAsync(string[] args, CancellationToken ct) => Proc.RunAsync("docker", args, timeout: TimeSpan.FromMinutes(5), ct: ct);
}

/// <summary>
/// The janitor once a day inside <c>foreman serve</c>, for jobs idle longer than <c>FOREMAN_GC_DAYS</c> (14 by
/// default; 0 turns it off). Jobs that are queued or running are never touched.
/// </summary>
public sealed class JanitorService(string home, IJobRunner runner, int days, Action<string>? log = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (days <= 0) return;
        using var timer = new PeriodicTimer(TimeSpan.FromHours(24));
        do
        {
            try
            {
                var r = await Janitor.RunAsync(home, TimeSpan.FromDays(days), dryRun: false, id => runner.Activity(id) is not null, stoppingToken);
                if (r.Volumes.Count + r.Images.Count + r.Containers.Count > 0)
                    log?.Invoke($"gc: {r.Jobs.Count} idle job(s), {r.Volumes.Count} volume(s), {r.Images.Count} image(s), {r.Containers.Count} container(s), {r.Orphans.Count} orphan(s)");
            }
            catch (InvalidOperationException e) { log?.Invoke($"gc failed: {e.Message}"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

