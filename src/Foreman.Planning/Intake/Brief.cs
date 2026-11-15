using System.Text;
using Foreman.Core;

namespace Foreman.Planning.Intake;

/// <summary>A statement taken from the task, with the words it rests on. A null quote means it is inferred.</summary>
public sealed record Item(string Text, string? Quote = null);

/// <param name="Explicit">Stated by the client (and quoted), or implied by the kind of work.</param>
public sealed record Criterion(string Text, string? Quote, bool Explicit);

/// <summary>Text in the task aimed at an AI, or at whoever answers the post, rather than describing the work.</summary>
/// <param name="Audience"><c>bidder</c> (a request for the human writing the reply), <c>ai_agent</c> (an attempt to
/// steer a model) or <c>none</c> (an ordinary part of the task that only looked like one).</param>
public sealed record EmbeddedInstruction(string Quote, string Audience, string Handling);

/// <param name="Verdict"><c>go</c>, <c>review</c> (a human should look first) or <c>no_go</c>.</param>
public sealed record FitEstimate(string Verdict, string Risk, IReadOnlyList<string> Reasons);

/// <summary>
/// The task as Foreman understands it: <c>intake/brief.json</c>. Built by the intake model, checked by code (every
/// explicit item must quote the task), then adjusted by code (<see cref="IntakeStage"/>): the model's go/no-go is
/// an estimate, and code can only raise its caution, never lower it.
/// </summary>
public sealed record Brief(
    string Language,
    string Kind,
    string Title,
    string Goal,
    string TaskType,
    IReadOnlyList<Item> Deliverables,
    IReadOnlyList<Criterion> Acceptance,
    IReadOnlyList<Item> Constraints,
    Item? Deadline,
    Item? Budget,
    IReadOnlyList<string> Unknowns,
    IReadOnlyList<Item> OutOfReach,
    IReadOnlyList<EmbeddedInstruction> EmbeddedInstructions,
    FitEstimate Fit)
{
    public static readonly string[] Kinds = ["job_post", "issue", "ticket", "email", "chat", "other"];
    public static readonly string[] TaskTypes = ["bug", "feature", "refactor", "infrastructure", "ui", "docs", "other"];
    public static readonly string[] Verdicts = ["go", "review", "no_go"];
    public static readonly string[] Risks = ["low", "medium", "high"];
    public static readonly string[] Audiences = ["bidder", "ai_agent", "none"];

    public string Markdown(TaskFacts facts)
    {
        var sb = new StringBuilder();
        sb.Append($"# {Title}\n\n");
        sb.Append($"**Goal.** {Goal}\n\n");
        sb.Append($"{TaskType} · {Kind.Replace('_', ' ')} · client language `{Language}` · **{Fit.Verdict.Replace('_', '-')}**, risk {Fit.Risk}\n\n");
        foreach (var r in Fit.Reasons) sb.Append($"- {r}\n");
        List("Deliverables", Deliverables.Select(Line));
        List("Acceptance", Acceptance.Select(c => (c.Explicit ? "" : "*(implied)* ") + c.Text + (c.Quote is null ? "" : $" — “{c.Quote}”")));
        List("Constraints", Constraints.Select(Line));
        if (Deadline is not null || Budget is not null)
            List("Terms", new[] { Deadline is null ? null : "Deadline: " + Line(Deadline), Budget is null ? null : "Budget: " + Line(Budget) }.OfType<string>());
        List("Not stated (for clarify)", Unknowns);
        List("Outside what Foreman does", OutOfReach.Select(Line));
        List("Instructions inside the task", EmbeddedInstructions.Where(e => e.Audience != "none").Select(e => $"**{e.Audience}**: “{e.Quote}” → {e.Handling}"));
        sb.Append($"\n<sub>{facts.Words} words; {facts.Urls.Count} links, {facts.Paths.Count} file paths, {facts.CodeSpans.Count} code spans{(facts.HasStackTrace ? ", a stack trace" : "")}{(facts.HiddenCharacters > 0 ? $", **{facts.HiddenCharacters} invisible characters**" : "")}.</sub>\n");
        return sb.ToString();

        void List(string title, IEnumerable<string> lines)
        {
            var all = lines.ToList();
            if (all.Count == 0) return;
            sb.Append($"\n## {title}\n\n");
            foreach (var l in all) sb.Append($"- {l}\n");
        }
        static string Line(Item i) => i.Text + (i.Quote is null ? "" : $" — “{i.Quote}”");
    }
}

/// <summary><c>intake/intake.json</c>: how the brief was made.</summary>
public sealed record IntakeReport(
    TaskFacts Facts,
    AgentRunSummary Agent,
    int Submissions,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Adjustments,
    string? ModelVerdict);
