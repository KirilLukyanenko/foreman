using Foreman.Recon.Triage;

namespace Foreman.Tests;

public class StackDetectorTests
{
    [Fact]
    public void Dotnet_finds_solution_test_projects_frameworks_and_runner()
    {
        using var repo = new TempRepo(
            ("App.slnx", "<Solution />"),
            ("global.json", """{ "sdk": { "version": "9.0.100" }, "test": { "runner": "Microsoft.Testing.Platform" } }"""),
            ("src/App/App.csproj", "<Project><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>"),
            ("tests/App.Tests/App.Tests.csproj", """<Project><PropertyGroup><TargetFrameworks>net8.0;net9.0</TargetFrameworks></PropertyGroup><ItemGroup><PackageReference Include="xunit.v3" /></ItemGroup></Project>"""),
            ("src/App/bin/Debug/Other.csproj", "ignored: inside bin"));

        var s = StackDetector.Dotnet(RepoFiles.Scan(repo.Root))!;

        Assert.Equal(["App.slnx"], s.Solutions);
        Assert.Equal(["tests/App.Tests/App.Tests.csproj"], s.TestProjects);
        Assert.Equal(["xunit.v3"], s.TestFrameworks);
        Assert.Equal(["net8.0", "net9.0"], s.TargetFrameworks);
        Assert.Equal("9.0.100", s.SdkVersion);
        Assert.Equal("mtp", s.TestRunner);
        Assert.Equal(2, s.Projects.Count);
    }

    [Theory]
    [InlineData("""<PackageReference Include="NUnit" Version="4.2.0" />""", "nunit")]
    [InlineData("""<PackageReference Include="MSTest.TestFramework" />""", "mstest")]
    [InlineData("""<PackageReference Include="xunit" Version="2.9.0" />""", "xunit")]
    [InlineData("""<PackageReference Include="TUnit" />""", "tunit")]
    public void Dotnet_test_framework_names(string reference, string expected) =>
        Assert.Contains(expected, StackDetector.TestFrameworksIn(reference));

    [Fact]
    public void Node_reads_manager_from_lockfile_and_runner_from_dependencies()
    {
        using var repo = new TempRepo(
            ("package.json", """{ "scripts": { "test": "vitest run" }, "devDependencies": { "vitest": "^3" }, "engines": { "node": ">=20" } }"""),
            ("pnpm-lock.yaml", "lockfileVersion: '9.0'"));

        var s = StackDetector.Node(RepoFiles.Scan(repo.Root))!;

        Assert.Equal("pnpm", s.PackageManager);
        Assert.Equal("pnpm-lock.yaml", s.Lockfile);
        Assert.Equal("vitest", s.TestFramework);
        Assert.Equal(">=20", s.NodeVersion);
        Assert.False(s.Workspaces);
    }

    [Fact]
    public void Node_packageManager_field_wins_over_lockfile()
    {
        using var repo = new TempRepo(
            ("package.json", """{ "packageManager": "yarn@4.5.0", "scripts": { "test": "jest" } }"""),
            ("package-lock.json", "{}"));

        var s = StackDetector.Node(RepoFiles.Scan(repo.Root))!;

        Assert.Equal("yarn", s.PackageManager);
        Assert.Equal("jest", s.TestFramework);
    }

    [Fact]
    public void Python_uv_project_with_test_extras()
    {
        using var repo = new TempRepo(
            ("pyproject.toml", """
                [project]
                name = "lib"
                requires-python = ">=3.10"

                [project.optional-dependencies]
                tests = ["pytest>=8"]
                docs = ["sphinx"]

                [tool.pytest.ini_options]
                addopts = "-q"
                """),
            ("uv.lock", ""),
            ("src/lib/__init__.py", ""),
            ("tests/test_lib.py", "def test_x(): pass"));

        var s = StackDetector.Python(RepoFiles.Scan(repo.Root))!;

        Assert.Equal("uv", s.PackageManager);
        Assert.Equal(">=3.10", s.PythonVersion);
        Assert.Equal("pytest", s.TestFramework);
        Assert.Equal(["tests"], s.TestExtras);
        Assert.Empty(s.TestGroups);
    }

    [Fact]
    public void Python_needs_a_manifest_and_python_files()
    {
        using var repo = new TempRepo(("scripts/tool.py", "print(1)"));
        Assert.Null(StackDetector.Python(RepoFiles.Scan(repo.Root)));
    }

    [Fact]
    public void Unsupported_stacks_are_reported_not_ignored()
    {
        using var repo = new TempRepo(("go.mod", "module x"), ("Cargo.toml", "[package]"));
        Assert.Equal(["go", "rust"], StackDetector.UnsupportedStacks(RepoFiles.Scan(repo.Root)));
    }
}

public class PythonGroupsTests
{
    [Fact]
    public void Dependency_groups_are_separate_from_extras_and_reach_uv_sync()
    {
        using var repo = new TempRepo(
            ("pyproject.toml", """
                [project]
                name = "lib"

                [dependency-groups]
                dev = [
                    "ruff",
                ]
                docs = ["sphinx"]
                tests = [
                    "pytest",
                ]
                """),
            ("uv.lock", ""),
            ("tests/test_lib.py", ""));

        var s = Foreman.Recon.Triage.StackDetector.Python(Foreman.Recon.Triage.RepoFiles.Scan(repo.Root))!;
        Assert.Empty(s.TestExtras);
        Assert.Equal(["dev", "tests"], s.TestGroups);

        var plan = Foreman.Recon.Baseline.BaselinePlanner.Python(s);
        Assert.Contains("uv sync --frozen --all-extras --group 'dev' --group 'tests'", plan.Setup[0].Script);
        Assert.Contains("--junitxml=\"$OUT/pytest.xml\"", plan.TestScript);
    }
}
