using Foreman.Core;
using Foreman.Recon.Baseline;
using Foreman.Recon.Environment;
using Foreman.Recon.Reports;
using Foreman.Recon.Triage;
using Foreman.Sandbox;

namespace Foreman.Recon;

public sealed record ReconOptions(int BaselineRuns = 3, string? Ref = null, SandboxLimits? Limits = null);

public sealed record FetchResult(string Source, string Commit, string? Ref, int Depth);

/// <summary>
/// Clones the repository into the job folder. Shallow, so the agent has the recent history for style but nothing
/// after the base commit; no submodules; hooks pointed at a folder that doesn't exist, so nothing runs on clone.
/// </summary>
public sealed class FetchStage(ReconOptions options) : IStage
{
    public const int Depth = 200;

    public string Name => "fetch";

    public async Task RunAsync(Job job, CancellationToken ct)
    {
        Dirs.Delete(job.SourceDir);

        var source = job.Manifest.Source;
        // A local path is cloned as a URL so that --depth applies and only committed files come along.
        var url = Directory.Exists(source) ? new Uri(Path.GetFullPath(source)).AbsoluteUri : source;
        List<string> args =
        [
            "-c", "core.hooksPath=.foreman-no-hooks", "-c", "core.longpaths=true",
            "clone", "--depth", Depth.ToString(), "--no-recurse-submodules", "--config", "core.hooksPath=.foreman-no-hooks",
        ];
        if (options.Ref is not null) args.AddRange(["--branch", options.Ref]);
        args.AddRange([url, job.SourceDir]);

        var r = await Proc.RunAsync("git", args, job.Dir, TimeSpan.FromMinutes(10), ct: ct);
        if (!r.Ok) throw new InvalidOperationException($"git clone failed: {r.Tail(5)}");

        var commit = (await Proc.GitAsync(job.SourceDir, "rev-parse", "HEAD")).Trim();
        job.WriteJson(Name, "fetch.json", new FetchResult(source, commit, options.Ref, Depth));
        job.Event(Name, "cloned", new { commit });
    }
}

/// <summary>Reads the repository without running anything in it: stacks, CI, instruction files, install-time code.</summary>
public sealed class TriageStage : IStage
{
    public string Name => "triage";

    public async Task RunAsync(Job job, CancellationToken ct)
    {
        var files = RepoFiles.Scan(job.SourceDir);
        var bytes = files.All.Sum(f => new FileInfo(files.FullPath(f)).Length);

        var report = new TriageReport(
            Commit: job.ReadJson<FetchResult>("fetch", "fetch.json").Commit,
            Size: new RepoSize(files.All.Count, bytes, files.Truncated),
            Languages: StackDetector.Languages(files),
            Dotnet: StackDetector.Dotnet(files),
            Node: StackDetector.Node(files),
            Python: StackDetector.Python(files),
            UnsupportedStacks: StackDetector.UnsupportedStacks(files),
            CiCommands: CiReader.Read(files),
            EnvironmentFiles: RepoSignals.EnvironmentFiles(files),
            InstructionFiles: RepoSignals.InstructionFiles(files),
            InstallScripts: RepoSignals.InstallScripts(files),
            RemovedAgentConfig: RepoSignals.AgentConfigPaths.Where(p => Directory.Exists(files.FullPath(p)) || File.Exists(files.FullPath(p))).ToList(),
            Git: await GitReader.ReadAsync(job.SourceDir));

        job.WriteJson(Name, "triage.json", report);
        job.Event(Name, "detected", new
        {
            dotnet = report.Dotnet is not null,
            node = report.Node is not null,
            python = report.Python is not null,
            unsupported = report.UnsupportedStacks,
            hiddenCharacters = report.InstructionFiles.Sum(f => f.HiddenCharacters),
            installScripts = report.InstallScripts.Count,
        });
    }
}

/// <summary>
/// Runs what the environment stage proved, in a fresh volume: setup, then the tests N times with the network off,
/// so we know which tests pass before anything changes, which fail already, and which can't be trusted.
/// Running setup again from scratch is deliberate: it shows the environment is reproducible, not a one-off.
/// </summary>
public sealed class BaselineStage(ReconOptions options, Action<string>? log = null) : IStage
{
    public string Name => "baseline";

    public async Task RunAsync(Job job, CancellationToken ct)
    {
        var triage = job.ReadJson<TriageReport>("triage", "triage.json");
        var environment = job.ReadJson<EnvironmentReport>("environment", "environment.json");
        var ready = environment.Stacks.Where(s => s is { Ready: true, Spec: not null }).Select(s => s.Spec!).ToList();
        if (ready.Count == 0) throw new InvalidOperationException("no stack has a working environment: see environment/environment.json");

        await DockerSandbox.EnsureDockerAsync();
        var sandbox = new DockerSandbox(job.Id, options.Limits);
        var digests = new Dictionary<string, string>();
        foreach (var image in ready.Select(s => s.Image).Distinct())
        {
            log?.Invoke($"  image {image}");
            digests[image] = await DockerSandbox.EnsureImageAsync(image);
        }

        log?.Invoke($"  copying the repository into volume {sandbox.Volume}");
        await sandbox.PopulateAsync(job.SourceDir, ready[0].Image, triage.RemovedAgentConfig);
        job.Event(Name, "sandbox-ready", new { volume = sandbox.Volume, removed = triage.RemovedAgentConfig });

        var stacks = new List<StackBaseline>();
        foreach (var spec in ready)
            stacks.Add(await RunStackAsync(job, sandbox, spec, digests.GetValueOrDefault(spec.Image), ct));

        job.WriteJson(Name, "baseline.json", new BaselineReport(options.BaselineRuns, stacks));
        if (stacks.All(s => s.Status != StackStatus.Ok))
            throw new InvalidOperationException("no stack produced a per-test baseline: " +
                string.Join("; ", stacks.Select(s => $"{s.Stack}: {s.Status}{(s.Reason is null ? "" : $" ({s.Reason})")}")));
    }

    private async Task<StackBaseline> RunStackAsync(Job job, DockerSandbox sandbox, EnvironmentSpec spec, string? digest, CancellationToken ct)
    {
        var dir = job.StageDir(Path.Combine(Name, spec.Stack));
        var steps = new List<StepResult>();
        // Setup steps chain through snapshots; the last one is the job's environment image, kept for later stages.
        var image = spec.Image;
        string? previous = null;
        foreach (var step in spec.Setup)
        {
            var network = step.Network ? Network.Open : Network.None;
            var snapshot = sandbox.SnapshotTag($"{spec.Stack}-{step.Name}");
            log?.Invoke($"  {spec.Stack}: {step.Name} (network {(step.Network ? "open" : "none")}{(step.AsRoot ? ", root" : "")})");
            var r = await sandbox.RunAsync(new SandboxRun(image, step.Script, network, TimeSpan.FromMinutes(step.TimeoutMinutes), spec.Env, step.AsRoot), Path.Combine(dir, step.Name), ct, commitAs: snapshot);
            steps.Add(Record(job, spec.Stack, step.Name, network, r, dir));
            if (!r.Ok)
            {
                var reason = r.TimedOut ? $"{step.Name} timed out after {step.TimeoutMinutes} min" : $"{step.Name} exited with {r.ExitCode}";
                return new StackBaseline(spec.Stack, spec.Image, digest, StackStatus.SetupFailed, reason, spec.Notes, steps, spec.TestScript, [], null, null);
            }
            if (previous is not null) await DockerSandbox.RemoveImageAsync(previous);
            previous = image = snapshot;
        }
        if (spec.Setup.Any(s => s.AsRoot)) await sandbox.ResetOwnershipAsync(spec.Image);

        var runs = new List<IReadOnlyList<TestOutcome>>();
        var testRuns = new List<StepResult>();
        for (var i = 1; i <= options.BaselineRuns; i++)
        {
            log?.Invoke($"  {spec.Stack}: test run {i}/{options.BaselineRuns} (network none)");
            var outDir = Path.Combine(dir, $"run{i}");
            Dirs.Delete(outDir);
            var r = await sandbox.RunAsync(new SandboxRun(image, spec.TestScript, Network.None, BaselinePlanner.TestTimeout, spec.Env), outDir, ct);
            testRuns.Add(Record(job, spec.Stack, $"run{i}", Network.None, r, dir));
            runs.Add(TestReports.ParseFolder(outDir, DockerSandbox.RepoPath));
        }

        if (runs.All(r => r.Count == 0))
            return new StackBaseline(spec.Stack, spec.Image, digest, StackStatus.NoPerTestResults,
                "the test command produced no report we can read", spec.Notes, steps, spec.TestScript, testRuns, null, null, image);

        var tests = BaselineMath.Combine(runs);
        var testsFile = Path.Combine(spec.Stack, "tests.json");
        job.WriteJson(Name, testsFile, tests);
        var counts = BaselineMath.Count(tests);
        job.Event(Name, "baseline", new { stack = spec.Stack, counts });
        return new StackBaseline(spec.Stack, spec.Image, digest, StackStatus.Ok, null, spec.Notes, steps, spec.TestScript, testRuns, counts, testsFile.Replace('\\', '/'), image);
    }

    private StepResult Record(Job job, string stack, string step, Network network, ProcResult r, string dir)
    {
        var log = Path.Combine(dir, $"{step}.log");
        File.WriteAllText(log, $"exit {r.ExitCode}{(r.TimedOut ? " (timed out)" : "")}, {r.Duration.TotalSeconds:0.0} s\n--- stdout\n{r.Stdout}\n--- stderr\n{r.Stderr}");
        job.Event(Name, "step", new { stack, step, network = network.ToString().ToLowerInvariant(), exit = r.ExitCode, seconds = Math.Round(r.Duration.TotalSeconds, 1), timedOut = r.TimedOut });
        return new StepResult(step, network.ToString().ToLowerInvariant(), r.ExitCode, Math.Round(r.Duration.TotalSeconds, 1), r.TimedOut,
            Path.GetRelativePath(job.Dir, log).Replace('\\', '/'));
    }
}
