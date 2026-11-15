using System.Text.Json;
using Foreman.Core;
using Foreman.Recon.Dossier;
using Foreman.Sandbox;

namespace Foreman.Recon.Workspaces;

/// <summary>
/// A writer's lab: a throwaway copy of the set-up repository (the baseline's volume) with the network off, as the
/// unprivileged user. The spec writer checks the PRP's claims about the code by running them and sketches the change
/// against the whole suite; the acceptance test writer writes its tests next to a reference fix and has them tested
/// on the code before the change, on the reference and on broken versions of it (<see cref="RunStateAsync"/>).
/// Nothing in a lab reaches the implementer: each stage removes its lab when its writer stops.
/// </summary>
public sealed class Lab(Job job, string dir, string suffix) : IAsyncDisposable
{
    public const string SpecSuffix = "spec-lab";
    public const string AcceptanceSuffix = "accept-lab";
    public const int MaxCommandSeconds = 600;
    private const int MaxOutputChars = 10_000;

    private TestEnvironment? _env;
    private string? _unavailable;
    private bool _checked;
    private Workspace? _ws;
    private string? _start;

    /// <summary>The last full test run in the lab and the sketch it ran on (the diff against the copy as it started).</summary>
    public TestRun? LastRun { get; private set; }
    public IReadOnlyList<string> LastSketchFiles { get; private set; } = [];
    public int TestRuns { get; private set; }

    /// <summary>Null if the job has a verified test environment to copy; otherwise why the lab can't open.</summary>
    public string? Unavailable
    {
        get
        {
            if (_checked) return _unavailable;
            _checked = true;
            try { _env = TestEnvironment.Of(job); }
            catch (InvalidOperationException e) { _unavailable = e.Message; }
            catch (IOException e) { _unavailable = e.Message; }
            return _unavailable;
        }
    }

    private async Task<Workspace> OpenAsync(CancellationToken ct)
    {
        if (_ws is not null) return _ws;
        if (Unavailable is { } why) throw new InvalidOperationException($"the lab is not available: {why}");
        var sandbox = new DockerSandbox(job.Id, suffix: suffix);
        await Workspace.CreateAsync(sandbox, _env!);
        _ws = await Workspace.OpenAsync(sandbox.Volume, _env!);
        _start = await _ws.MarkStartAsync(ct);
        return _ws;
    }

    public async Task<ToolOutput> RunAsync(string command, int? timeoutSeconds, CancellationToken ct)
    {
        if (command.Trim().Length == 0) return new ToolOutput("command is empty", true);
        var ws = await OpenAsync(ct);
        var seconds = Math.Clamp(timeoutSeconds ?? 120, 1, MaxCommandSeconds);
        var r = await ws.BashAsync(command, TimeSpan.FromSeconds(seconds), ct);
        var output = r.Stderr.Trim().Length == 0 ? r.Stdout : r.Stdout.TrimEnd() + "\n--- stderr\n" + r.Stderr;
        return new ToolOutput($"exit {r.ExitCode}{(r.TimedOut ? $" (timed out after {seconds} s)" : "")}, {r.Duration.TotalSeconds:0.0} s\n{Clip(output)}", false);
    }

    public async Task<ToolOutput> RunTestsAsync(CancellationToken ct)
    {
        var ws = await OpenAsync(ct);
        var n = ++TestRuns;
        var run = await ws.RunTestsAsync(Path.Combine(dir, "lab", $"tests-{n}"), ct);
        var sketch = await ws.DiffAsync(_start!, ct);
        File.WriteAllText(Path.Combine(dir, "lab", $"sketch-{n}.diff"), sketch);
        LastRun = run;
        LastSketchFiles = SketchFiles(sketch);
        if (run.Outcomes.Count == 0)
            return new ToolOutput($"The test command produced no report (exit {run.ExitCode}{(run.TimedOut ? ", timed out" : "")}, {run.Seconds} s): the sketch may not compile or collect. Last output:\n{run.LogTail}", false);
        var files = LastSketchFiles.Count == 0 ? "no sketch: the lab is as it started" : $"sketch: {LastSketchFiles.Count} file(s) changed ({string.Join(", ", LastSketchFiles.Take(8))})";
        return new ToolOutput($"{files}\n{run.Delta.Summary(25)}\n(exit {run.ExitCode}, {run.Seconds} s)", false);
    }

    /// <summary>Everything changed in the lab since it opened (caches and build outputs excluded).</summary>
    public async Task<string> DiffAsync(CancellationToken ct) => await (await OpenAsync(ct)).DiffAsync(_start!, ct);

    /// <summary>
    /// Puts the lab back to how it opened, applies <paramref name="patches"/> in order and runs the tests. The caller
    /// restores the writer's own state afterwards with <see cref="RestoreAsync"/>.
    /// </summary>
    public async Task<TestRun> RunStateAsync(string label, IReadOnlyList<string> patches, CancellationToken ct)
    {
        await RestoreAsync(patches, ct);
        return await (await OpenAsync(ct)).RunTestsAsync(Path.Combine(dir, "lab", label), ct);
    }

    /// <summary>The lab as it opened, plus <paramref name="patches"/>; throws with git's message if one doesn't apply.</summary>
    public async Task RestoreAsync(IReadOnlyList<string> patches, CancellationToken ct)
    {
        var ws = await OpenAsync(ct);
        await ws.ResetAsync(_start!, ct);
        foreach (var p in patches.Where(p => p.Trim().Length > 0))
            if (await ws.ApplyAsync(p, ct: ct) is { } error) throw new InvalidOperationException($"a patch doesn't apply in the lab: {error}");
    }

    /// <summary>The files a diff touches, from its <c>diff --git</c> headers.</summary>
    internal static IReadOnlyList<string> SketchFiles(string diff) =>
        diff.Replace("\r\n", "\n").Split('\n')
            .Where(l => l.StartsWith("diff --git a/", StringComparison.Ordinal))
            .Select(l => l["diff --git a/".Length..].Split(" b/")[0])
            .Distinct(StringComparer.Ordinal).ToList();

    private static string Clip(string s) =>
        s.Length <= MaxOutputChars ? s : s[..2_000] + $"\n... {s.Length - MaxOutputChars} characters cut ...\n" + s[^(MaxOutputChars - 2_000)..];

    public async ValueTask DisposeAsync()
    {
        if (_ws is not null) await _ws.DisposeAsync();
        _ws = null;
    }

    /// <summary>Removes the lab's container and volume. The stage calls it after the writer stops, however it stopped.</summary>
    public static async Task RemoveAsync(Job job, string suffix)
    {
        var sandbox = new DockerSandbox(job.Id, suffix: suffix);
        await Proc.RunAsync("docker", ["rm", "-f", $"{sandbox.Volume}-session"], timeout: TimeSpan.FromMinutes(1));
        await sandbox.RemoveAsync();
    }

    // ---------------------------------------------------------------- tool definitions

    public static readonly ToolDefinition Run = new("lab_run",
        "Run a bash command in your lab: a throwaway copy of the repository at /work/repo as recon set it up (dependencies installed, built), " +
        "as an unprivileged user with the network OFF. Use it to check what the code does now (call the function with the PRP's inputs, run one " +
        "existing test, grep the tests) and to sketch the change you are about to specify (edit files with sed, python or a heredoc). " +
        "The lab is thrown away; nothing in it reaches the PRP or the implementer.",
        RepoReader.Schema(new
        {
            type = "object",
            properties = new
            {
                command = new { type = "string", description = "Runs with bash in /work/repo." },
                timeout_seconds = new { type = "integer", description = $"Default 120, at most {MaxCommandSeconds}." },
            },
            required = new[] { "command" },
            additionalProperties = false,
        }));

    public static readonly ToolDefinition RunTests = new("lab_run_tests",
        "Run the verified test command in the lab and compare with the baseline: which tests that passed before now fail (each pins behaviour " +
        "your sketch changes), and how new tests fare. submit_spec holds the PRP to the last run.",
        RepoReader.Schema(new { type = "object", properties = new { }, additionalProperties = false }));

    internal static int? Int(IReadOnlyDictionary<string, JsonElement> input, string key) =>
        input.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
}
