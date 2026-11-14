using System.Diagnostics;
using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta;
using Anthropic.Models.Beta.Messages;
using Foreman.Core;
using Foreman.Recon.Environment;

namespace Foreman.Agents;

/// <summary>
/// Our own agent loop on the Claude API (Anthropic C# SDK): send the conversation with the four sandbox tools, run
/// the tools the model asks for, return every result, repeat. Nothing is hidden behind a runner, so each turn's
/// tokens, cache hits and tool calls are visible. The model never touches the host: its only effect on the world is
/// through <see cref="EnvironmentTools"/>, inside the sandbox.
///
/// Credentials come from the SDK's usual chain (ANTHROPIC_API_KEY, or a profile from <c>ant auth login</c>).
/// </summary>
public sealed class ApiEnvironmentDriver(string model = ApiEnvironmentDriver.DefaultModel, string effort = "medium", int maxTurns = 100) : IEnvironmentDriver
{
    public const string DefaultModel = "claude-opus-5-5";

    public string Name => "api";
    public string Model => model;

    public async Task<AgentRunSummary> RunAsync(EnvironmentAgentRequest request, CancellationToken ct)
    {
        using var span = Telemetry.Source.StartActivity("invoke_agent environment");
        span?.SetTag("gen_ai.operation.name", "invoke_agent");
        span?.SetTag("gen_ai.agent.name", "environment");
        span?.SetTag("gen_ai.provider.name", "anthropic");
        span?.SetTag("gen_ai.request.model", model);
        span?.SetTag("foreman.prompt.sha256", request.System.Sha256);

        var clock = Stopwatch.StartNew();
        var usage = TokenUsage.Zero;
        var turns = 0;
        string outcome;
        string? error = null;

        await using var tools = new EnvironmentTools(request.Job, request.Stack, request.ExcludedPaths, null, request.Limits);
        try
        {
            await tools.StartAsync(request.StartImage);
            var client = new AnthropicClient();
            List<BetaMessageParam> messages = [new BetaUserMessageParam { Content = request.Task }];
            outcome = "max_turns";

            for (turns = 1; turns <= maxTurns; turns++)
            {
                var response = await client.Beta.Messages.Create(new MessageCreateParams
                {
                    Model = model,
                    MaxTokens = 16000,
                    // The system prompt and the tool list never change within a run: one cached prefix for every turn.
                    System = new List<BetaTextBlockParam> { new() { Text = request.System.Text, CacheControl = new BetaCacheControlEphemeral() } },
                    Tools = ToolDefinitions(),
                    Messages = messages,
                    OutputConfig = new BetaOutputConfig { Effort = ParseEffort(effort) },
                    // If a safety classifier declines, the API retries on a fallback model inside the same call.
                    Betas = [AnthropicBeta.ServerSideFallback2026_07_01],
                    Fallbacks = new Default(),
                }, ct);

                usage = usage.Add(response.Usage.InputTokens, response.Usage.OutputTokens,
                    response.Usage.CacheReadInputTokens ?? 0, response.Usage.CacheCreationInputTokens ?? 0);

                List<BetaContentBlockParam> assistant = [];
                List<BetaContentBlockParam> results = [];
                foreach (var block in response.Content)
                {
                    if (block.TryPickText(out var text))
                        assistant.Add(new BetaTextBlockParam { Text = text.Text });
                    else if (block.TryPickThinking(out var thinking))
                        // Thinking blocks go back unchanged, signature included: the API checks them.
                        assistant.Add(new BetaThinkingBlockParam { Thinking = thinking.Thinking, Signature = thinking.Signature });
                    else if (block.TryPickRedactedThinking(out var redacted))
                        assistant.Add(new BetaRedactedThinkingBlockParam { Data = redacted.Data });
                    else if (block.TryPickFallback(out var fallback))
                        // Marks where a fallback model took over; it stays in the history like any other block.
                        assistant.Add(new BetaFallbackBlockParam
                        {
                            From = BetaFallbackInfoParam.FromRawUnchecked(fallback.From.RawData),
                            To = BetaFallbackInfoParam.FromRawUnchecked(fallback.To.RawData),
                        });
                    else if (block.TryPickToolUse(out var call))
                    {
                        assistant.Add(new BetaToolUseBlockParam { ID = call.ID, Name = call.Name, Input = call.Input });
                        var output = await tools.CallAsync(call.Name, call.Input, ct);
                        // Exactly one result per tool call, all in one message, or the API rejects the next turn.
                        results.Add(new BetaToolResultBlockParam { ToolUseID = call.ID, Content = output.Text, IsError = output.IsError });
                    }
                }

                var stop = response.StopReason?.ToString() ?? "";
                if (stop == "refusal") { outcome = "refused"; break; }
                if (results.Count == 0) { outcome = tools.Finished ? "finished" : $"stopped ({stop})"; break; }

                messages.Add(new BetaAssistantMessageParam { Content = assistant });
                messages.Add(new BetaUserMessageParam { Content = results });
            }
        }
        catch (AnthropicApiException e)
        {
            outcome = "api_error";
            error = e.Message;
        }
        catch (InvalidOperationException e)
        {
            outcome = "sandbox_error";
            error = e.Message;
        }

        if (tools.Result is { } r) outcome = r.Accepted is not null ? "accepted" : "gave_up";
        span?.SetTag("gen_ai.usage.input_tokens", usage.Input);
        span?.SetTag("gen_ai.usage.output_tokens", usage.Output);
        span?.SetTag("foreman.outcome", outcome);

        return new AgentRunSummary(Name, model, outcome, turns, tools.ToolCalls, Pricing.Cost(model, usage) ?? 0, CostIsEstimate: false,
            usage.Input, usage.Output, usage.CacheRead, usage.CacheWrite, Math.Round(clock.Elapsed.TotalSeconds, 1), request.System.Sha256, error);
    }

    private static List<BetaToolUnion> ToolDefinitions() =>
        EnvironmentTools.Definitions.Select(d => (BetaToolUnion)new BetaTool
        {
            Name = d.Name,
            Description = d.Description,
            InputSchema = new()
            {
                Properties = d.Schema.GetProperty("properties").EnumerateObject().ToDictionary(p => p.Name, p => p.Value),
                Required = d.Schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()!).ToList(),
            },
        }).ToList();

    private static Effort ParseEffort(string effort) => effort switch
    {
        "low" => Effort.Low,
        "high" => Effort.High,
        "max" => Effort.Max,
        _ => Effort.Medium,
    };
}
