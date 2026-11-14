using System.Text.Json;
using Foreman.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace Foreman.Agents;

/// <summary>
/// Foreman's MCP server (stdio): serves exactly one toolbox's tools, with the toolbox's own names, descriptions and
/// JSON schemas, so the tools an agent sees through Claude Code are the same as through our own API loop. Claude Code
/// starts it as a child process. stdout belongs to the protocol, so logs go to stderr.
/// </summary>
public static class ToolboxMcpServer
{
    public static async Task RunAsync(IToolbox toolbox)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        var host = new LazyToolbox(toolbox);
        builder.Services.AddSingleton(host);
        builder.Services
            .AddMcpServer(o => o.ServerInfo = new Implementation { Name = "foreman", Version = "0.2" })
            .WithStdioServerTransport()
            .WithListToolsHandler((_, _) => ValueTask.FromResult(new ListToolsResult
            {
                Tools = toolbox.Definitions.Select(d => new Tool { Name = d.Name, Description = d.Description, InputSchema = d.Schema }).ToList(),
            }))
            .WithCallToolHandler(async (request, ct) =>
            {
                var name = request.Params?.Name ?? "";
                var input = request.Params?.Arguments is { } a ? new Dictionary<string, JsonElement>(a) : new Dictionary<string, JsonElement>();
                var output = await (await host.GetAsync(ct)).CallAsync(name, input, ct);
                return new CallToolResult { Content = [new TextContentBlock { Text = output.Text }], IsError = output.IsError };
            });

        await builder.Build().RunAsync();
    }

    /// <summary>Starts the toolbox on the first tool call (a sandbox start can be slow) and disposes it on shutdown.</summary>
    private sealed class LazyToolbox(IToolbox toolbox) : IAsyncDisposable
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private bool _started;

        public async Task<IToolbox> GetAsync(CancellationToken ct)
        {
            await _gate.WaitAsync(ct);
            try
            {
                if (!_started)
                {
                    await toolbox.StartAsync(ct);
                    _started = true;
                }
                return toolbox;
            }
            finally
            {
                _gate.Release();
            }
        }

        public ValueTask DisposeAsync() => toolbox.DisposeAsync();
    }
}
