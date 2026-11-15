using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Foreman.Core;

namespace Foreman.Agents;

/// <summary>
/// Runs an agent as Claude Code without a UI, on the owner's Claude subscription instead of an API key. Claude Code
/// gets none of its built-in tools (no shell, no file access on the host): its only tools are the toolbox's, served
/// by Foreman's own MCP server (<c>foreman mcp ...</c>) in a separate process. Claude Code's own system prompt is
/// replaced by ours, it runs in an empty folder, user and project settings are not loaded, and anything not
/// explicitly allowed is denied without asking.
/// </summary>
public sealed class ClaudeCodeDriver(string model = ApiDriver.DefaultModel, decimal maxBudgetUsd = 10m, TimeSpan? timeout = null) : IAgentDriver
{
    private static readonly string[] BuiltInTools =
        ["Bash", "Edit", "Write", "Read", "Glob", "Grep", "WebFetch", "WebSearch", "NotebookEdit", "Agent", "Task", "TodoWrite"];

    public string Name => "claude-code";
    public string Model => model;

    public async Task<AgentRunSummary> RunAsync(AgentRequest request, CancellationToken ct)
    {
        var runModel = request.Model ?? model;
        var budget = request.MaxBudgetUsd ?? maxBudgetUsd;
        using var span = Telemetry.Source.StartActivity($"invoke_agent {request.Agent}");
        span?.SetTag("gen_ai.operation.name", "invoke_agent");
        span?.SetTag("gen_ai.agent.name", request.Agent);
        span?.SetTag("gen_ai.request.model", runModel);
        span?.SetTag("foreman.driver", Name);

        var job = request.Job;
        var dir = Directory.CreateDirectory(Path.Combine(request.WorkDir, "claude-code")).FullName;
        var cwd = Directory.CreateDirectory(Path.Combine(dir, "cwd")).FullName;
        var home = Path.GetDirectoryName(Path.GetDirectoryName(job.Dir))!;

        var (command, prefix) = SelfCommand();
        var mcpConfig = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["foreman"] = new JsonObject
                {
                    ["type"] = "stdio",
                    ["command"] = command,
                    ["args"] = new JsonArray([.. prefix.Select(p => (JsonNode)p!), "mcp", .. request.McpArgs.Select(a => (JsonNode)a!), "--home", home]),
                },
            },
        };
        var systemFile = Path.Combine(dir, "system-prompt.md");
        File.WriteAllText(systemFile, request.System.Text, new UTF8Encoding(false));
        var mcpFile = Path.Combine(dir, "mcp.json");
        File.WriteAllText(mcpFile, mcpConfig.ToJsonString(Json.Options), new UTF8Encoding(false));

        List<string> args =
        [
            // The task goes through stdin and the system prompt through a file: both can exceed the 32,767
            // characters of a Windows command line (a first message with two code maps does).
            "-p",
            "--system-prompt-file", systemFile,
            "--model", runModel,
            "--tools", "",
            "--disallowedTools", string.Join(',', BuiltInTools),
            "--mcp-config", mcpFile,
            "--strict-mcp-config",
            "--allowedTools", "mcp__foreman",
            "--permission-mode", "dontAsk",
            "--setting-sources", "local",
            "--no-session-persistence",
            "--max-budget-usd", budget.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--output-format", "stream-json",
            "--verbose",
        ];

        var clock = Stopwatch.StartNew();
        // Some tools run for minutes (a fresh-copy verification, an image pull), far beyond Claude Code's default
        // wait for an MCP tool.
        var env = new Dictionary<string, string?> { ["MCP_TOOL_TIMEOUT"] = "3600000", ["MCP_TIMEOUT"] = "120000" };
        ProcResult r;
        // Once the toolbox has the result, the agent's work is done; Claude Code still writes a closing message, and
        // that last turn has hung for an hour on API retries. Give it a grace period, then stop it.
        using var stop = new CancellationTokenSource();
        using var watching = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var watcher = request.ReadOutcome is null ? Task.CompletedTask : StopAfterOutcomeAsync(request.ReadOutcome, stop, watching.Token);
        try
        {
            r = await Proc.RunAsync(Environment.GetEnvironmentVariable("CLAUDE_BIN") ?? "claude", args, cwd, timeout ?? TimeSpan.FromMinutes(90), env, ct, stdin: request.Task, stop: stop.Token);
        }
        finally
        {
            await watching.CancelAsync();
            try { await watcher; } catch (OperationCanceledException) { }
            // Claude Code ends its MCP servers without a graceful shutdown, so the toolbox can't release what it
            // holds (a sandbox container, a volume): the request's cleanup does it here.
            if (request.Cleanup is not null) await request.Cleanup();
        }
        File.WriteAllText(Path.Combine(dir, "stream.jsonl"), r.Stdout, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(dir, "stderr.log"), r.Stderr, new UTF8Encoding(false));

        var stream = ParseStream(r.Stdout);
        var outcome = request.ReadOutcome?.Invoke()
            ?? (r.TimedOut ? "timed_out" : null)
            ?? stream.Subtype ?? (r.Ok ? "finished" : $"exit {r.ExitCode}");
        span?.SetTag("foreman.outcome", outcome);

        // Without Claude Code's final result line (stopped after the outcome), cost comes from the per-message usage.
        var cost = stream.Subtype is null ? Pricing.Cost(runModel, stream.Usage) ?? 0 : stream.CostUsd;
        return new AgentRunSummary(Name, runModel, outcome, stream.Turns, stream.ToolCalls, cost, CostIsEstimate: true,
            stream.Usage.Input, stream.Usage.Output, stream.Usage.CacheRead, stream.Usage.CacheWrite,
            Math.Round(clock.Elapsed.TotalSeconds, 1), request.System.Sha256,
            r.Stopped ? $"stopped {OutcomeGrace.TotalSeconds:0} s after the result was in" : r.Ok ? null : r.Tail(5));
    }

    internal static readonly TimeSpan OutcomeGrace = TimeSpan.FromSeconds(90);

    private static async Task StopAfterOutcomeAsync(Func<string?> readOutcome, CancellationTokenSource stop, CancellationToken ct)
    {
        while (readOutcome() is null) await Task.Delay(TimeSpan.FromSeconds(2), ct);
        await Task.Delay(OutcomeGrace, ct);
        await stop.CancelAsync();
    }

    internal sealed record StreamSummary(int Turns, int ToolCalls, decimal CostUsd, TokenUsage Usage, string? Subtype);

    /// <summary>
    /// Reads Claude Code's stream-json: counts tool calls from assistant messages and takes turns, cost and usage
    /// from the final result line. The cost is Claude Code's list-price estimate; on a subscription it isn't billed.
    /// </summary>
    internal static StreamSummary ParseStream(string stdout)
    {
        var toolCalls = 0;
        JsonNode? result = null;
        var messages = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            JsonNode? node;
            try { node = JsonNode.Parse(line); }
            catch (JsonException) { continue; }
            var type = node?["type"]?.GetValue<string>();
            if (type == "assistant" && node?["message"]?["content"] is JsonArray content)
            {
                toolCalls += content.Count(c => c?["type"]?.GetValue<string>() == "tool_use");
                // One API message arrives as several lines (one per content block), each with the same usage.
                if (node["message"]?["id"]?.GetValue<string>() is { } id && node["message"]?["usage"] is { } u) messages[id] = u;
            }
            else if (type == "result")
                result = node;
        }

        static long Long(JsonNode? usage, string key) => usage?[key] is JsonValue v && v.TryGetValue<long>(out var l) ? l : 0;
        static TokenUsage Usage(IEnumerable<JsonNode?> usages) => usages.Aggregate(TokenUsage.Zero, (t, u) =>
            t.Add(Long(u, "input_tokens"), Long(u, "output_tokens"), Long(u, "cache_read_input_tokens"), Long(u, "cache_creation_input_tokens")));
        return new StreamSummary(
            result?["num_turns"]?.GetValue<int>() ?? messages.Count,
            toolCalls,
            result?["total_cost_usd"] is JsonValue c && c.TryGetValue<decimal>(out var cost) ? Math.Round(cost, 4) : 0,
            result is null ? Usage(messages.Values) : Usage([result["usage"]]),
            result?["subtype"]?.GetValue<string>());
    }

    /// <summary>How to start this same program again: the apphost directly, or <c>dotnet foreman.dll</c>.</summary>
    private static (string Command, string[] Prefix) SelfCommand()
    {
        var process = Environment.ProcessPath ?? throw new InvalidOperationException("cannot find the foreman executable");
        var name = Path.GetFileNameWithoutExtension(process);
        return name.Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? (process, [typeof(ClaudeCodeDriver).Assembly.Location.Replace("Foreman.Agents.dll", "foreman.dll", StringComparison.Ordinal)])
            : (process, []);
    }
}
