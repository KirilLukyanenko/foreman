using Foreman.Recon.Baseline;
using Foreman.Recon.Reports;
using Foreman.Recon.Triage;

namespace Foreman.Tests;

public class TestReportsTests
{
    [Fact]
    public void Trx_results_are_keyed_by_assembly_and_name()
    {
        const string trx = """
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results>
                <UnitTestResult testId="a" testName="Lib.Tests.MathTests.Adds" outcome="Passed" duration="00:00:00.0120000" />
                <UnitTestResult testId="b" testName="Lib.Tests.MathTests.Divides" outcome="Failed" duration="00:00:01.5000000" />
                <UnitTestResult testId="c" testName="Lib.Tests.MathTests.Later" outcome="NotExecuted" />
              </Results>
              <TestDefinitions>
                <UnitTest id="a" storage="/work/repo/tests/bin/Lib.Tests.dll" />
                <UnitTest id="b" storage="/work/repo/tests/bin/Lib.Tests.dll" />
                <UnitTest id="c" storage="/work/repo/tests/bin/Lib.Tests.dll" />
              </TestDefinitions>
            </TestRun>
            """;

        var r = TestReports.ParseTrx(trx).ToList();

        Assert.Equal(new TestOutcome("Lib.Tests::Lib.Tests.MathTests.Adds", TestStatus.Passed, 0.012), r[0]);
        Assert.Equal(TestStatus.Failed, r[1].Status);
        Assert.Equal(TestStatus.Skipped, r[2].Status);
    }

    [Fact]
    public void Junit_failure_error_and_skipped()
    {
        const string xml = """
            <testsuites>
              <testsuite name="pytest">
                <testcase classname="tests.test_a" name="test_ok" time="0.01" />
                <testcase classname="tests.test_a" name="test_bad" time="0.02"><failure message="x" /></testcase>
                <testcase classname="tests.test_a" name="test_crash"><error message="y" /></testcase>
                <testcase classname="tests.test_a" name="test_later"><skipped /></testcase>
              </testsuite>
            </testsuites>
            """;

        var r = TestReports.ParseJUnit(xml).ToList();

        Assert.Equal(["Passed", "Failed", "Failed", "Skipped"], r.Select(o => o.Status.ToString()));
        Assert.Equal("tests.test_a::test_ok", r[0].Id);
    }

    [Fact]
    public void Junit_rejects_dtd()
    {
        const string xml = """<?xml version="1.0"?><!DOCTYPE x [<!ENTITY e SYSTEM "file:///etc/passwd">]><testsuite><testcase name="&e;" /></testsuite>""";
        Assert.ThrowsAny<System.Xml.XmlException>(() => TestReports.ParseJUnit(xml).ToList());
    }

    [Fact]
    public void Jest_json_paths_are_made_relative()
    {
        const string json = """
            { "testResults": [ { "name": "/work/repo/src/sum.test.js", "assertionResults": [
                { "fullName": "sum adds", "status": "passed", "duration": 4 },
                { "fullName": "sum todo", "status": "todo" } ] } ] }
            """;

        var r = TestReports.ParseJestJson(json, "/work/repo").ToList();

        Assert.Equal(new TestOutcome("src/sum.test.js::sum adds", TestStatus.Passed, 0.004), r[0]);
        Assert.Equal(TestStatus.Skipped, r[1].Status);
    }

    [Fact]
    public void Run_time_values_in_names_become_placeholders()
    {
        Assert.Equal(
            "T.Returns(input: <time>, name: \"now\")",
            TestReports.NormalizeId("T.Returns(input: 2026-11-14T15:16:21.5824354+00:00, name: \"now\")"));
        Assert.Equal("T.Id(<guid>)", TestReports.NormalizeId("T.Id(40da261b-c364-4351-a704-f0c666553311)"));
        Assert.Equal("T.At(<time>)", TestReports.NormalizeId("T.At(11/14/2026 3:16:21 PM)"));

        var run1 = TestReports.Deduplicate([new("T(2026-11-14T15:16:21Z)", TestStatus.Passed, null), new("T(2026-11-14T15:16:22Z)", TestStatus.Passed, null)]);
        var run2 = TestReports.Deduplicate([new("T(2026-11-14T15:17:01Z)", TestStatus.Passed, null), new("T(2026-11-14T15:17:02Z)", TestStatus.Passed, null)]);
        Assert.Equal(run1.Select(o => o.Id), run2.Select(o => o.Id));
    }

    [Fact]
    public void Repeated_names_get_a_suffix()
    {
        var r = TestReports.Deduplicate([new("t", TestStatus.Passed, null), new("t", TestStatus.Failed, null), new("u", TestStatus.Passed, null)]);
        Assert.Equal(["t", "t#2", "u"], r.Select(o => o.Id));
    }
}

public class BaselineMathTests
{
    private static List<TestOutcome> Run(params (string Id, TestStatus Status)[] tests) => tests.Select(t => new TestOutcome(t.Id, t.Status, 1)).ToList();

    [Fact]
    public void Verdicts_across_runs()
    {
        var runs = new[]
        {
            Run(("a", TestStatus.Passed), ("b", TestStatus.Failed), ("c", TestStatus.Passed), ("d", TestStatus.Skipped), ("e", TestStatus.Passed)),
            Run(("a", TestStatus.Passed), ("b", TestStatus.Failed), ("c", TestStatus.Failed), ("d", TestStatus.Skipped), ("e", TestStatus.Passed)),
            Run(("a", TestStatus.Passed), ("b", TestStatus.Failed), ("c", TestStatus.Passed), ("d", TestStatus.Skipped)),
        };

        var v = BaselineMath.Combine(runs).ToDictionary(t => t.Id, t => t.Verdict);

        Assert.Equal(Verdict.Pass, v["a"]);
        Assert.Equal(Verdict.KnownRed, v["b"]);
        Assert.Equal(Verdict.Flaky, v["c"]);
        Assert.Equal(Verdict.Skipped, v["d"]);
        Assert.Equal(Verdict.Flaky, v["e"]); // missing from one run
        Assert.Equal(new VerdictCounts(5, 1, 1, 2, 1), BaselineMath.Count(BaselineMath.Combine(runs)));
    }
}

public class BaselinePlannerTests
{
    [Theory]
    [InlineData(null, 24)]
    [InlineData(">=18", 20)]
    [InlineData("22.11.0", 22)]
    [InlineData("v23", 24)]
    [InlineData("lts/*", 24)]
    public void Node_image_major(string? declared, int expected) => Assert.Equal(expected, BaselinePlanner.NodeMajor(declared));

    [Theory]
    [InlineData(null, "3.12")]
    [InlineData(">=3.9", "3.12")]
    [InlineData(">=3.8,<3.12", "3.11")]
    [InlineData("3.11", "3.11")]
    public void Python_image_version(string? declared, string expected) => Assert.Equal(expected, BaselinePlanner.PythonVersion(declared));

    [Fact]
    public void Dotnet_channel_from_global_json_or_highest_target_framework()
    {
        var notes = new List<string>();
        DotnetStack Stack(string? sdk, params string[] tfms) => new([], [], [], tfms, sdk, "vstest", [], false);

        Assert.Equal("9.0", BaselinePlanner.DotnetChannel(Stack("9.0.301", "net10.0"), notes));
        Assert.Equal("10.0", BaselinePlanner.DotnetChannel(Stack(null, "net8.0", "net10.0", "netstandard2.0"), notes));
        Assert.Equal("8.0", BaselinePlanner.DotnetChannel(Stack(null, "net6.0"), notes));
        Assert.Equal("10.0", BaselinePlanner.DotnetChannel(Stack(null), notes));
    }

    [Fact]
    public void Dotnet_plan_builds_offline_and_writes_trx()
    {
        var plan = BaselinePlanner.Dotnet(new DotnetStack(["My App.sln"], ["src/A.csproj", "tests/A.Tests.csproj"], ["tests/A.Tests.csproj"], ["net10.0"], null, "vstest", ["xunit"], false));

        Assert.Equal("mcr.microsoft.com/dotnet/sdk:10.0", plan.Image);
        Assert.Equal(["restore", "build"], plan.Setup.Select(s => s.Name));
        Assert.Equal(Foreman.Sandbox.Network.None, plan.Setup[1].Network);
        Assert.Contains("dotnet test 'My App.sln' --no-build --logger \"trx", plan.TestScript);
    }

    [Fact]
    public void Shell_quoting() => Assert.Equal("'it'\\''s'", Sh.Quote("it's"));
}
