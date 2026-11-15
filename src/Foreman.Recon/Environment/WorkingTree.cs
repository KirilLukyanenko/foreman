using System.Text.RegularExpressions;

namespace Foreman.Recon.Environment;

/// <summary>
/// Whether an environment tests the repository as it is when the tests run. Later stages change the code and run the
/// same test script without setting up again, so a script that tests a copy made during setup (a non-editable install
/// into site-packages, binaries built once and run with --no-build) would hide every change. The baseline can't
/// catch this: before any change, the copy and the code agree.
/// </summary>
public static partial class WorkingTree
{
    public static IReadOnlyList<string> Problems(string setupScript, string testScript)
    {
        var problems = new List<string>();
        if (NoBuild().IsMatch(testScript))
            problems.Add("the test script runs `dotnet test --no-build`, which tests the binaries built in setup, not the code as it is " +
                         "when the tests run; use `--no-restore` instead, so `dotnet test` builds again (offline, incrementally)");
        foreach (var line in setupScript.Split('\n').Select(l => l.Trim()))
        {
            if (line.StartsWith('#')) continue;
            if (PipInstall().Match(line) is { Success: true } m && !Editable().IsMatch(line)
                && m.Groups["args"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(a => ProjectItself().IsMatch(a)))
                problems.Add($"`{line}` installs a copy of the project into site-packages, so the tests import that copy and not the " +
                             "code as it is when they run; install it editable (`pip install -e .`, extras as `-e \".[tests]\"`)");
            else if (SetupPyInstall().IsMatch(line))
                problems.Add($"`{line}` installs a copy of the project; use an editable install (`pip install -e .`)");
        }
        return problems;
    }

    [GeneratedRegex(@"\bdotnet\s+test\b[^\n]*--no-build\b")]
    private static partial Regex NoBuild();

    [GeneratedRegex(@"\b(?:pip3?|uv\s+pip|python3?\s+-m\s+pip)\s+install\b(?<args>.*)$")]
    private static partial Regex PipInstall();

    [GeneratedRegex(@"(?:^|\s)(?:-e|--editable)(?:\s|=|$)")]
    private static partial Regex Editable();

    /// <summary>The project itself as an install target: `.`, `./`, `.[tests]`, quoted or not.</summary>
    [GeneratedRegex(@"^[""']?\./?(?:\[[^\]]*\])?[""']?$")]
    private static partial Regex ProjectItself();

    [GeneratedRegex(@"\bsetup\.py\s+install\b")]
    private static partial Regex SetupPyInstall();
}
