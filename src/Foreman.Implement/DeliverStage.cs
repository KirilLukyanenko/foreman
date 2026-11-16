using System.Text;
using System.Text.Json;
using Foreman.Core;
using Foreman.Planning.Intake;

namespace Foreman.Implement;

public sealed record DeliverOptions(string Model = DeliverOptions.DefaultModel, decimal MaxBudgetUsd = 1m)
{
    public const string DefaultModel = "claude-sonnet-5-5";
}

/// <summary><c>deliver/delivery.json</c>: the report's facts and what it cost, for the human; the client gets report.md.</summary>
public sealed record Delivery(string Language, IReadOnlyList<Fact> Facts, IReadOnlyList<string> Warnings, decimal JobCostUsd, double JobHours, AgentRunSummary Agent);

/// <summary>
/// Stage 7: the report for the client, in their language, after the change is verified: what was wrong or wanted,
/// what changed, how it was checked, and what is still open. A model writes it, but only from facts code gathered from
/// the job's files (<see cref="DeliveryFacts"/>), and code checks every line against them (<see cref="DeliveryTools"/>).
/// Nothing is sent: the human reads <c>deliver/report.md</c>, sends it with the PR, or doesn't. What the job cost stays
/// in <c>delivery.json</c> for the human; the client's price is the human's business.
/// </summary>
public sealed class DeliverStage(IAgentDriver? driver, DeliverOptions? options = null, Action<string>? log = null) : IStage
{
    private readonly DeliverOptions _options = options ?? new DeliverOptions();

    public string Name => "deliver";

    public async Task RunAsync(Job job, CancellationToken ct)
    {
        if (!File.Exists(Path.Combine(job.Dir, "intake", "brief.json"))) throw new InvalidOperationException("the job has no brief: the report is written in the client's terms");
        var implement = job.ReadJson<ImplementReport>("implement", "implement.json");
        if (implement.Status != "verified") throw new InvalidOperationException($"nothing to deliver: the change is {implement.Status.Replace('_', ' ')}");
        if (driver is null) throw new InvalidOperationException("the report needs a model: agents are off");

        var brief = job.ReadJson<Brief>("intake", "brief.json");
        var facts = DeliveryFacts.Of(job);
        var dir = job.StageDir(Name);
        foreach (var f in new[] { "submission.json", "tools.jsonl", "report.md", "delivery.json" })
            if (File.Exists(Path.Combine(dir, f))) File.Delete(Path.Combine(dir, f));

        var message = FirstMessage(brief, facts);
        File.WriteAllText(Path.Combine(dir, "task.md"), message, new UTF8Encoding(false));
        var request = new AgentRequest(
            Agent: "deliver",
            Job: job,
            System: Prompts.Load("deliver"),
            Task: message,
            WorkDir: dir,
            Toolbox: () => new DeliveryTools(job, facts, brief.Language),
            McpArgs: ["deliver", "--job", job.Id],
            Model: _options.Model,
            Effort: "low",
            MaxBudgetUsd: _options.MaxBudgetUsd,
            ReadOutcome: () => File.Exists(DeliveryTools.ResultFile(job)) ? "submitted" : null);

        log?.Invoke($"  deliver: {driver.Name}, {_options.Model}");
        var summary = await driver.RunAsync(request, ct);
        job.Event(Name, "agent", new { summary });
        if (!File.Exists(DeliveryTools.ResultFile(job)))
            throw new InvalidOperationException($"the report writer delivered nothing: {summary.Outcome}{(summary.Error is null ? "" : $" ({summary.Error})")}");

        using var doc = JsonDocument.Parse(File.ReadAllText(DeliveryTools.ResultFile(job)));
        var draft = doc.RootElement.GetProperty("draft").Deserialize<ReportDraft>(Json.Options)!;
        var warnings = doc.RootElement.GetProperty("warnings").Deserialize<List<string>>(Json.Options) ?? [];
        job.WriteText(Name, "report.md", DeliveryTools.Markdown(draft));
        var (cost, hours) = JobCost(job);
        job.WriteJson(Name, "delivery.json", new Delivery(brief.Language, facts, warnings, cost + summary.CostUsd, hours, summary));
        job.Event(Name, "written", new { brief.Language, facts = facts.Count, warnings = warnings.Count });
        log?.Invoke($"  deliver: report in {brief.Language}, {facts.Count} facts{(warnings.Count > 0 ? $", {warnings.Count} warning(s)" : "")}; ${summary.CostUsd:0.00}{(summary.CostIsEstimate ? " (estimate)" : "")}, {summary.Seconds:0} s");
    }

    internal static string FirstMessage(Brief brief, IReadOnlyList<Fact> facts)
    {
        var sb = new StringBuilder();
        sb.Append($"Write the report for the client in their language (`{brief.Language}`) and submit it with `submit_report`.\n\n");
        sb.Append($"The client's request: **{brief.Title}** ({brief.TaskType}). {brief.Goal}\n\n");
        sb.Append("## The facts (the report may say these and nothing else; cite their ids)\n\n");
        foreach (var f in facts) sb.Append($"- **{f.Id}** ({f.Kind}{(f.MustMention ? ", must_mention" : "")}): {f.Text}\n");
        return sb.ToString();
    }

    /// <summary>What the agents of this job cost at list prices, and how long it took, from the event log.</summary>
    private static (decimal Cost, double Hours) JobCost(Job job)
    {
        decimal cost = 0;
        var file = Path.Combine(job.Dir, "events.jsonl");
        if (File.Exists(file))
            foreach (var line in File.ReadLines(file))
            {
                try
                {
                    using var e = JsonDocument.Parse(line);
                    if (e.RootElement.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object && d.TryGetProperty("summary", out var s)
                        && s.ValueKind == JsonValueKind.Object && s.TryGetProperty("costUsd", out var c) && c.ValueKind == JsonValueKind.Number)
                        cost += c.GetDecimal();
                }
                catch (JsonException) { }
            }
        return (cost, Math.Round((DateTime.UtcNow - job.Manifest.CreatedAt).TotalHours, 1));
    }
}
