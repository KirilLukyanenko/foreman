using Foreman.Recon.Baseline;
using Foreman.Recon.Reports;

namespace Foreman.Implement;

/// <summary>A test whose result changed against the baseline, and what it is now.</summary>
public sealed record TestChange(string Id, string Now);

/// <summary>
/// One test run compared with the baseline. <see cref="Regressions"/> is the pass-to-pass gate: a test that passed in
/// every baseline run and now fails, is skipped or is gone (a renamed or deleted test hides a failure as well as a
/// skip does). Flaky tests are out of the gate. New tests are the ones the baseline never saw.
/// </summary>
public sealed record TestDelta(
    int Ran,
    int Passed,
    int Failed,
    int Skipped,
    IReadOnlyList<TestChange> Regressions,
    IReadOnlyList<string> NewPassing,
    IReadOnlyList<string> NewFailing,
    IReadOnlyList<string> NewSkipped,
    IReadOnlyList<string> FixedKnownRed,
    int KnownRedStillFailing)
{
    public static TestDelta Compare(IReadOnlyList<TestBaseline> baseline, IReadOnlyList<TestOutcome> now)
    {
        var before = baseline.ToDictionary(t => t.Id, StringComparer.Ordinal);
        var after = now.ToDictionary(t => t.Id, StringComparer.Ordinal);
        var regressions = baseline.Where(b => b.Verdict == Verdict.Pass)
            .Select(b => after.TryGetValue(b.Id, out var o)
                ? o.Status == TestStatus.Passed ? null : new TestChange(b.Id, o.Status.ToString().ToLowerInvariant())
                : new TestChange(b.Id, "missing"))
            .OfType<TestChange>().ToList();
        var fresh = now.Where(o => !before.ContainsKey(o.Id)).ToList();
        var red = baseline.Where(b => b.Verdict == Verdict.KnownRed).ToList();
        return new TestDelta(
            now.Count,
            now.Count(o => o.Status == TestStatus.Passed),
            now.Count(o => o.Status == TestStatus.Failed),
            now.Count(o => o.Status == TestStatus.Skipped),
            regressions,
            fresh.Where(o => o.Status == TestStatus.Passed).Select(o => o.Id).ToList(),
            fresh.Where(o => o.Status == TestStatus.Failed).Select(o => o.Id).ToList(),
            fresh.Where(o => o.Status == TestStatus.Skipped).Select(o => o.Id).ToList(),
            red.Where(b => after.TryGetValue(b.Id, out var o) && o.Status == TestStatus.Passed).Select(b => b.Id).ToList(),
            red.Count(b => !after.TryGetValue(b.Id, out var o) || o.Status != TestStatus.Passed));
    }

    /// <summary>Keeps only regressions that also failed in a second run: one failure of a test that passed three
    /// times before can be a flake the baseline didn't catch.</summary>
    public TestDelta Confirmed(TestDelta again)
    {
        var second = again.Regressions.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        return this with { Regressions = Regressions.Where(r => second.Contains(r.Id)).ToList() };
    }

    /// <summary>A few lines for an agent or a report.</summary>
    public string Summary(int max = 15)
    {
        var lines = new List<string>
        {
            $"{Ran} tests ran: {Passed} passed, {Failed} failed, {Skipped} skipped.",
            Regressions.Count == 0 ? "No regressions: every test that passed before the change still passes." :
                $"REGRESSIONS ({Regressions.Count}), tests that passed before the change and don't now:",
        };
        lines.AddRange(Regressions.Take(max).Select(r => $"  - {r.Id} ({r.Now})"));
        if (Regressions.Count > max) lines.Add($"  ... and {Regressions.Count - max} more");
        if (NewPassing.Count + NewFailing.Count + NewSkipped.Count > 0)
        {
            lines.Add($"New tests (not in the baseline): {NewPassing.Count} pass, {NewFailing.Count} fail, {NewSkipped.Count} skipped.");
            lines.AddRange(NewFailing.Take(max).Select(t => $"  - failing: {t}"));
            lines.AddRange(NewSkipped.Take(max).Select(t => $"  - skipped: {t}"));
        }
        else lines.Add("No new tests.");
        if (FixedKnownRed.Count > 0) lines.Add($"Known-red tests that now pass: {string.Join(", ", FixedKnownRed.Take(max))}.");
        if (KnownRedStillFailing > 0) lines.Add($"{KnownRedStillFailing} known-red test(s) still fail, as before the change (not your concern).");
        return string.Join('\n', lines);
    }
}
