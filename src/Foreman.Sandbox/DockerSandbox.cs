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

/// <summary>A script to run in the sandbox. It writes files it wants to keep to <c>$OUT</c>.</summary>
public sealed record SandboxRun(string Image, string Script, Network Network, TimeSpan Timeout, IReadOnlyDictionary<string, string>? Env = null);

/// <summary>
/// One Docker volume per job holds the repository copy (<c>/work/repo</c>), a home directory with package caches
/// (<c>/work/home</c>) and run outputs (<c>/work/out</c>). The untrusted code runs as a non-root user with no
/// capabilities and never sees the host filesystem: files move in and out with <c>docker cp</c>, not bind mounts.
/// That also works where bind mounts don't (a remote Docker host, or a host folder Docker can't see). No secrets
/// are passed in.
/// </summary>
public sealed class DockerSandbox
{
    public const string Uid = "1000:1000";
    public const string RepoPath = "/work/repo";
    public const string HomePath = "/work/home";
    public const string OutRoot = "/work/out";

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
    /// Recreates the volume and copies the repository into it, then deletes <paramref name="excluded"/> (agent
    /// configuration from the repository: Claude Code has had CVEs where a repository's settings ran code before the
    /// trust dialog). The finishing step runs as root because it sets ownership; it runs only our own commands.
    /// </summary>
    public async Task PopulateAsync(string hostRepoDir, string helperImage, IEnumerable<string> excluded)
    {
        await Proc.RunAsync("docker", ["volume", "rm", "-f", Volume]);
        await Docker("volume", "create", Volume);

        var remove = string.Concat(excluded.Select(p => $" {RepoPath}/'{p.Replace("'", "")}'"));
        var finish = $"set -e; mkdir -p {HomePath} {OutRoot};" + (remove.Length > 0 ? $" rm -rf{remove};" : "") + $" chown -R {Uid} /work";
        var helper = $"{Volume}-populate";
        await Proc.RunAsync("docker", ["rm", "-f", helper]);
        await Docker("create", "--name", helper, "--network", "none", "-v", $"{Volume}:/work", helperImage, "sh", "-c", finish);
        try
        {
            await Docker("cp", Path.GetFullPath(hostRepoDir) + Path.DirectorySeparatorChar + ".", $"{helper}:{RepoPath}");
            var r = await Proc.RunAsync("docker", ["start", "-a", helper], timeout: TimeSpan.FromMinutes(10));
            if (!r.Ok) throw new InvalidOperationException($"preparing the sandbox copy failed: {r.Tail(10)}");
        }
        finally
        {
            await Proc.RunAsync("docker", ["rm", "-f", helper]);
        }
    }

    /// <summary>
    /// Runs a bash script in the repository folder, then copies what it wrote to <c>$OUT</c> into
    /// <paramref name="hostOutDir"/>.
    /// </summary>
    public async Task<ProcResult> RunAsync(SandboxRun run, string hostOutDir, CancellationToken ct = default)
    {
        var id = $"{Interlocked.Increment(ref _runs)}-{Guid.NewGuid().ToString("N")[..6]}";
        var name = $"{Volume}-{id}";
        var outPath = $"{OutRoot}/{id}";
        List<string> args =
        [
            "run", "--rm", "--name", name,
            "--user", Uid,
            "--cap-drop", "ALL",
            "--security-opt", "no-new-privileges",
            "--pids-limit", _limits.Pids.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--memory", _limits.Memory,
            "--cpus", _limits.Cpus.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--network", run.Network == Network.None ? "none" : "bridge",
            "-e", $"HOME={HomePath}",
            "-e", $"OUT={outPath}",
            "-e", "CI=true",
            "-e", "DOTNET_CLI_TELEMETRY_OPTOUT=1",
            "-e", "DOTNET_NOLOGO=1",
            "-e", "NPM_CONFIG_UPDATE_NOTIFIER=false",
            "-e", "PIP_DISABLE_PIP_VERSION_CHECK=1",
            "-v", $"{Volume}:/work",
            "-w", RepoPath,
        ];
        foreach (var (k, v) in run.Env ?? new Dictionary<string, string>()) args.AddRange(["-e", $"{k}={v}"]);
        args.AddRange([run.Image, "bash", "-c", $"set -o pipefail\nmkdir -p \"$OUT\"\n{run.Script}"]);

        var result = await Proc.RunAsync("docker", args, timeout: run.Timeout, ct: ct);
        if (result.TimedOut)
            await Proc.RunAsync("docker", ["kill", name], timeout: TimeSpan.FromSeconds(30), ct: CancellationToken.None);

        await CopyOutAsync(outPath, hostOutDir, run.Image);
        return result;
    }

    private async Task CopyOutAsync(string containerPath, string hostDir, string image)
    {
        Directory.CreateDirectory(hostDir);
        var helper = $"{Volume}-copy-{Guid.NewGuid().ToString("N")[..6]}";
        await Docker("create", "--name", helper, "--network", "none", "-v", $"{Volume}:/work", image, "true");
        try
        {
            // A run that died before creating $OUT leaves nothing to copy; that is not an error here.
            await Proc.RunAsync("docker", ["cp", $"{helper}:{containerPath}/.", Path.GetFullPath(hostDir)]);
        }
        finally
        {
            await Proc.RunAsync("docker", ["rm", "-f", helper]);
        }
    }

    public async Task RemoveAsync() => await Proc.RunAsync("docker", ["volume", "rm", "-f", Volume]);

    private static async Task Docker(params string[] args)
    {
        var r = await Proc.RunAsync("docker", args, timeout: TimeSpan.FromMinutes(10));
        if (!r.Ok) throw new InvalidOperationException($"docker {args[0]} failed: {r.Tail(5)}");
    }
}
