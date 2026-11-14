using System.Text.Json;

namespace Foreman.Core;

public sealed record ToolOutput(string Text, bool IsError);

public sealed record ToolDefinition(string Name, string Description, JsonElement Schema);

/// <summary>
/// An agent's whole world: a fixed set of tools and the code behind them. A driver only connects a model to it, in
/// process (our own API loop) or through Foreman's MCP server in a separate process (Claude Code). Every effect an
/// agent has goes through a toolbox, so the toolbox is also where limits, logging and verification live.
/// </summary>
public interface IToolbox : IAsyncDisposable
{
    IReadOnlyList<ToolDefinition> Definitions { get; }

    int ToolCalls { get; }

    /// <summary>True once the agent has delivered (or given up); further calls are refused.</summary>
    bool Finished { get; }

    /// <summary>A short word for how it ended (<c>accepted</c>, <c>gave_up</c>, <c>submitted</c>), or null.</summary>
    string? Outcome { get; }

    Task StartAsync(CancellationToken ct);

    Task<ToolOutput> CallAsync(string name, IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct);
}

/// <summary>One agent run: who, with what prompt and task, over which toolbox, on which model.</summary>
/// <param name="Agent">Name for traces and logs: <c>environment</c>, <c>reader-architecture</c>.</param>
/// <param name="WorkDir">Folder for the driver's own files (stream logs, MCP config).</param>
/// <param name="Toolbox">Builds the toolbox in this process.</param>
/// <param name="McpArgs">Arguments of <c>foreman mcp</c> that build the same toolbox in another process.</param>
/// <param name="ReadOutcome">For out-of-process drivers: reads the outcome the toolbox wrote to the job folder.</param>
/// <param name="Cleanup">Runs after an out-of-process run, which can end without the toolbox being disposed.</param>
public sealed record AgentRequest(
    string Agent,
    Job Job,
    Prompt System,
    string Task,
    string WorkDir,
    Func<IToolbox> Toolbox,
    IReadOnlyList<string> McpArgs,
    string? Model = null,
    string? Effort = null,
    decimal? MaxBudgetUsd = null,
    Func<string?>? ReadOutcome = null,
    Func<Task>? Cleanup = null);

public sealed record AgentRunSummary(
    string Driver,
    string Model,
    string Outcome,
    int Turns,
    int ToolCalls,
    decimal CostUsd,
    bool CostIsEstimate,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    double Seconds,
    string PromptSha256,
    string? Error = null);

/// <summary>The model loop. Drivers are interchangeable over the same toolbox, which makes them an ablation.</summary>
public interface IAgentDriver
{
    string Name { get; }

    /// <summary>The model used when a request doesn't name one.</summary>
    string Model { get; }

    Task<AgentRunSummary> RunAsync(AgentRequest request, CancellationToken ct);
}
