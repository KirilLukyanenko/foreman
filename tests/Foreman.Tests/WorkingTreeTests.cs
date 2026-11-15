using Foreman.Recon.Environment;

namespace Foreman.Tests;

public class WorkingTreeTests
{
    [Theory]
    [InlineData("pip install .")]
    [InlineData("pip install --quiet .")]
    [InlineData("python -m pip install --quiet \".[tests]\"")]
    [InlineData("uv pip install ./")]
    [InlineData("python setup.py install")]
    public void A_copy_of_the_project_in_site_packages_is_refused(string line)
    {
        var problems = WorkingTree.Problems($"set -e\npython -m venv /opt/venv\n{line}", "pytest");
        Assert.Single(problems);
    }

    [Theory]
    [InlineData("pip install -e .")]
    [InlineData("pip install --quiet -e \".[tests]\"")]
    [InlineData("pip install --editable=.")]
    [InlineData("pip install -r requirements/tests.txt")]
    [InlineData("pip install pytest .venv-tools")]
    [InlineData("# pip install .")]
    public void Editable_installs_and_dependencies_pass(string line) =>
        Assert.Empty(WorkingTree.Problems(line, "pytest"));

    [Fact]
    public void Dotnet_test_must_build()
    {
        Assert.Single(WorkingTree.Problems("dotnet restore", "dotnet test App.sln --no-build --logger trx"));
        Assert.Empty(WorkingTree.Problems("dotnet restore\ndotnet build --no-restore", "dotnet test App.sln --no-restore --logger trx"));
    }
}
