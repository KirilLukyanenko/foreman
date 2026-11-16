using Foreman.Core;
using Foreman.Planning.Clarify;
using Foreman.Planning.Intake;
using Foreman.Planning.Spec;
using Foreman.Recon;
using Foreman.Recon.Baseline;
using Foreman.Recon.CodeMap;
using Foreman.Recon.Dossier;
using Foreman.Recon.Environment;
using Foreman.Recon.Security;

namespace Foreman.Implement;

/// <summary>What a job's pipeline is built with; the defaults are the models and limits the evals measured.</summary>
/// <param name="Go">Go on past intake's no-go.</param>
/// <param name="Acceptance">Null leaves out the hidden acceptance tests.</param>
public sealed record JobPipelineOptions(
    ReconOptions Recon,
    string? EnvironmentModel = null,
    string ReaderModel = DossierOptions.DefaultModel,
    IReadOnlyList<string>? Sections = null,
    string IntakeModel = IntakeOptions.DefaultModel,
    string ClarifyModel = ClarifyOptions.DefaultModel,
    string SpecModel = SpecOptions.DefaultModel,
    bool Go = false,
    AcceptanceOptions? Acceptance = null,
    ImplementOptions? Implement = null,
    DeliverOptions? Deliver = null);

/// <summary>
/// The whole job, from task text to a verified patch, in order: intake and the go gate (before recon spends anything),
/// recon, clarify, the PRP, the hidden acceptance tests, implement and verify, the client report. The stages that need a human stop with
/// <see cref="JobWaitingException"/> and pick up where they left off when the job runs again. The CLI and the service
/// build the same pipeline here.
/// </summary>
public static class JobPipeline
{
    public static IStage[] Stages(IAgentDriver? driver, bool hasTask, JobPipelineOptions o, Action<string>? log = null) =>
    [
        .. hasTask ? new IStage[] { new IntakeStage(driver, new IntakeOptions(o.IntakeModel), log), new GoStage(o.Go, log) } : [],
        new FetchStage(o.Recon),
        new TriageStage(),
        new SecurityStage(o.Recon.Limits, log),
        new CodeMapStage(null, o.Recon.Limits),
        new EnvironmentStage(o.Recon, driver, log, o.EnvironmentModel),
        new BaselineStage(o.Recon, log),
        new DossierStage(driver, new DossierOptions(o.ReaderModel, Sections: o.Sections), log),
        .. hasTask ? TaskStages(driver, o, log) : [],
    ];

    private static IStage[] TaskStages(IAgentDriver? driver, JobPipelineOptions o, Action<string>? log) =>
    [
        new ClarifyStage(driver, new ClarifyOptions(o.ClarifyModel), log),
        new SpecStage(driver, new SpecOptions(o.SpecModel), log),
        .. o.Acceptance is null ? Array.Empty<IStage>() : [new AcceptanceStage(driver, o.Acceptance, log)],
        new ImplementStage(driver, o.Implement ?? new ImplementOptions(), log),
        new DeliverStage(driver, o.Deliver, log),
    ];
}
