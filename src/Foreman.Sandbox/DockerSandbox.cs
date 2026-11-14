using Foreman.Core;

namespace Foreman.Sandbox;

public enum Network
{
    /// <summary>No network at all: build and test runs, so results can't depend on the internet.</summary>
    None,

    /// <summary>Default bridge network: package installs only. An egress allowlist proxy is planned.</summary>
    Open,
}

public sealed record SandboxLimits(double Cpus = 4, string Memory = "6g", int Pids = 4096)
{
    public static readonly SandboxLimits Default = new();
}

public sealed record SandboxRun(string Image, string Script, Network Network, TimeSpan Timeout, IReadOnlyDictionary<string, string>? Env = null);

/// <summary>
/// One Docker volume per job holds the repository copy (<c>/work/repo</c>) and a home directory with package caches
/// (<c>/work/home</c>). The untrusted code runs as a non-root user with no capabilities and never sees the host
/// filesystem: the only bind mount is the stage's output folder at <c>/out</c>. No secrets are passed in.
/// </summary>
public sealed class DockerSandbox
{
    public const string Uid = "1000:1000";
    public const string RepoPath = "/work/repo";
    public const string HomePath = "/work/home";

    private readonly SandboxLimits _limits;
    private int _runs;

    public DockerSandbox(string jobId, SandboxLimits? limits = null)
    {
        Volume = "foreman-" + jobId.ToLowerInvariant();
        _limits = limits ?? SandboxLimits.Default;
    }

    public string Volume { get; }

    public static async Task EnsureDockerAsync()
    {
        var r = await Proc.RunAsync("docker", ["info", "--format", "{{.ServerVersion}}"], timeout: TimeSpan.FromSeconds(30));
        if (!r.Ok) throw new InvalidOperationException("Docker is not running: " + r.Tail(5));
    }

    /// <summary>Pulls the image if it is missing and returns its content digest, for the run record.</summary>
    public static async Task<string> EnsureImageAsync(string image)
    {
        var inspect = await Proc.RunAsync("docker", ["image", "inspect", "--format", "{{json .RepoDigests}}", image]);
        if (!inspect.Ok)
        {
            var pull = await Proc.RunAsync("docker", ["pull", "--quiet", image], timeout: TimeSpan.FromMinutes(15));
            if (!pull.Ok) throw new InvalidOperationException($"docker pull {image} failed: {pull.Tail(5)}");
            inspect = await Proc.RunAsync("docker", ["image", "inspect", "--format", "{{json .RepoDigests}}", image]);
        }
        var digests = Json.Read<string[]>(inspect.Stdout.Trim()) ?? [];
        return digests.FirstOrDefault() ?? image;
    }

    /// <summary>
    /// Creates the volume and copies the repository into it, without <paramref name="excluded"/> (agent configuration
    /// from the repository: Claude Code has had CVEs where a repository's settings ran code before the trust dialog).
    /// This one step runs as root with the default capabilities, because it sets ownership; it runs only our own
    /// copy command.
    /// </summary>
    public async Task PopulateAsync(string hostRepoDir, string helperImage, IEnumerable<string> excluded)
    {
        var create = await Proc.RunAsync("docker", ["volume", "create", Volume]);
        if (!create.Ok) throw new InvalidOperationException($"docker volume create failed: {create.Tail(5)}");

        var remove = string.Concat(excluded.Select(p => $" {RepoPath}/'{p.Replace("'", "")}'"));
        var script = $"set -e; rm -rf {RepoPath} {HomePath}; mkdir -p {RepoPath} {HomePath}; cp -a /src/. {RepoPath}/;"
            + (remove.Length > 0 ? $" rm -rf{remove};" : "") + $" chown -R {Uid} /work";
        var r = await Proc.RunAsync("docker",
            ["run", "--rm", "--network", "none", "-v", $"{Volume}:/work", "-v", $"{Path.GetFullPath(hostRepoDir)}:/src:ro", helperImage, "sh", "-c", script],
            timeout: TimeSpan.FromMinutes(10));
        if (!r.Ok) throw new InvalidOperationException($"copying the repository into the sandbox failed: {r.Tail(10)}");
    }

    /// <summary>Runs a bash script in the repository folder. <paramref name="outDir"/> is mounted at /out.</summary>
    public async Task<ProcResult> RunAsync(SandboxRun run, string outDir, CancellationToken ct = default)
    {
        Directory.CreateDirectory(outDir);
        var name = $"{Volume}-{Interlocked.Increment(ref _runs)}-{Guid.NewGuid().ToString("N")[..6]}";
        List<string> args =
        [
            "run", "--rm", "--name", name,
            "--user", Uid,
            "--cap-drop", "ALL",
            "--security-opt", "no-new-privileges",
            "--pids-limit", _limits.Pids.ToString(),
            "--memory", _limits.Memory,
            "--cpus", _limits.Cpus.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--network", run.Network == Network.None ? "none" : "bridge",
            "-e", $"HOME={HomePath}",
            "-e", "CI=true",
            "-e", "DOTNET_CLI_TELEMETRY_OPTOUT=1",
            "-e", "DOTNET_NOLOGO=1",
            "-e", "NPM_CONFIG_UPDATE_NOTIFIER=false",
            "-e", "PIP_DISABLE_PIP_VERSION_CHECK=1",
            "-v", $"{Volume}:/work",
            "-v", $"{Path.GetFullPath(outDir)}:/out",
            "-w", RepoPath,
        ];
        foreach (var (k, v) in run.Env ?? new Dictionary<string, string>()) args.AddRange(["-e", $"{k}={v}"]);
        args.AddRange([run.Image, "bash", "-c", "set -o pipefail\n" + run.Script]);

        var result = await Proc.RunAsync("docker", args, timeout: run.Timeout, ct: ct);
        if (result.TimedOut)
            await Proc.RunAsync("docker", ["kill", name], timeout: TimeSpan.FromSeconds(30), ct: CancellationToken.None);
        return result;
    }

    public async Task RemoveAsync() => await Proc.RunAsync("docker", ["volume", "rm", "-f", Volume]);
}
