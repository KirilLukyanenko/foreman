using Foreman.Core;

namespace Foreman.Recon.Environment;

public sealed record EnvStep(string Name, string Script, bool Network, bool AsRoot, int TimeoutMinutes);

/// <summary>
/// How to install, build and test one stack, proven to work from a clean copy. Written by the deterministic planner
/// or by the environment agent; the baseline stage only ever runs what is here.
/// </summary>
public sealed record EnvironmentSpec(
    string Stack,
    string Source,
    string Image,
    IReadOnlyList<EnvStep> Setup,
    string TestScript,
    IReadOnlyDictionary<string, string> Env,
    IReadOnlyList<string> Notes);

/// <summary>The result of running a spec once in a fresh copy of the repository.</summary>
public sealed record Validation(bool Ok, string? Reason, int Tests, int Passed, int Failed, int Skipped, double Seconds, string? LogTail)
{
    public double PassRate => Tests == 0 ? 0 : (double)Passed / Tests;
}

/// <summary>What the agent left behind: an accepted spec, or the reason it gave up.</summary>
public sealed record AgentResult(EnvironmentSpec? Accepted, Validation? Validation, string? AcceptedFailuresReason, string? GaveUp, int ToolCalls);

public sealed record StackEnvironment(
    string Stack,
    bool Ready,
    string? Reason,
    EnvironmentSpec? Spec,
    Validation? Deterministic,
    Validation? Final,
    AgentRunSummary? Agent);

public sealed record EnvironmentReport(IReadOnlyList<StackEnvironment> Stacks);
