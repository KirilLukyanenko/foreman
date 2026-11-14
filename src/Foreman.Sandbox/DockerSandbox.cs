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
/// <param name="AsRoot">
/// Install steps may need system packages (apt). They run as root with only the capabilities a package manager
/// needs; builds and tests always run as the unprivileged user with none.
/// </param>
public sealed record SandboxRun(string Image, string Script, Network Network, TimeSpan Timeout, IReadOnlyDictionary<string, string>? Env = null, bool AsRoot = false);

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

    /// <summary>Capabilities a package manager needs as root (dpkg sets owners and modes; apt drops to its own user).</summary>
    public static readonly string[] InstallCapabilities = ["CHOWN", "DAC_OVERRIDE", "FOWNER", "FSETID", "SETUID", "SETGID"];

    /// <param name="suffix">Separate volumes of one job: the agent's scratch copy, a validation copy.</param>
    public DockerSandbox(string jobId, SandboxLimits? limits = null, string? suffix = null)
    {
        Volume = "foreman-" + jobId.ToLowerInvariant() + (suffix is null ? "" : "-" + suffix);
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
    /// <paramref name="hostOutDir"/>. With <paramref name="commitAs"/>, a successful run's container is saved as that
    /// image: setup steps install system packages outside the volume, and only a snapshot keeps them for the test runs
    /// (the "setup script, then snapshot" pattern of Codex, Jules and Copilot).
    /// </summary>
    public async Task<ProcResult> RunAsync(SandboxRun run, string hostOutDir, CancellationToken ct = default, string? commitAs = null)
    {
        var id = $"{Interlocked.Increment(ref _runs)}-{Guid.NewGuid().ToString("N")[..6]}";
        var name = $"{Volume}-{id}";
        var outPath = $"{OutRoot}/{id}";
        List<string> args =
        [
            "run", .. (commitAs is null ? new[] { "--rm" } : []), "--name", name,
            .. PrivilegeArgs(run.AsRoot),
            .. LimitArgs(_limits),
            "--network", run.Network == Network.None ? "none" : "bridge",
            "-e", $"HOME={HomePath}",
            "-e", $"OUT={outPath}",
            "-e", "CI=true",
            "-e", "DOTNET_CLI_TELEMETRY_OPTOUT=1",
            "-e", "DOTNET_NOLOGO=1",
            "-e", "NPM_CONFIG_UPDATE_NOTIFIER=false",
            "-e", "PIP_DISABLE_PIP_VERSION_CHECK=1",
            "-e", "DEBIAN_FRONTEND=noninteractive",
            "-v", $"{Volume}:/work",
            "-w", RepoPath,
        ];
        foreach (var (k, v) in run.Env ?? new Dictionary<string, string>()) args.AddRange(["-e", $"{k}={v}"]);
        args.AddRange([run.Image, "bash", "-c", $"set -o pipefail\nmkdir -p \"$OUT\"\n{run.Script}"]);

        var result = await Proc.RunAsync("docker", args, timeout: run.Timeout, ct: ct);
        if (result.TimedOut)
            await Proc.RunAsync("docker", ["kill", name], timeout: TimeSpan.FromSeconds(30), ct: CancellationToken.None);

        if (commitAs is not null)
        {
            if (result.Ok)
            {
                var commit = await Proc.RunAsync("docker", ["commit", name, commitAs], timeout: TimeSpan.FromMinutes(10), ct: CancellationToken.None);
                if (!commit.Ok) throw new InvalidOperationException($"saving the setup snapshot failed: {commit.Tail(5)}");
            }
            await Proc.RunAsync("docker", ["rm", "-f", name], timeout: TimeSpan.FromMinutes(1), ct: CancellationToken.None);
        }

        await CopyOutAsync(outPath, hostOutDir, run.Image);
        return result;
    }

    /// <summary>Image name for a setup snapshot of this volume.</summary>
    public string SnapshotTag(string label) => $"foreman-env/{Volume}:{label.ToLowerInvariant()}";

    public static Task RemoveImageAsync(string image) => Proc.RunAsync("docker", ["rmi", "-f", image], timeout: TimeSpan.FromMinutes(2));

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

    /// <summary>Gives every file in the volume back to the unprivileged user, after a step that ran as root.</summary>
    public async Task ResetOwnershipAsync(string image)
    {
        var r = await Proc.RunAsync("docker",
            ["run", "--rm", "--network", "none", "--user", "0:0", "--cap-drop", "ALL", "--cap-add", "CHOWN", "--cap-add", "DAC_OVERRIDE", "--cap-add", "FOWNER",
             "-v", $"{Volume}:/work", image, "chown", "-R", Uid, "/work"],
            timeout: TimeSpan.FromMinutes(10));
        if (!r.Ok) throw new InvalidOperationException($"resetting ownership failed: {r.Tail(5)}");
    }

    public async Task RemoveAsync() => await Proc.RunAsync("docker", ["volume", "rm", "-f", Volume]);

    internal static IEnumerable<string> PrivilegeArgs(bool asRoot)
    {
        if (!asRoot) return ["--user", Uid, "--cap-drop", "ALL"];
        List<string> args = ["--user", "0:0", "--cap-drop", "ALL"];
        foreach (var cap in InstallCapabilities) args.AddRange(["--cap-add", cap]);
        return args;
    }

    internal static IEnumerable<string> LimitArgs(SandboxLimits limits) =>
    [
        "--security-opt", "no-new-privileges",
        "--pids-limit", limits.Pids.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "--memory", limits.Memory,
        "--cpus", limits.Cpus.ToString(System.Globalization.CultureInfo.InvariantCulture),
    ];

    private static async Task Docker(params string[] args)
    {
        var r = await Proc.RunAsync("docker", args, timeout: TimeSpan.FromMinutes(10));
        if (!r.Ok) throw new InvalidOperationException($"docker {args[0]} failed: {r.Tail(5)}");
    }
}
