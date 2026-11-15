using System.Text.RegularExpressions;
using Foreman.Planning.Spec;
using Foreman.Recon.CodeMap;

namespace Foreman.Implement;

/// <param name="Rule">What kind of edit: <c>edited-test</c>, <c>skip-added</c>, <c>lockfile</c>...</param>
/// <param name="Blocks">True: the gate fails. False: allowed, but the change must explain it (the reviewer sees it).</param>
public sealed record GuardFinding(string Rule, string Path, bool Blocks, string Detail, int? Line = null);

/// <summary>What the PRP allows. Touch points count as allowed: the spec said the change goes there.</summary>
public sealed record GuardPolicy(IReadOnlyList<AllowedEdit> Allowed, IReadOnlyList<string> DoNotEdit, IReadOnlyList<TouchPoint> TouchPoints)
{
    public static GuardPolicy Of(Prp prp) => new(prp.Spec.AllowedEdits, prp.DoNotEdit, prp.Spec.TouchPoints);

    public bool IsAllowed(string path) =>
        Allowed.Any(a => Same(a.Path, path)) || TouchPoints.Any(t => Same(t.Path, path));

    internal static bool Same(string a, string b) =>
        string.Equals(a.Trim().TrimStart('/').Replace('\\', '/'), b, StringComparison.Ordinal)
        || (a.EndsWith('/') && b.StartsWith(a.TrimStart('/'), StringComparison.Ordinal));
}

/// <summary>
/// The diff guard: the edits that make a test suite pass without making the code right. An agent that deletes the
/// failing test, adds a skip marker, rewrites an assertion or loosens the test config turns the gate green; linters
/// and SAST rules don't look for that, so these rules do. Protected paths (tests, CI, test config, lockfiles,
/// generated and vendored files, agent instructions) fail the gate unless the PRP allows them with a reason;
/// skip and focus markers fail it everywhere. The rest is allowed but reported, for the reviewer and the PR.
/// </summary>
public static partial class DiffGuard
{
    public static IReadOnlyList<GuardFinding> Check(IReadOnlyList<FileChange> changes, GuardPolicy policy)
    {
        var findings = new List<GuardFinding>();
        foreach (var c in changes)
        {
            var allowed = policy.IsAllowed(c.Path);
            var name = c.Path.Split('/')[^1];
            var test = CodePaths.IsTest(c.Path);

            if (c.Path.StartsWith(".git/", StringComparison.Ordinal))
                findings.Add(new("git-internals", c.Path, true, "edits git's own files"));
            else if (IsAgentConfig(c.Path))
                findings.Add(new("agent-config", c.Path, !allowed, "agent instructions or settings: an implementer must not leave instructions for the next agent"));
            else if (IsCi(c.Path))
                findings.Add(new("ci-config", c.Path, !allowed, "CI configuration decides what the client's checks run"));
            else if (IsTestConfig(c.Path, name))
                findings.Add(new("test-config", c.Path, !allowed, "test configuration can hide or skip tests"));
            else if (Lockfiles.Contains(name))
                findings.Add(new("lockfile", c.Path, !allowed, "dependency versions changed; needs a reason and a vulnerability check"));
            else if (policy.DoNotEdit.Any(d => GuardPolicy.Same(d, c.Path)))
                findings.Add(new("do-not-edit", c.Path, !allowed, "generated or vendored: change the source it comes from instead"));
            else if (test && c.Kind == ChangeKind.Deleted)
                findings.Add(new("deleted-test", c.Path, !allowed, "a test file was deleted"));
            else if (test && c.Kind == ChangeKind.Modified)
            {
                if (!allowed) findings.Add(new("edited-test", c.Path, true, "an existing test file was edited and the PRP doesn't list it (allowed_edits or where the change goes)"));
                var assertions = c.Removed.Where(l => Assertion().IsMatch(l)).ToList();
                if (assertions.Count > 0)
                    findings.Add(new("changed-assertion", c.Path, false,
                        $"{assertions.Count} existing assertion line(s) removed or rewritten, e.g. `{Short(assertions[0])}`: the reviewer checks the PRP asks for that"));
            }
            else if (Manifests.Contains(name) || name.EndsWith(".csproj", StringComparison.Ordinal) || RequirementsFile().IsMatch(name))
                findings.Add(new("manifest", c.Path, false, "project or dependency manifest changed: say why in the PR; dependencies need a vulnerability check"));
            else if (!test && !allowed)
                findings.Add(new("outside-touch-points", c.Path, false, c.Kind == ChangeKind.Added ? "a new file the PRP doesn't name" : "a file the PRP doesn't name"));

            if (c.Binary) findings.Add(new("binary", c.Path, false, "binary content: the reviewer can't read it"));

            if (test || name.StartsWith("conftest", StringComparison.Ordinal))
                foreach (var line in c.Added.Where(l => SkipMarker().IsMatch(l.Text)))
                    findings.Add(new("skip-added", c.Path, true, $"adds a skip or focus marker: `{Short(line.Text)}`", line.Line));
        }
        return findings;
    }

    private static string Short(string s) { s = s.Trim(); return s.Length > 100 ? s[..100] + "…" : s; }

    private static bool IsAgentConfig(string path) =>
        path.StartsWith(".claude/", StringComparison.Ordinal) || path.StartsWith(".cursor/", StringComparison.Ordinal)
        || path is "CLAUDE.md" or "AGENTS.md" or ".mcp.json" or ".cursorrules" or ".github/copilot-instructions.md"
        || path.EndsWith("/CLAUDE.md", StringComparison.Ordinal) || path.EndsWith("/AGENTS.md", StringComparison.Ordinal);

    private static bool IsCi(string path) =>
        path.StartsWith(".github/workflows/", StringComparison.Ordinal) || path.StartsWith(".github/actions/", StringComparison.Ordinal)
        || path.StartsWith(".circleci/", StringComparison.Ordinal) || path.StartsWith(".buildkite/", StringComparison.Ordinal)
        || path is ".gitlab-ci.yml" or "azure-pipelines.yml" or ".travis.yml" or "Jenkinsfile" or "appveyor.yml" or ".appveyor.yml";

    private static bool IsTestConfig(string path, string name) =>
        name is "conftest.py" or "pytest.ini" or "tox.ini" or "noxfile.py" or ".coveragerc" or "xunit.runner.json" or ".mocharc.json" or ".mocharc.js" or ".mocharc.yml" or ".nycrc"
        || name.EndsWith(".runsettings", StringComparison.Ordinal)
        || TestConfigName().IsMatch(name);

    private static readonly HashSet<string> Lockfiles = new(StringComparer.Ordinal)
    {
        "package-lock.json", "npm-shrinkwrap.json", "yarn.lock", "pnpm-lock.yaml", "bun.lock", "bun.lockb", "deno.lock",
        "poetry.lock", "uv.lock", "Pipfile.lock", "pdm.lock", "packages.lock.json", "Cargo.lock", "go.sum", "Gemfile.lock", "composer.lock",
    };

    private static readonly HashSet<string> Manifests = new(StringComparer.Ordinal)
    {
        "package.json", "pyproject.toml", "setup.py", "setup.cfg", "Pipfile", "Directory.Packages.props", "Directory.Build.props",
        "Directory.Build.targets", "global.json", "go.mod", "Cargo.toml", "Gemfile", "composer.json", "tsconfig.json",
    };

    [GeneratedRegex(@"^(jest|vitest|vitest\.workspace|karma\.conf|playwright\.config|cypress\.config|ava\.config|babel\.config)\.[cm]?[jt]s(on)?$")]
    private static partial Regex TestConfigName();

    [GeneratedRegex(@"^requirements[\w.-]*\.(txt|in)$")]
    private static partial Regex RequirementsFile();

    /// <summary>Skip and focus markers of pytest, unittest, Jest/Vitest/Mocha, xUnit/NUnit/MSTest, JUnit and Go.</summary>
    [GeneratedRegex(@"@pytest\.mark\.(skip|skipif|xfail)\b|\bpytest\.(skip|xfail|importorskip)\(|@unittest\.(skip|skipIf|skipUnless|expectedFailure)\b|\b(it|test|describe|suite|context)\.(skip|only|todo|fails)\b|\b(xit|xtest|xdescribe|fit|fdescribe|ftest)\s*\(|\[\s*(Fact|Theory|Test|TestMethod|TestCase)\s*\([^\]]*\bSkip\s*=|\[\s*(Ignore|Skip|Explicit)\b|\bAssert\.(Skip|Ignore|Inconclusive)\b|\bSkip\.(If|Unless)\(|\bt\.Skip(Now|f)?\(|@(Disabled|Ignore)\b")]
    private static partial Regex SkipMarker();

    [GeneratedRegex(@"\bassert\b|\bAssert\.|\bexpect\s*\(|\.should\b|\bShould\.|\.toBe|\.toEqual|\.toThrow|\bassert(Equal|Equals|True|False|Raises|In|Is)\w*\s*\(|\bself\.assert|\bpytest\.raises\b|\bt\.(Error|Fatal|Fail)")]
    private static partial Regex Assertion();
}
