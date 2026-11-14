using System.Diagnostics;

namespace Foreman.Core;

/// <summary>
/// One trace per job, one span per stage. Attribute names follow the OpenTelemetry GenAI semantic conventions
/// where one exists (they are still in Development status, so names may change); the rest are under "foreman.".
/// Exporters are configured by the host; without one, spans cost nothing.
/// </summary>
public static class Telemetry
{
    public const string SourceName = "Foreman";
    public static readonly ActivitySource Source = new(SourceName, typeof(Telemetry).Assembly.GetName().Version?.ToString());

    public static Activity? StartJob(Job job)
    {
        var a = Source.StartActivity("foreman.job");
        a?.SetTag("foreman.job.id", job.Id);
        return a;
    }

    public static Activity? StartStage(Job job, string stage)
    {
        // A stage is a workflow step; when it runs an agent, the agent span (gen_ai.operation.name = invoke_agent)
        // is a child of this one.
        var a = Source.StartActivity($"stage {stage}");
        a?.SetTag("foreman.job.id", job.Id);
        a?.SetTag("foreman.stage", stage);
        return a;
    }
}
