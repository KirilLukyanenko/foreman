using Foreman.Recon.Security;

namespace Foreman.Tests;

public class SecurityTests
{
    [Fact]
    public void Ecosystems_come_from_manifests_and_lockfiles()
    {
        var found = SecurityStage.Ecosystems([
            "src/App/App.csproj", "web/package.json", "web/pnpm-lock.yaml", "web/node_modules/x/package.json",
            "requirements-dev.txt", "go.mod", "README.md", "vendor/github.com/x/go.mod",
        ]);
        Assert.Equal(["Go", "NuGet", "PyPI", "npm"], found);
    }

    [Fact]
    public void Gitleaks_findings_keep_only_what_locates_them()
    {
        const string json = """
            [{"Description":"Generic API Key","StartLine":12,"EndLine":12,"Match":"key = REDACTED","Secret":"REDACTED",
              "File":"src/config.ts","Commit":"abc123def456","Author":"Someone","Email":"someone@example.invalid",
              "Date":"2025-01-02T03:04:05Z","RuleID":"generic-api-key","Fingerprint":"abc:src/config.ts:generic-api-key:12"},
             {"Description":"JWT","StartLine":3,"File":"tests/auth.test.ts","Commit":"","RuleID":"jwt"}]
            """;
        var found = SecurityStage.Gitleaks(json);
        Assert.Equal(2, found.Count);
        var key = found[0];
        Assert.Equal(("generic-api-key", "src/config.ts", 12, "abc123def456"), (key.Rule, key.File, key.Line, key.Commit));
        Assert.False(key.InTestPath);
        Assert.True(found[1].InTestPath);
        Assert.Null(found[1].Commit);
        // Nothing that could carry the secret or a person's details is kept.
        var serialized = Foreman.Core.Json.Write(found);
        Assert.DoesNotContain("REDACTED", serialized);
        Assert.DoesNotContain("someone", serialized);
    }

    [Fact]
    public void A_secret_still_in_the_files_is_not_also_listed_as_history()
    {
        var dir = Directory.CreateTempSubdirectory("foreman-sec-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "gitleaks-files.json"), """[{"RuleID":"jwt","File":"a.ts","StartLine":4}]""");
            File.WriteAllText(Path.Combine(dir, "gitleaks-history.json"), """[{"RuleID":"jwt","File":"a.ts","StartLine":2,"Commit":"c1"},{"RuleID":"aws-access-token","File":"old.env","StartLine":1,"Commit":"c0"}]""");
            File.WriteAllText(Path.Combine(dir, "gitleaks-files.run"), "0 1.2");
            File.WriteAllText(Path.Combine(dir, "gitleaks-history.run"), "0 3.4");
            File.WriteAllText(Path.Combine(dir, "osv.run"), "128 0.5");
            File.WriteAllText(Path.Combine(dir, "opengrep.run"), "7 2.0");
            File.WriteAllText(Path.Combine(dir, "removed-ignore-files.txt"), ".gitleaksignore\n");
            var report = SecurityStage.Read(dir, "c2", [], new Dictionary<string, string>(), 9);

            Assert.Equal(2, report.Secrets.Count);
            Assert.Equal(1, report.SecretsInFiles);
            Assert.Equal("old.env", report.Secrets.Single(s => !s.InCurrentFiles).File);
            Assert.Equal([".gitleaksignore"], report.RemovedIgnoreFiles);
            Assert.Null(report.Tools.Single(t => t.Tool == "osv").Error); // 128: nothing to scan
            Assert.NotNull(report.Tools.Single(t => t.Tool == "opengrep").Error);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Osv_groups_become_one_finding_with_their_fixed_versions()
    {
        const string json = """
            {"results":[{"source":{"path":"/work/repo/poetry.lock","type":"lockfile"},"packages":[{
              "package":{"name":"requests","version":"2.19.0","ecosystem":"PyPI"},
              "vulnerabilities":[{"id":"PYSEC-2018-28","summary":"Credentials leak on redirect",
                "affected":[{"package":{"name":"requests","ecosystem":"PyPI"},"ranges":[{"type":"ECOSYSTEM","events":[{"introduced":"0"},{"fixed":"2.20.0"}]}]}]}],
              "groups":[{"ids":["PYSEC-2018-28","GHSA-x84v-xcm2-53pg"],"aliases":["CVE-2018-18074"],"max_severity":"7.5"}]}]}]}
            """;
        var v = SecurityStage.Osv(json).Single();
        Assert.Equal(("PyPI", "requests", "2.19.0", "poetry.lock"), (v.Ecosystem, v.Package, v.Version, v.Source));
        Assert.Equal(["PYSEC-2018-28", "GHSA-x84v-xcm2-53pg", "CVE-2018-18074"], v.Ids);
        Assert.Equal(7.5, v.Severity);
        Assert.Equal(["2.20.0"], v.Fixed);
        Assert.Equal("Credentials leak on redirect", v.Summary);
    }

    [Fact]
    public void Opengrep_results_sort_by_severity_and_keep_the_rule_id()
    {
        const string json = """
            {"results":[
              {"check_id":"opt.rules.python.assert.python_assert_rule-assert-used","path":"./src/a.py","start":{"line":3},"extra":{"message":"Assert used.","severity":"INFO","metadata":{"cwe":"CWE-703"}}},
              {"check_id":"opt.rules.python.exec.python_exec_rule-subprocess-popen-shell-true","path":"src/b.py","start":{"line":9},"extra":{"message":"shell=True. Avoid it.","severity":"ERROR","metadata":{"cwe":["CWE-78: OS Command Injection"]}}}],
             "errors":[{"message":"Syntax error at line src/c.py:1:\n details"},{"message":"Syntax error at line src/c.py:1:\n details"}],
             "paths":{"scanned":["src/a.py","src/b.py","src/c.py"]}}
            """;
        var found = SecurityStage.Opengrep(json);
        Assert.Equal("python.exec.python_exec_rule-subprocess-popen-shell-true", found[0].Rule);
        Assert.Equal(("src/b.py", 9), (found[0].File, found[0].Line));
        Assert.Equal(["CWE-78: OS Command Injection"], found[0].Cwe);
        Assert.Equal(["CWE-703"], found[1].Cwe);

        var (warnings, scanned) = SecurityStage.OpengrepMeta(json);
        Assert.Equal(3, scanned);
        Assert.Equal(["opengrep: Syntax error at line src/c.py:1: (2 times)"], warnings);
    }

    [Fact]
    public void Rules_file_lines_only_when_there_is_something_to_know()
    {
        SecurityReport Report(IReadOnlyList<SecretFinding> secrets, IReadOnlyList<Vulnerability> vulns, IReadOnlyList<SastFinding> sast) =>
            new("c", secrets, vulns, sast, [], new Dictionary<string, string>(), [], 10, new Dictionary<string, string>(), [], [], 0, [], 1);

        Assert.Empty(SecuritySummary.ForRules(Report([], [], [new SastFinding("r", "INFO", "a.py", 1, "m", [], false)])));

        var lines = SecuritySummary.ForRules(Report(
            [new SecretFinding("jwt", "", "tests/a.ts", 3, null, null, true, true), new SecretFinding("aws", "", "deploy/.env", 1, null, null, true, false)],
            [new Vulnerability("npm", "lodash", "4.17.0", "package-lock.json", ["GHSA-1"], 9.1, ["4.17.21"], null)],
            []));
        Assert.Contains(lines, l => l.Contains("1 outside tests (`deploy/.env:1`)"));
        Assert.Contains(lines, l => l.StartsWith("Dependencies with known vulnerabilities: 1"));

        var reader = SecuritySummary.ForReader(Report([], [new Vulnerability("npm", "lodash", "4.17.0", "package-lock.json", ["GHSA-1"], 9.1, ["4.17.21"], null)], []));
        Assert.Contains("lodash 4.17.0 (npm, `package-lock.json`): 1 advisories, max CVSS 9.1, fixed in 4.17.21", reader);
    }

    [Theory]
    [InlineData("/work/repo/src/a.cs", "src/a.cs")]
    [InlineData("./src/a.cs", "src/a.cs")]
    [InlineData("src/a.cs", "src/a.cs")]
    public void Scanner_paths_become_repository_paths(string path, string expected) => Assert.Equal(expected, SecurityStage.Clean(path));
}

public class VulnerabilityFlagsTests
{
    [Theory]
    [InlineData("4.17.21", "package-lock.json", true, false)]
    [InlineData("[8.14.0, )", "src/MediatR/MediatR.csproj", false, false)]
    [InlineData("^1.2.0", "package.json", false, false)]
    [InlineData("1.0.0", "benchmarks/jsx/yarn.lock", true, true)]
    [InlineData("1.0.0", "src/App.Tests.Benchmarks/packages.lock.json", true, true)]
    public void Pinned_and_dev_only(string version, string source, bool pinned, bool devOnly)
    {
        var v = new Vulnerability("npm", "x", version, source, [], null, [], null);
        Assert.Equal((pinned, devOnly), (v.Pinned, v.DevOnly));
    }
}
