using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Foreman.Core;
using Foreman.Sandbox;

namespace Foreman.Recon.Environment;

public sealed record ToolOutput(string Text, bool IsError);

/// <summary>
/// The environment agent's whole world: four tools, all acting inside the sandbox. Shared by every driver (our own
/// Claude API loop, or Claude Code through Foreman's MCP server), so drivers differ only in the model loop. Every
/// call is appended to <c>tools.jsonl</c>; the outcome is written to <c>agent-result.json</c>.
/// </summary>
public sealed partial class EnvironmentTools : IAsyncDisposable
{
    public const int MaxToolCalls = 80;
    public const int MaxCommandSeconds = 1200;
    public const double MinPassRateWithoutReason = 0.5;
    private const int HeadChars = 2_000;
    private const int TailChars = 6_000;

    public const string RunDescription = """
        Run a bash command in the sandbox, in /work/repo (the repository). You are root, with the network on;
        HOME is /work/home. Files persist between calls; the shell does not (no cd or exports carry over).
        Returns the exit code and the output (long output is cut in the middle). Default timeout 300 s, max 1200 s.
        """;

    public const string UseImageDescription = """
        Restart the sandbox on another Docker image, for example node:22-bookworm, python:3.11-bookworm,
        golang:1.24-bookworm, rust:1-bookworm or mcr.microsoft.com/dotnet/sdk:9.0. Only official Docker Hub images and
        mcr.microsoft.com/dotnet images, with an explicit tag. /work survives the switch; system packages don't.
        """;

    public const string SubmitDescription = """
        Submit the environment for verification. Foreman runs it from scratch in a fresh copy of the repository:
        setup_script as root with the network on, then test_script as an unprivileged user with the network OFF.
        test_script must write a machine-readable report to the folder in $OUT: JUnit XML (*.xml), TRX (*.trx) or
        Jest JSON (jest*.json). You get back how many tests ran, passed and failed, or why verification failed.
        If fewer than half of the tests pass, also give expected_failures_reason (for example, the integration tests
        need a database that the sandbox does not provide); otherwise fix the environment.
        """;

    public const string GiveUpDescription = """
        Stop without an environment, when the repository cannot be built or tested in this sandbox. Give the concrete
        reason and what a human would need to provide.
        """;

    private readonly Job _job;
    private readonly string _stack;
    private readonly string _dir;
    private readonly EnvironmentValidator _validator;
    private readonly IReadOnlyList<string> _excluded;
    private readonly SandboxLimits? _limits;
    private readonly IReadOnlyDictionary<string, string> _env;
    private SandboxSession? _session;
    private int _validations;

    public EnvironmentTools(Job job, string stack, IReadOnlyList<string> excludedPaths, IReadOnlyDictionary<string, string>? env = null, SandboxLimits? limits = null)
    {
        _job = job;
        _stack = stack;
        _excluded = excludedPaths;
        _limits = limits;
        _env = env ?? new Dictionary<string, string>();
        _dir = job.StageDir(Path.Combine("environment", stack));
        _validator = new EnvironmentValidator(job, excludedPaths, limits);
    }

    public int ToolCalls { get; private set; }
    public AgentResult? Result { get; private set; }
    public bool Finished => Result is not null;
    public string ResultFile => Path.Combine(_dir, "agent-result.json");

    /// <summary>Copies the repository into the agent's own volume and starts the session container.</summary>
    public async Task StartAsync(string image)
    {
        var scratch = new DockerSandbox(_job.Id, _limits, $"agent-{_stack}");
        await DockerSandbox.EnsureImageAsync(image);
        await scratch.PopulateAsync(_job.SourceDir, image, _excluded);
        _session = await SandboxSession.StartAsync(scratch.Volume, image, _limits);
        if (File.Exists(ResultFile)) File.Delete(ResultFile);
    }

    // ---------------------------------------------------------------- tools

    public async Task<ToolOutput> RunAsync(string command, int? timeoutSeconds, CancellationToken ct = default)
    {
        if (Gate() is { } stop) return stop;
        var seconds = Math.Clamp(timeoutSeconds ?? 300, 1, MaxCommandSeconds);
        var r = await Session.ExecAsync(command, TimeSpan.FromSeconds(seconds), ct);
        var text = $"exit {r.ExitCode}{(r.TimedOut ? $" (timed out after {seconds} s)" : "")}, {r.Duration.TotalSeconds:0.0} s\n{Clip(Join(r.Stdout, r.Stderr))}";
        Log("run", new { command, seconds }, r.ExitCode, text);
        return new ToolOutput(text, false);
    }

    public async Task<ToolOutput> UseImageAsync(string image, CancellationToken ct = default)
    {
        if (Gate() is { } stop) return stop;
        if (!IsAllowedImage(image))
        {
            var refused = $"refused: {image} is not an official Docker Hub image or an mcr.microsoft.com/dotnet image with an explicit tag";
            Log("use_image", new { image }, null, refused);
            return new ToolOutput(refused, true);
        }
        try
        {
            await Session.SwitchImageAsync(image);
            Log("use_image", new { image }, 0, "ok");
            return new ToolOutput($"the sandbox now runs {image}; /work is unchanged", false);
        }
        catch (InvalidOperationException e)
        {
            Log("use_image", new { image }, null, e.Message);
            return new ToolOutput(e.Message, true);
        }
    }

    public async Task<ToolOutput> SubmitAsync(string image, string setupScript, string testScript, string? expectedFailuresReason, CancellationToken ct = default)
    {
        if (Gate() is { } stop) return stop;
        if (!IsAllowedImage(image)) return Logged("submit", new { image }, new ToolOutput($"refused: {image} is not an allowed image", true));

        var spec = new EnvironmentSpec(_stack, "agent", image,
            [new EnvStep("setup", setupScript, Network: true, AsRoot: true, TimeoutMinutes: 30)],
            testScript, _env, expectedFailuresReason is { Length: > 0 } r ? [$"expected failures: {r}"] : []);

        var v = await _validator.ValidateAsync(spec, Path.Combine(_dir, $"validation-{++_validations}"), ct);
        string text;
        if (!v.Ok)
        {
            text = $"verification FAILED in a fresh copy: {v.Reason}\nlast output:\n{v.LogTail}";
        }
        else if (v.PassRate < MinPassRateWithoutReason && string.IsNullOrWhiteSpace(expectedFailuresReason))
        {
            text = $"verification ran {v.Tests} tests but only {v.Passed} passed ({v.Failed} failed, {v.Skipped} skipped). " +
                   "Fix the environment, or submit again with expected_failures_reason if these failures are not about the environment.\n" +
                   $"last output:\n{v.LogTail}";
        }
        else
        {
            Result = new AgentResult(spec, v, expectedFailuresReason, null, ToolCalls);
            _job.WriteJson(Path.Combine("environment", _stack), "agent-result.json", Result);
            text = $"ACCEPTED: {v.Tests} tests ran in a fresh copy, {v.Passed} passed, {v.Failed} failed, {v.Skipped} skipped. " +
                   "You are done: reply with two or three sentences on what the environment needed, and stop.";
        }
        return Logged("submit", new { image, setupScript, testScript, expectedFailuresReason }, new ToolOutput(text, !v.Ok), v.Ok ? 0 : 1);
    }

    public Task<ToolOutput> GiveUpAsync(string reason)
    {
        if (Finished) return Task.FromResult(new ToolOutput("already finished; stop now", true));
        ToolCalls++;
        Result = new AgentResult(null, null, null, reason, ToolCalls);
        _job.WriteJson(Path.Combine("environment", _stack), "agent-result.json", Result);
        return Task.FromResult(Logged("give_up", new { reason }, new ToolOutput("recorded; stop now", false)));
    }

    /// <summary>Dispatch by name, for drivers that receive tool calls as JSON (our own API loop).</summary>
    public async Task<ToolOutput> CallAsync(string name, IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct = default)
    {
        string? Str(string key) => input.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        int? Int(string key) => input.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

        return name switch
        {
            "run" when Str("command") is { } c => await RunAsync(c, Int("timeout_seconds"), ct),
            "use_image" when Str("image") is { } i => await UseImageAsync(i, ct),
            "submit" when Str("image") is { } i && Str("setup_script") is { } s && Str("test_script") is { } t
                => await SubmitAsync(i, s, t, Str("expected_failures_reason"), ct),
            "give_up" when Str("reason") is { } r => await GiveUpAsync(r),
            "run" or "use_image" or "submit" or "give_up" => new ToolOutput($"invalid input for {name}: a required field is missing", true),
            _ => new ToolOutput($"unknown tool {name}", true),
        };
    }

    /// <summary>JSON schemas of the tools, for drivers that declare tools themselves.</summary>
    public static IReadOnlyList<(string Name, string Description, JsonElement Schema)> Definitions { get; } =
    [
        ("run", RunDescription, Schema(new
        {
            type = "object",
            properties = new
            {
                command = new { type = "string", description = "The bash command or script." },
                timeout_seconds = new { type = "integer", description = $"Timeout in seconds, 1 to {MaxCommandSeconds}. Default 300." },
            },
            required = new[] { "command" },
            additionalProperties = false,
        })),
        ("use_image", UseImageDescription, Schema(new
        {
            type = "object",
            properties = new { image = new { type = "string", description = "Image with tag, e.g. node:22-bookworm." } },
            required = new[] { "image" },
            additionalProperties = false,
        })),
        ("submit", SubmitDescription, Schema(new
        {
            type = "object",
            properties = new
            {
                image = new { type = "string", description = "The image both scripts run in." },
                setup_script = new { type = "string", description = "Bash script run once as root with the network on, in /work/repo of a fresh copy: system packages, toolchain, dependencies, build." },
                test_script = new { type = "string", description = "Bash script run as an unprivileged user with the network off, in /work/repo. Runs the project's tests and writes the report into \"$OUT\"." },
                expected_failures_reason = new { type = "string", description = "Only when fewer than half of the tests pass: why those failures are not about the environment." },
            },
            required = new[] { "image", "setup_script", "test_script" },
            additionalProperties = false,
        })),
        ("give_up", GiveUpDescription, Schema(new
        {
            type = "object",
            properties = new { reason = new { type = "string", description = "What blocks the build or the tests, and what a human would have to provide." } },
            required = new[] { "reason" },
            additionalProperties = false,
        })),
    ];

    // ---------------------------------------------------------------- helpers

    public static bool IsAllowedImage(string image) => OfficialImage().IsMatch(image) || DotnetImage().IsMatch(image);

    internal static string Clip(string text)
    {
        if (text.Length <= HeadChars + TailChars) return text;
        var cut = text.Length - HeadChars - TailChars;
        return $"{text[..HeadChars]}\n... [{cut} characters cut] ...\n{text[^TailChars..]}";
    }

    private static string Join(string stdout, string stderr) =>
        stderr.Trim().Length == 0 ? stdout : stdout.Trim().Length == 0 ? stderr : stdout + "\n--- stderr\n" + stderr;

    private SandboxSession Session => _session ?? throw new InvalidOperationException("call StartAsync first");

    private ToolOutput? Gate()
    {
        if (Finished) return new ToolOutput("already finished; stop now", true);
        if (++ToolCalls > MaxToolCalls)
            return new ToolOutput($"tool budget of {MaxToolCalls} calls is used up: call submit with your best environment, or give_up", true);
        return null;
    }

    private ToolOutput Logged(string tool, object input, ToolOutput output, int? exit = null)
    {
        Log(tool, input, exit, output.Text);
        return output;
    }

    private void Log(string tool, object input, int? exit, string output)
    {
        using var span = Telemetry.Source.StartActivity($"execute_tool {tool}", ActivityKind.Internal);
        span?.SetTag("gen_ai.operation.name", "execute_tool");
        span?.SetTag("gen_ai.tool.name", tool);
        span?.SetTag("foreman.job.id", _job.Id);
        var line = JsonSerializer.Serialize(new { ts = DateTime.UtcNow, call = ToolCalls, tool, input, exit, output = Clip(output) }, Json.Line);
        File.AppendAllText(Path.Combine(_dir, "tools.jsonl"), line + "\n", new UTF8Encoding(false));
        _job.Event("environment", "tool", new { stack = _stack, call = ToolCalls, tool, exit });
    }

    private static JsonElement Schema(object o) => JsonSerializer.SerializeToElement(o);

    [GeneratedRegex(@"^[a-z0-9]+(?:[._-][a-z0-9]+)*:[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$")]
    private static partial Regex OfficialImage();

    [GeneratedRegex(@"^mcr\.microsoft\.com/dotnet/(sdk|runtime|aspnet|runtime-deps):[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$")]
    private static partial Regex DotnetImage();

    public async ValueTask DisposeAsync()
    {
        if (_session is not null)
        {
            await _session.DisposeAsync();
            await new DockerSandbox(_job.Id, _limits, $"agent-{_stack}").RemoveAsync();
        }
    }
}
