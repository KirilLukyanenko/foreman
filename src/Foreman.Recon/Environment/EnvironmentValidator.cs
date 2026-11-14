using Foreman.Core;
using Foreman.Recon.Reports;
using Foreman.Sandbox;

namespace Foreman.Recon.Environment;

/// <summary>
/// Runs a spec from scratch: a fresh volume with a clean copy of the repository, the setup steps, then the test
/// script once with the network off. A spec that only worked because of something done by hand in an agent's
/// session fails here. It is the check RepoLaunch calls "verify", done by code rather than by a model.
/// </summary>
public sealed class EnvironmentValidator(Job job, IReadOnlyList<string> excludedPaths, SandboxLimits? limits = null)
{
    public async Task<Validation> ValidateAsync(EnvironmentSpec spec, string logDir, CancellationToken ct = default)
    {
        var started = DateTime.UtcNow;
        Directory.CreateDirectory(logDir);
        var sandbox = new DockerSandbox(job.Id, limits, "check");
        var snapshots = new List<string>();
        try
        {
            await DockerSandbox.EnsureImageAsync(spec.Image);
            await sandbox.PopulateAsync(job.SourceDir, spec.Image, excludedPaths);

            // Each setup step starts from the previous step's snapshot; the tests run on the last one.
            var image = spec.Image;
            foreach (var step in spec.Setup)
            {
                var snapshot = sandbox.SnapshotTag(step.Name);
                snapshots.Add(snapshot);
                var r = await sandbox.RunAsync(
                    new SandboxRun(image, step.Script, step.Network ? Network.Open : Network.None, TimeSpan.FromMinutes(step.TimeoutMinutes), spec.Env, step.AsRoot),
                    Path.Combine(logDir, step.Name), ct, commitAs: snapshot);
                await WriteLogAsync(logDir, step.Name, r);
                if (!r.Ok)
                    return Fail(started, r.TimedOut ? $"setup step '{step.Name}' timed out" : $"setup step '{step.Name}' exited with {r.ExitCode}", r);
                image = snapshot;
            }
            if (spec.Setup.Any(s => s.AsRoot)) await sandbox.ResetOwnershipAsync(spec.Image);

            var outDir = Path.Combine(logDir, "test");
            Dirs.Delete(outDir);
            var test = await sandbox.RunAsync(new SandboxRun(image, spec.TestScript, Network.None, TimeSpan.FromMinutes(20), spec.Env), outDir, ct);
            await WriteLogAsync(logDir, "test", test);
            if (test.TimedOut) return Fail(started, "the test script timed out after 20 minutes", test);

            var outcomes = TestReports.ParseFolder(outDir, DockerSandbox.RepoPath);
            if (outcomes.Count == 0)
                return Fail(started, $"the test script (exit {test.ExitCode}) left no test report in $OUT that Foreman can read (JUnit XML, TRX or Jest JSON)", test);

            return new Validation(true, null, outcomes.Count,
                outcomes.Count(o => o.Status == TestStatus.Passed),
                outcomes.Count(o => o.Status == TestStatus.Failed),
                outcomes.Count(o => o.Status == TestStatus.Skipped),
                Seconds(started), test.Tail(15));
        }
        finally
        {
            await sandbox.RemoveAsync();
            foreach (var s in snapshots) await DockerSandbox.RemoveImageAsync(s);
        }
    }

    private static Validation Fail(DateTime started, string reason, ProcResult r) =>
        new(false, reason, 0, 0, 0, 0, Seconds(started), r.Tail(40));

    private static double Seconds(DateTime started) => Math.Round((DateTime.UtcNow - started).TotalSeconds, 1);

    private static Task WriteLogAsync(string dir, string name, ProcResult r) =>
        File.WriteAllTextAsync(Path.Combine(dir, $"{name}.log"),
            $"exit {r.ExitCode}{(r.TimedOut ? " (timed out)" : "")}, {r.Duration.TotalSeconds:0.0} s\n--- stdout\n{r.Stdout}\n--- stderr\n{r.Stderr}");
}
