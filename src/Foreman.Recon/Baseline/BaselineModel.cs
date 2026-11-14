using Foreman.Recon.Reports;

namespace Foreman.Recon.Baseline;

/// <summary>What a test did across all baseline runs.</summary>
public enum Verdict
{
    /// <summary>Passed every run: part of the pass-to-pass gate.</summary>
    Pass,

    /// <summary>Failed every run before we changed anything: the implementer is not blamed for it.</summary>
    KnownRed,

    /// <summary>Different results between runs, or missing from some: excluded from the gate.</summary>
    Flaky,

    /// <summary>Skipped every run.</summary>
    Skipped,
}

public sealed record TestBaseline(string Id, Verdict Verdict, IReadOnlyList<string> Runs, double? MedianSeconds);

public sealed record VerdictCounts(int Total, int Pass, int KnownRed, int Flaky, int Skipped);

public static class BaselineMath
{
    /// <summary>Combines the outcomes of N runs into one verdict per test.</summary>
    public static IReadOnlyList<TestBaseline> Combine(IReadOnlyList<IReadOnlyList<TestOutcome>> runs)
    {
        var ids = runs.SelectMany(r => r.Select(o => o.Id)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        var byRun = runs.Select(r => r.ToDictionary(o => o.Id, StringComparer.Ordinal)).ToList();
        var result = new List<TestBaseline>();
        foreach (var id in ids)
        {
            var seen = byRun.Select(r => r.GetValueOrDefault(id)).ToList();
            var labels = seen.Select(o => o is null ? "missing" : o.Status.ToString().ToLowerInvariant()).ToList();
            var verdict =
                seen.Any(o => o is null) ? Verdict.Flaky :
                seen.All(o => o!.Status == TestStatus.Passed) ? Verdict.Pass :
                seen.All(o => o!.Status == TestStatus.Failed) ? Verdict.KnownRed :
                seen.All(o => o!.Status == TestStatus.Skipped) ? Verdict.Skipped :
                Verdict.Flaky;
            var times = seen.Where(o => o?.Seconds is not null).Select(o => o!.Seconds!.Value).Order().ToList();
            double? median = times.Count == 0 ? null : Math.Round(times[times.Count / 2], 3);
            result.Add(new TestBaseline(id, verdict, labels, median));
        }
        return result;
    }

    public static VerdictCounts Count(IReadOnlyList<TestBaseline> tests) => new(
        tests.Count,
        tests.Count(t => t.Verdict == Verdict.Pass),
        tests.Count(t => t.Verdict == Verdict.KnownRed),
        tests.Count(t => t.Verdict == Verdict.Flaky),
        tests.Count(t => t.Verdict == Verdict.Skipped));
}

public sealed record StepResult(string Name, string Network, int ExitCode, double Seconds, bool TimedOut, string Log);

public enum StackStatus
{
    /// <summary>Per-test results from every run.</summary>
    Ok,

    /// <summary>Install or build failed: the environment agent has to take over.</summary>
    SetupFailed,

    /// <summary>The test command ran but produced no per-test report we can read.</summary>
    NoPerTestResults,

    /// <summary>The project has no tests we found.</summary>
    NoTests,

    /// <summary>The stack is detected but not handled deterministically.</summary>
    Unsupported,
}

public sealed record StackBaseline(
    string Stack,
    string Image,
    string? ImageDigest,
    StackStatus Status,
    string? Reason,
    IReadOnlyList<string> Notes,
    IReadOnlyList<StepResult> Steps,
    string? TestCommand,
    IReadOnlyList<StepResult> TestRuns,
    VerdictCounts? Counts,
    string? TestsFile);

public sealed record BaselineReport(int Runs, IReadOnlyList<StackBaseline> Stacks);
