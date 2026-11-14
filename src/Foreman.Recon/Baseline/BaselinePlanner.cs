using System.Globalization;
using System.Text.RegularExpressions;
using Foreman.Recon.Triage;
using Foreman.Sandbox;

namespace Foreman.Recon.Baseline;

public sealed record PlanStep(string Name, string Script, Network Network, TimeSpan Timeout);

/// <summary>
/// The deterministic first attempt at building and testing one stack. Simple repositories need nothing more;
/// for the rest, the environment agent starts from this plan and the CI commands.
/// </summary>
public sealed record StackPlan(
    string Stack,
    string Image,
    IReadOnlyList<PlanStep> Setup,
    string? TestScript,
    IReadOnlyDictionary<string, string> TestEnv,
    IReadOnlyList<string> Notes,
    string? Unsupported = null);

public static partial class BaselinePlanner
{
    private static readonly TimeSpan Install = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan Build = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(20);

    public static IReadOnlyList<StackPlan> Plan(TriageReport triage)
    {
        var plans = new List<StackPlan>();
        if (triage.Dotnet is { } d) plans.Add(Dotnet(d));
        if (triage.Node is { } n) plans.Add(Node(n));
        if (triage.Python is { } p) plans.Add(Python(p));
        return plans;
    }

    // ---------------------------------------------------------------- .NET

    internal static StackPlan Dotnet(DotnetStack s)
    {
        var notes = new List<string>();
        var channel = DotnetChannel(s, notes);
        var image = $"mcr.microsoft.com/dotnet/sdk:{channel}";

        if (s.TestProjects.Count == 0)
            notes.Add("no test projects found (no test SDK or framework reference)");

        // One root solution builds everything; otherwise build and test each test project.
        var rootSolution = s.Solutions.FirstOrDefault(f => RepoFiles.Depth(f) == 0);
        var targets = rootSolution is not null ? [rootSolution] : s.TestProjects.Count > 0 ? s.TestProjects : s.Projects.Take(1).ToList();

        var restore = string.Join("\n", targets.Select(t => $"dotnet restore {Sh.Quote(t)}"));
        var build = string.Join("\n", targets.Select(t => $"dotnet build {Sh.Quote(t)} --no-restore -c Debug -nologo"));

        string test;
        if (s.TestRunner == "mtp")
        {
            notes.Add("global.json selects Microsoft.Testing.Platform: TRX needs the TrxReport extension in the test projects");
            test = string.Join("\n", targets.Select(t =>
                $"dotnet test {(t.EndsWith("proj", StringComparison.OrdinalIgnoreCase) ? "--project" : "--solution")} {Sh.Quote(t)} --no-build --report-trx --results-directory /out"));
        }
        else
        {
            test = string.Join("\n", targets.Select(t =>
                $"dotnet test {Sh.Quote(t)} --no-build --logger \"trx;LogFilePrefix=tests\" --results-directory /out"));
        }

        // Test projects for an older runtime still run on the image's newer one.
        var env = new Dictionary<string, string> { ["DOTNET_ROLL_FORWARD"] = "Major" };
        notes.Add($"DOTNET_ROLL_FORWARD=Major: test hosts for older target frameworks run on the .NET {channel} runtime");

        return new StackPlan("dotnet", image,
            [new PlanStep("restore", restore, Network.Open, Install), new PlanStep("build", build, Network.None, Build)],
            s.TestProjects.Count == 0 ? null : test, env, notes);
    }

    internal static string DotnetChannel(DotnetStack s, List<string> notes)
    {
        if (s.SdkVersion is { } sdk && Regex.Match(sdk, @"^(\d+)\.(\d+)") is { Success: true } m)
        {
            notes.Add($"global.json pins SDK {sdk}");
            return $"{m.Groups[1].Value}.{m.Groups[2].Value}";
        }
        var highest = s.TargetFrameworks
            .Select(tf => Regex.Match(tf, @"^net(\d+)\.(\d+)"))
            .Where(m => m.Success)
            .Select(m => (Major: int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), Minor: int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)))
            .DefaultIfEmpty((Major: 10, Minor: 0))
            .Max();
        return highest.Major < 8 ? "8.0" : $"{highest.Major}.{highest.Minor}";
    }

    // ---------------------------------------------------------------- Node

    internal static StackPlan Node(NodeStack s)
    {
        var notes = new List<string>();
        var major = NodeMajor(s.NodeVersion);
        var image = $"node:{major}-bookworm";
        var dir = Path.GetDirectoryName(s.PackageJson)?.Replace('\\', '/') ?? "";
        var cd = dir.Length == 0 ? "" : $"cd {Sh.Quote(dir)}\n";

        if (s.PackageManager == "bun")
            return new StackPlan("node", image, [], null, new Dictionary<string, string>(), notes, "bun is not supported yet");

        const string corepack = "corepack enable --install-directory \"$HOME/bin\"\nexport PATH=\"$HOME/bin:$PATH\"\n";
        var install = s.PackageManager switch
        {
            "pnpm" => corepack + (s.Lockfile is null ? "pnpm install" : "pnpm install --frozen-lockfile"),
            "yarn" => corepack + (s.Lockfile is null ? "yarn install" : "yarn install --immutable || yarn install --frozen-lockfile"),
            _ => s.Lockfile is null ? "npm install --no-audit --no-fund" : "npm ci --no-audit --no-fund",
        };

        string? test = s.TestFramework switch
        {
            "jest" => "npx --no-install jest --ci --json --outputFile=/out/jest.json",
            "vitest" => "npx --no-install vitest run --reporter=default --reporter=junit --outputFile.junit=/out/vitest.xml",
            "mocha" => "npx --no-install mocha --reporter xunit --reporter-option output=/out/mocha.xml",
            "node" => "npm test -- --test-reporter=spec --test-reporter-destination=stdout --test-reporter=junit --test-reporter-destination=/out/node.xml",
            _ when s.TestScript is not null => "npm test",
            _ => null,
        };
        if (s.TestFramework is null && s.TestScript is not null)
            notes.Add($"test runner not recognised (\"{s.TestScript}\"): no per-test report");
        if (s.TestScript is null && s.TestFramework is null)
            notes.Add("no test script in package.json");
        if (s.Workspaces)
            notes.Add("workspaces: only the root test command is run");

        return new StackPlan("node", image,
            [new PlanStep("install", cd + install, Network.Open, Install)],
            test is null ? null : cd + test,
            new Dictionary<string, string> { ["COREPACK_ENABLE_NETWORK"] = "0" }, notes);
    }

    internal static int NodeMajor(string? declared)
    {
        if (declared is null) return 24;
        var m = Regex.Match(declared, @"(\d+)");
        if (!m.Success) return 24;
        var v = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        return v switch { <= 20 => 20, <= 22 => 22, _ => 24 };
    }

    // ---------------------------------------------------------------- Python

    internal static StackPlan Python(PythonStack s)
    {
        var notes = new List<string>();
        var version = PythonVersion(s.PythonVersion);
        var image = $"python:{version}-bookworm";
        var extras = s.TestExtras.Count == 0 ? "" : $"[{string.Join(',', s.TestExtras)}]";

        const string venv = "python -m venv \"$HOME/venv\"\n. \"$HOME/venv/bin/activate\"\npython -m pip install --quiet --upgrade pip\n";
        var install = s.PackageManager switch
        {
            "uv" => "pip install --quiet uv\nUV_PROJECT_ENVIRONMENT=\"$HOME/venv\" uv sync --frozen --all-extras",
            "poetry" => "pip install --quiet poetry\npoetry config virtualenvs.create false\npoetry install --no-interaction --all-extras",
            _ => PipInstall(s, extras),
        };
        install += "\npip install --quiet pytest";

        string? test = s.TestFramework is null ? null
            : ". \"$HOME/venv/bin/activate\"\npython -m pytest -q -p no:cacheprovider --junitxml=/out/pytest.xml";
        if (s.TestFramework is null) notes.Add("no pytest or unittest tests found");
        if (s.TestFramework == "unittest") notes.Add("unittest tests run through pytest for the JUnit report");

        return new StackPlan("python", image,
            [new PlanStep("install", venv + install, Network.Open, Install)],
            test, new Dictionary<string, string>(), notes);
    }

    private static string PipInstall(PythonStack s, string extras)
    {
        var lines = new List<string>();
        if (s.Manifests.Any(m => m is "pyproject.toml" or "setup.py" or "setup.cfg")) lines.Add($"pip install --quiet -e \".{extras}\"");
        foreach (var r in s.RequirementsFiles) lines.Add($"pip install --quiet -r {Sh.Quote(r)}");
        return lines.Count == 0 ? "true" : string.Join("\n", lines);
    }

    internal static string PythonVersion(string? declared)
    {
        if (declared is null) return "3.12";
        var exact = Regex.Match(declared, @"^\s*(3\.\d+)");
        if (exact.Success && !declared.Contains('<') && !declared.Contains('>') && !declared.Contains('=')) return exact.Groups[1].Value;
        var upper = Regex.Match(declared, @"<\s*3\.(\d+)");
        if (upper.Success)
        {
            var minor = int.Parse(upper.Groups[1].Value, CultureInfo.InvariantCulture) - 1;
            return $"3.{Math.Clamp(minor, 9, 13)}";
        }
        return "3.12";
    }
}

public static class Sh
{
    /// <summary>Single-quotes a value for bash.</summary>
    public static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";
}
