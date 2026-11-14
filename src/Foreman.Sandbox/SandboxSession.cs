using Foreman.Core;

namespace Foreman.Sandbox;

/// <summary>
/// A long-running container on a job volume, for an agent that explores step by step: each command is a
/// <c>docker exec</c> into it, so installed packages and files persist between commands. The image can be swapped;
/// the volume (repository copy and home) stays. Commands run as root with only the install capabilities, because
/// finding the right setup often needs system packages; the network is open. Nothing from the host is mounted.
/// </summary>
public sealed class SandboxSession : IAsyncDisposable
{
    private readonly SandboxLimits _limits;

    private SandboxSession(string name, string volume, string image, SandboxLimits limits)
    {
        Name = name;
        Volume = volume;
        Image = image;
        _limits = limits;
    }

    public string Name { get; }
    public string Volume { get; }
    public string Image { get; private set; }

    public static async Task<SandboxSession> StartAsync(string volume, string image, SandboxLimits? limits = null)
    {
        var s = new SandboxSession($"{volume}-session", volume, image, limits ?? SandboxLimits.Default);
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

    /// <summary>Runs a bash command in /work/repo. The timeout is enforced inside the container and again outside.</summary>
    public async Task<ProcResult> ExecAsync(string command, TimeSpan timeout, CancellationToken ct = default)
    {
        var seconds = (int)Math.Max(1, timeout.TotalSeconds);
        var r = await Proc.RunAsync("docker",
            ["exec", "-w", DockerSandbox.RepoPath, Name, "timeout", "-k", "10", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), "bash", "-c", command],
            timeout: timeout + TimeSpan.FromSeconds(30), ct: ct);
        return r with { TimedOut = r.TimedOut || r.ExitCode == 124 };
    }

    private async Task StartContainerAsync()
    {
        await DockerSandbox.EnsureImageAsync(Image);
        await RemoveContainerAsync();
        List<string> args = ["run", "-d", "--name", Name, .. DockerSandbox.PrivilegeArgs(asRoot: true), .. DockerSandbox.LimitArgs(_limits),
            "--network", "bridge",
            "-e", $"HOME={DockerSandbox.HomePath}", "-e", "CI=true", "-e", "DEBIAN_FRONTEND=noninteractive",
            "-e", "DOTNET_CLI_TELEMETRY_OPTOUT=1", "-e", "DOTNET_NOLOGO=1",
            "-v", $"{Volume}:/work", "-w", DockerSandbox.RepoPath,
            Image, "sleep", "infinity"];
        var r = await Proc.RunAsync("docker", args, timeout: TimeSpan.FromMinutes(2));
        if (!r.Ok) throw new InvalidOperationException($"starting the session container on {Image} failed: {r.Tail(5)}");
    }

    private Task RemoveContainerAsync() => Proc.RunAsync("docker", ["rm", "-f", Name], timeout: TimeSpan.FromMinutes(1));

    public async ValueTask DisposeAsync() => await RemoveContainerAsync();
}
