using System.Text;
using Foreman.Core;
using Foreman.Recon.Baseline;
using Foreman.Recon.Environment;
using Foreman.Recon.Reports;
using Foreman.Sandbox;

namespace Foreman.Recon.Workspaces;

/// <summary>
/// What recon proved about running this repository's tests: the environment image the baseline saved, the test script,
/// its variables, the per-test baseline and the volume that holds the set-up copy. Every workspace starts from a copy
/// of that volume, so installed packages and build outputs come along without setting anything up again.
/// </summary>
public sealed record TestEnvironment(string Stack, string Image, string TestScript, IReadOnlyDictionary<string, string> Env, IReadOnlyList<TestBaseline> Baseline, string BaselineVolume)
{
    public static TestEnvironment Of(Job job)
    {
        if (!File.Exists(Path.Combine(job.Dir, "baseline", "baseline.json")))
            throw new InvalidOperationException("there is no baseline: implementing needs a verified test command (run recon's environment and baseline stages)");
        var report = job.ReadJson<BaselineReport>("baseline", "baseline.json");
        var stack = report.Stacks.FirstOrDefault(s => s is { Status: StackStatus.Ok, EnvImage: not null, TestCommand: not null, TestsFile: not null })
            ?? throw new InvalidOperationException("no stack has a per-test baseline and an environment image: see baseline/baseline.json");
        var spec = job.ReadJson<EnvironmentReport>("environment", "environment.json").Stacks.FirstOrDefault(s => s.Stack == stack.Stack)?.Spec;
        if (spec is not null && WorkingTree.Problems(string.Join('\n', spec.Setup.Select(s => s.Script)), stack.TestCommand!) is { Count: > 0 } stale)
            throw new InvalidOperationException($"the environment tests a copy made during setup, so no change would show: {stale[0]}. Rerun the environment and baseline stages.");
        var tests = Json.Read<List<TestBaseline>>(File.ReadAllText(Path.Combine(job.Dir, "baseline", stack.TestsFile!))) ?? [];
        return new TestEnvironment(stack.Stack, stack.EnvImage!, stack.TestCommand!, spec?.Env ?? new Dictionary<string, string>(), tests, new DockerSandbox(job.Id).Volume);
    }
}

/// <summary>The outcome of one test run in a workspace.</summary>
public sealed record TestRun(IReadOnlyList<TestOutcome> Outcomes, TestDelta Delta, int ExitCode, bool TimedOut, double Seconds, string LogTail);

/// <summary>
/// A copy of the set-up repository in its own volume with a session container on it: the unprivileged user, no
/// capabilities, no network. Git runs inside the container, never on the host, with hooks off and Foreman's own
/// identity; the diff is taken against a commit recorded when the workspace was made.
/// </summary>
public sealed class Workspace : IAsyncDisposable
{
    public const string Git = "git -c core.hooksPath=/dev/null -c core.quotePath=false -c core.fsmonitor=false -c commit.gpgsign=false -c user.name=Foreman -c user.email=foreman@invalid";

    /// <summary>Caches and build outputs that tests and tools leave behind: never part of a change.</summary>
    public static readonly string[] Junk =
    [
        "**/__pycache__/**", "**/*.pyc", "**/.pytest_cache/**", "**/.mypy_cache/**", "**/.ruff_cache/**", "**/.tox/**", "**/.nox/**",
        "**/.venv/**", "**/*.egg-info/**", "**/node_modules/**", "**/.vitest/**", "**/TestResults/**", "**/bin/Debug/**", "**/bin/Release/**",
        "**/obj/**", "**/.coverage", "**/coverage.xml", "**/.DS_Store",
    ];

    private static readonly string Pathspec = ". " + string.Join(' ', Junk.Select(j => $"':(exclude,glob){j}'"));

    private readonly TestEnvironment _env;
    private int _runs;

    private Workspace(SandboxSession session, TestEnvironment env)
    {
        Session = session;
        _env = env;
    }

    public SandboxSession Session { get; }
    public TestEnvironment Environment => _env;

    /// <summary>Makes <paramref name="target"/>'s volume a fresh copy of the baseline volume.</summary>
    public static async Task CreateAsync(DockerSandbox target, TestEnvironment env)
    {
        if (!await DockerSandbox.VolumeExistsAsync(env.BaselineVolume))
            throw new InvalidOperationException($"the baseline volume {env.BaselineVolume} is gone: rerun the baseline stage");
        await target.CloneFromAsync(env.BaselineVolume, env.Image);
    }

    public static async Task<Workspace> OpenAsync(string volume, TestEnvironment env, SandboxLimits? limits = null) =>
        new(await SandboxSession.StartAsync(volume, env.Image, limits, SessionOptions.Offline(env.Env)), env);

    public Task<ProcResult> BashAsync(string command, TimeSpan timeout, CancellationToken ct = default, string? stdin = null) =>
        Session.ExecAsync(command, timeout, ct, stdin);

    /// <summary>Commits the starting state (setup included) and returns its sha: the diff is taken against it.</summary>
    public async Task<string> MarkStartAsync(CancellationToken ct = default)
    {
        var r = await BashAsync($"set -e; {Git} add -A -- {Pathspec}; {Git} commit -q --no-verify --allow-empty -m 'foreman: start'; {Git} rev-parse HEAD", TimeSpan.FromMinutes(10), ct);
        if (!r.Ok) throw new InvalidOperationException($"recording the start commit failed: {r.Tail(5)}");
        return r.Stdout.Trim().Split('\n')[^1].Trim();
    }

    /// <summary>Everything changed since <paramref name="start"/>, new files included, caches and build outputs not.</summary>
    public async Task<string> DiffAsync(string start, CancellationToken ct = default)
    {
        var r = await BashAsync($"set -e; {Git} add -A -- {Pathspec}; {Git} diff --cached --binary --no-color --no-ext-diff --no-renames {start} -- {Pathspec}",
            TimeSpan.FromMinutes(5), ct);
        if (!r.Ok) throw new InvalidOperationException($"taking the diff failed: {r.Tail(5)}");
        return r.Stdout;
    }

    /// <summary>
    /// Back to <paramref name="start"/>: tracked files reset and files the change added removed (the diff adds them to
    /// the index). Ignored and excluded files, installed packages and build outputs, stay.
    /// </summary>
    public async Task ResetAsync(string start, CancellationToken ct = default)
    {
        var r = await BashAsync($"{Git} add -A -- {Pathspec} && {Git} reset -q --hard {Quote(start)}", TimeSpan.FromMinutes(5), ct);
        if (!r.Ok) throw new InvalidOperationException($"resetting the copy failed: {r.Tail(5)}");
    }

    /// <summary>Applies a patch to the working tree; with <paramref name="only"/>, just those files.</summary>
    public async Task<string?> ApplyAsync(string patch, IReadOnlyList<string>? only = null, CancellationToken ct = default)
    {
        var include = only is null ? "" : string.Concat(only.Select(p => $" --include={Quote(p)}"));
        var r = await BashAsync($"{Git} apply --whitespace=nowarn{include} -", TimeSpan.FromMinutes(5), ct, stdin: patch);
        return r.Ok ? null : r.Tail(10);
    }

    /// <summary>Runs the baseline's test script, network off, and compares the result with the baseline.</summary>
    public async Task<TestRun> RunTestsAsync(string hostDir, CancellationToken ct = default)
    {
        var label = $"tests-{++_runs}-{Guid.NewGuid().ToString("N")[..6]}";
        var outPath = $"{DockerSandbox.OutRoot}/{label}";
        var script = $"export OUT={outPath}\nrm -rf \"$OUT\"; mkdir -p \"$OUT\"\n{_env.TestScript}";
        var r = await BashAsync(script, BaselinePlanner.TestTimeout, ct);
        Dirs.Delete(hostDir);
        await Session.CopyOutAsync(outPath, hostDir);
        await BashAsync($"rm -rf {outPath}", TimeSpan.FromMinutes(1), ct);
        File.WriteAllText(Path.Combine(hostDir, "run.log"), $"exit {r.ExitCode}{(r.TimedOut ? " (timed out)" : "")}, {r.Duration.TotalSeconds:0.0} s\n--- stdout\n{r.Stdout}\n--- stderr\n{r.Stderr}", new UTF8Encoding(false));
        var outcomes = TestReports.ParseFolder(hostDir, DockerSandbox.RepoPath);
        return new TestRun(outcomes, TestDelta.Compare(_env.Baseline, outcomes), r.ExitCode, r.TimedOut, Math.Round(r.Duration.TotalSeconds, 1), r.Tail(30));
    }

    /// <summary>Single-quotes a value for bash.</summary>
    public static string Quote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    public ValueTask DisposeAsync() => Session.DisposeAsync();
}
