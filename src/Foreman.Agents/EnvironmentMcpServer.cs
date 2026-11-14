using System.ComponentModel;
using Foreman.Core;
using Foreman.Recon.Environment;
using Foreman.Recon.Triage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Foreman.Agents;

/// <summary>
/// Foreman's MCP server for the environment agent (stdio). Claude Code starts it as a child process; it owns the
/// sandbox session for one job and stack, and serves exactly the four tools of <see cref="EnvironmentTools"/>.
/// stdout belongs to the protocol, so logs go to stderr.
/// </summary>
public static class EnvironmentMcpServer
{
    public static async Task RunAsync(string home, string jobId, string stack, string image)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        var job = Job.Open(home, jobId);
        var triage = job.ReadJson<TriageReport>("triage", "triage.json");
        builder.Services.AddSingleton(new SandboxHost(new EnvironmentTools(job, stack, triage.RemovedAgentConfig), image));
        builder.Services
            .AddMcpServer(o => o.ServerInfo = new Implementation { Name = "foreman", Version = "0.1" })
            .WithStdioServerTransport()
            .WithTools<EnvironmentMcpTools>();

        await builder.Build().RunAsync();
    }
}

/// <summary>Starts the sandbox on the first tool call (image pulls can be slow) and removes it on shutdown.</summary>
public sealed class SandboxHost(EnvironmentTools tools, string image) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _started;

    public async Task<EnvironmentTools> GetAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (!_started)
            {
                await tools.StartAsync(image);
                _started = true;
            }
            return tools;
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask DisposeAsync() => tools.DisposeAsync();
}

[McpServerToolType]
public sealed class EnvironmentMcpTools(SandboxHost host)
{
    [McpServerTool(Name = "run"), Description(EnvironmentTools.RunDescription)]
    public async Task<CallToolResult> Run(
        [Description("The bash command or script.")] string command,
        [Description("Timeout in seconds, 1 to 1200. Default 300.")] int? timeout_seconds = null,
        CancellationToken ct = default) =>
        Result(await (await host.GetAsync()).RunAsync(command, timeout_seconds, ct));

    [McpServerTool(Name = "use_image"), Description(EnvironmentTools.UseImageDescription)]
    public async Task<CallToolResult> UseImage(
        [Description("Image with tag, e.g. node:22-bookworm.")] string image,
        CancellationToken ct = default) =>
        Result(await (await host.GetAsync()).UseImageAsync(image, ct));

    [McpServerTool(Name = "submit"), Description(EnvironmentTools.SubmitDescription)]
    public async Task<CallToolResult> Submit(
        [Description("The image both scripts run in.")] string image,
        [Description("Bash script run once as root with the network on, in /work/repo of a fresh copy: system packages, toolchain, dependencies, build.")] string setup_script,
        [Description("Bash script run as an unprivileged user with the network off, in /work/repo. Runs the project's tests and writes the report into \"$OUT\".")] string test_script,
        [Description("Only when fewer than half of the tests pass: why those failures are not about the environment.")] string? expected_failures_reason = null,
        CancellationToken ct = default) =>
        Result(await (await host.GetAsync()).SubmitAsync(image, setup_script, test_script, expected_failures_reason, ct));

    [McpServerTool(Name = "give_up"), Description(EnvironmentTools.GiveUpDescription)]
    public async Task<CallToolResult> GiveUp(
        [Description("What blocks the build or the tests, and what a human would have to provide.")] string reason) =>
        Result(await (await host.GetAsync()).GiveUpAsync(reason));

    private static CallToolResult Result(ToolOutput o) => new()
    {
        Content = [new TextContentBlock { Text = o.Text }],
        IsError = o.IsError,
    };
}
