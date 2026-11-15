using Foreman.Core;

namespace Foreman.Sandbox;

/// <summary>How a session's container runs.</summary>
/// <param name="AsRoot">Root with only the install capabilities (finding a setup), or the unprivileged user with none.</param>
/// <param name="Network">Open for finding a setup; none for writing code and running tests.</param>
public sealed record SessionOptions(bool AsRoot = true, Network Network = Network.Open, IReadOnlyDictionary<string, string>? Env = null)
{
    /// <summary>Writing and testing code: the unprivileged user, no capabilities, no network.</summary>
    public static SessionOptions Offline(IReadOnlyDictionary<string, string>? env = null) => new(AsRoot: false, Network.None, env);
}

/// <summary>
/// A long-running container on a job volume, for an agent that works step by step: each command is a
/// <c>docker exec</c> into it, so files and installed packages persist between commands. The image can be swapped;
/// the volume (repository copy and home) stays. The environment agent's session runs as root with the install
/// capabilities and the network open, because finding a setup often needs system packages; the implementer's runs as
/// the unprivileged user with no network. Nothing from the host is mounted.
/// </summary>
public sealed class SandboxSession : IAsyncDisposable
{
    private readonly SandboxLimits _limits;
    private readonly SessionOptions _options;

    private SandboxSession(string name, string volume, string image, SandboxLimits limits, SessionOptions options)
    {
        Name = name;
        Volume = volume;
        Image = image;
        _limits = limits;
        _options = options;
    }

    public string Name { get; }
    public string Volume { get; }
    public string Image { get; private set; }

    public static async Task<SandboxSession> StartAsync(string volume, string image, SandboxLimits? limits = null, SessionOptions? options = null)
    {
        var s = new SandboxSession($"{volume}-session", volume, image, limits ?? SandboxLimits.Default, options ?? new SessionOptions());
        await s.StartContainerAsync();
        return s;
    }

    /// <summary>Restarts the session on another image. Files in /work survive; system packages don't.</summary>
    public async Task SwitchImageAsync(string image)
    {
        await RemoveContainerAsync();
        Image = image;
        await StartContainerAsync();
    }

    /// <summary>
    /// Runs a bash command in /work/repo. The timeout is enforced inside the container and again outside.
    /// <paramref name="stdin"/> is passed to the command (file contents, a patch).
    /// </summary>
    public async Task<ProcResult> ExecAsync(string command, TimeSpan timeout, CancellationToken ct = default, string? stdin = null)
    {
        var seconds = (int)Math.Max(1, timeout.TotalSeconds);
        List<string> args = ["exec", .. (stdin is null ? Array.Empty<string>() : ["-i"]), "-w", DockerSandbox.RepoPath, Name,
            "timeout", "-k", "10", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), "bash", "-c", command];
        var r = await Proc.RunAsync("docker", args, timeout: timeout + TimeSpan.FromSeconds(30), ct: ct, stdin: stdin);
        return r with { TimedOut = r.TimedOut || r.ExitCode == 124 };
    }

    /// <summary>Copies a folder out of the container (test reports); a missing folder copies nothing.</summary>
    public async Task CopyOutAsync(string containerPath, string hostDir)
    {
        Directory.CreateDirectory(hostDir);
        await Proc.RunAsync("docker", ["cp", $"{Name}:{containerPath}/.", Path.GetFullPath(hostDir)], timeout: TimeSpan.FromMinutes(5));
    }

    private async Task StartContainerAsync()
    {
        await DockerSandbox.EnsureImageAsync(Image);
        await RemoveContainerAsync();
        List<string> args = ["run", "-d", "--name", Name, .. DockerSandbox.PrivilegeArgs(_options.AsRoot), .. DockerSandbox.LimitArgs(_limits),
            "--network", _options.Network == Network.None ? "none" : "bridge",
            "-e", $"HOME={DockerSandbox.HomePath}", "-e", "CI=true", "-e", "DEBIAN_FRONTEND=noninteractive",
            "-e", "DOTNET_CLI_TELEMETRY_OPTOUT=1", "-e", "DOTNET_NOLOGO=1",
            "-e", "NPM_CONFIG_UPDATE_NOTIFIER=false", "-e", "PIP_DISABLE_PIP_VERSION_CHECK=1"];
        foreach (var (k, v) in _options.Env ?? new Dictionary<string, string>()) args.AddRange(["-e", $"{k}={v}"]);
        args.AddRange(["-v", $"{Volume}:/work", "-w", DockerSandbox.RepoPath, Image, "sleep", "infinity"]);
        var r = await Proc.RunAsync("docker", args, timeout: TimeSpan.FromMinutes(2));
        if (!r.Ok) throw new InvalidOperationException($"starting the session container on {Image} failed: {r.Tail(5)}");
    }

    private Task RemoveContainerAsync() => Proc.RunAsync("docker", ["rm", "-f", Name], timeout: TimeSpan.FromMinutes(1));

    public async ValueTask DisposeAsync() => await RemoveContainerAsync();
}
