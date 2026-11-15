using System.Text;
using Foreman.Core;

namespace Foreman.Planning.Spec;

/// <summary>How a requirement will be shown to hold. Every requirement has at least one.</summary>
/// <param name="Kind"><c>acceptance_test</c> (a hidden black-box test written from the spec alone),
/// <c>existing_tests</c> (named tests already in the repository keep or start passing), <c>command</c> (a command
/// whose exit code decides, such as a linter), or <c>review</c> (the reviewer judges it against the spec; for what
/// tests can't show).</param>
public sealed record Check(string Kind, string How);

/// <param name="Sources">Where it comes from: <c>C2</c> (the brief's acceptance criterion 2), <c>Q1</c> (an answered
/// question), <c>A1</c> (an assumption), <c>K1</c> (what the code showed), or <c>task</c>.</param>
public sealed record Requirement(string Id, string Statement, IReadOnlyList<string> Sources, IReadOnlyList<Check> Verify);

/// <summary>A name the acceptance tests may call. Existing ones are checked against the code index.</summary>
/// <param name="Status"><c>existing</c>, <c>changed</c> (exists, its behaviour or signature changes) or <c>new</c>.</param>
public sealed record InterfaceItem(string Name, string Path, string Status, string Signature, string? Note = null);

public sealed record Example(string Id, string Given, string When, string Then, IReadOnlyList<string> Requirements);

public sealed record Decision(string Text, string Reason, string Source);

/// <param name="New">A file this change creates.</param>
public sealed record TouchPoint(string Path, IReadOnlyList<string> Symbols, string Change, bool New = false);

/// <param name="Kind"><c>reproduce</c> (a failing test that shows the bug or the missing behaviour), <c>implement</c>,
/// <c>test</c>, <c>docs</c>.</param>
public sealed record SpecTask(string Id, string Kind, string Task, string DoneWhen);

/// <summary>A protected path (tests, CI, lockfiles) the change may edit, and why. Verify's diff guard reads this.</summary>
public sealed record AllowedEdit(string Path, string Reason);

/// <summary>What the spec model writes; everything else in the PRP comes from code.</summary>
public sealed record SpecDraft(
    string Title,
    string Goal,
    IReadOnlyList<string> Why,
    IReadOnlyList<Requirement> Requirements,
    IReadOnlyList<InterfaceItem> Interface,
    IReadOnlyList<Example> Examples,
    IReadOnlyList<string> OutOfScope,
    IReadOnlyList<Decision> Decisions,
    IReadOnlyList<TouchPoint> TouchPoints,
    IReadOnlyList<string> Gotchas,
    IReadOnlyList<SpecTask> Tasks,
    IReadOnlyList<string> AntiPatterns,
    IReadOnlyList<AllowedEdit> AllowedEdits);

/// <summary>A validation command for the implementer, gathered by code from recon.</summary>
public sealed record ValidationStep(string Name, string Command, string Source, bool Verified = false);

/// <summary><c>spec/prp.json</c>: the draft as accepted, plus what code added.</summary>
public sealed record Prp(
    string JobId,
    string Commit,
    string Status,
    DateTime Created,
    SpecDraft Spec,
    IReadOnlyList<ValidationStep> Validation,
    IReadOnlyList<string> KnownRed,
    IReadOnlyList<string> DoNotEdit,
    IReadOnlyList<string> Warnings)
{
    public static readonly string[] CheckKinds = ["acceptance_test", "existing_tests", "command", "review"];
    public static readonly string[] InterfaceStatuses = ["existing", "changed", "new"];
    public static readonly string[] TaskKinds = ["reproduce", "implement", "test", "docs"];

    /// <summary>The PRP as a document, in the shape of comp-ledger's template, cut to what a single change needs.</summary>
    public string Markdown(string source, string? taskType)
    {
        var s = Spec;
        var sb = new StringBuilder();
        sb.Append($"# PRP: {s.Title}\n\n");
        sb.Append("| Field | Value |\n|---|---|\n");
        sb.Append($"| Status | {Status} |\n| Job | `{JobId}` |\n| Repository | `{source}` at `{Commit[..Math.Min(12, Commit.Length)]}` |\n| Type | {taskType ?? "unknown"} |\n| Created | {Created:yyyy-MM-dd} |\n\n");
        sb.Append("Written for an engineer (human or agent) who is very capable, has never seen this codebase and can't ask questions. ");
        sb.Append("If the PRP looks wrong, stop and report; don't work around it.\n\n");

        sb.Append($"## 1. Goal\n\n{s.Goal.Trim()}\n\n");
        sb.Append("## 2. Why\n\n");
        foreach (var w in s.Why) sb.Append($"- {w}\n");

        sb.Append("\n## 3. Requirements and how each is checked\n\n");
        sb.Append("The done contract: a requirement is met when its checks pass, and only then.\n\n");
        sb.Append("| # | Requirement | From | Checked by |\n|---|---|---|---|\n");
        foreach (var r in s.Requirements)
            sb.Append($"| {r.Id} | {Cell(r.Statement)} | {string.Join(", ", r.Sources)} | {string.Join("<br>", r.Verify.Select(v => $"**{v.Kind.Replace('_', ' ')}**: {Cell(v.How)}"))} |\n");

        if (s.Interface.Count > 0)
        {
            sb.Append("\n### 3.1 Public interface\n\nThe hidden acceptance tests call only these, as written here.\n\n");
            sb.Append("| Name | File | Status | Signature |\n|---|---|---|---|\n");
            foreach (var i in s.Interface) sb.Append($"| `{i.Name}` | `{i.Path}` | {i.Status} | `{Cell(i.Signature)}`{(i.Note is null ? "" : $" {Cell(i.Note)}")} |\n");
        }

        sb.Append("\n## 4. Acceptance examples (public)\n\nThe hidden tests check the same rules with other data.\n\n");
        foreach (var e in s.Examples)
            sb.Append($"**{e.Id}** ({string.Join(", ", e.Requirements)}).\nGiven {e.Given.Trim()}\nWhen {e.When.Trim()}\nThen {e.Then.Trim()}\n\n");

        sb.Append("## 5. Out of scope\n\n");
        foreach (var o in s.OutOfScope) sb.Append($"- {o}\n");

        sb.Append("\n## 6. Context\n\n### 6.1 Decisions (fixed: changing one needs a PRP update first)\n\n| Decision | Reason | From |\n|---|---|---|\n");
        foreach (var d in s.Decisions) sb.Append($"| {Cell(d.Text)} | {Cell(d.Reason)} | {d.Source} |\n");
        sb.Append("\n### 6.2 Where the change goes\n\n| File | Symbols | Change |\n|---|---|---|\n");
        foreach (var t in s.TouchPoints) sb.Append($"| `{t.Path}`{(t.New ? " (new)" : "")} | {string.Join(", ", t.Symbols.Select(x => $"`{x}`"))} | {Cell(t.Change)} |\n");
        sb.Append("\nRead `dossier/dossier.md` (architecture, conventions, risks) and `dossier/AGENTS.md` before starting.\n");
        if (s.Gotchas.Count > 0)
        {
            sb.Append("\n### 6.3 Known gotchas\n\n");
            foreach (var (g, i) in s.Gotchas.Select((g, i) => (g, i + 1))) sb.Append($"{i}. {g}\n");
        }

        sb.Append("\n## 7. Tasks (in order; one commit per task)\n\n| # | Kind | Task | Done when |\n|---|---|---|---|\n");
        foreach (var t in s.Tasks) sb.Append($"| {t.Id} | {t.Kind} | {Cell(t.Task)} | {Cell(t.DoneWhen)} |\n");

        sb.Append("\n## 8. Validation loop\n\n");
        if (!Validation.Any(v => v.Verified))
            sb.Append("**No test command was verified for this job** (recon's baseline didn't run or found none). Find the repository's own test command first and say in the PR how the tests were run.\n\n");
        if (Validation.Count > 0)
        {
            sb.Append("Each command says where it comes from; only the baseline's were run by recon (network off).\n\n```bash\n");
            foreach (var v in Validation) sb.Append($"# {v.Name} ({v.Source})\n{v.Command}\n");
            sb.Append("```\n");
        }
        if (KnownRed.Count > 0) sb.Append($"\nAlready failing before this change (not yours to fix unless a requirement says so): {string.Join("; ", KnownRed.Take(12))}{(KnownRed.Count > 12 ? $"; and {KnownRed.Count - 12} more" : "")}.\n");
        sb.Append("\nNever weaken, skip or delete a test to make it pass.\n");

        sb.Append("\n## 9. Definition of done\n\n");
        sb.Append("- [ ] Every requirement in section 3 holds, shown by its checks.\n");
        sb.Append("- [ ] Every test that passed before the change still passes (the baseline, flaky tests excepted).\n");
        sb.Append("- [ ] The hidden acceptance tests pass.\n");
        sb.Append($"- [ ] No edits to tests that existed, CI, lockfiles or test configuration{(s.AllowedEdits.Count > 0 ? ", except those allowed below" : "")}.\n");
        sb.Append("- [ ] No secrets, credentials or hostnames added; no vulnerable dependency added.\n");
        sb.Append("- [ ] The PR description maps each requirement to the test or check that shows it.\n");
        if (s.AllowedEdits.Count > 0)
        {
            sb.Append("\nAllowed edits to protected paths:\n\n");
            foreach (var a in s.AllowedEdits) sb.Append($"- `{a.Path}`: {a.Reason}\n");
        }
        if (DoNotEdit.Count > 0) sb.Append($"\nGenerated or vendored, don't edit by hand: {string.Join(", ", DoNotEdit.Take(10).Select(p => $"`{p}`"))}{(DoNotEdit.Count > 10 ? $" and {DoNotEdit.Count - 10} more" : "")}.\n");

        sb.Append("\n## 10. Anti-patterns\n\n");
        foreach (var a in s.AntiPatterns) sb.Append($"- {a}\n");

        sb.Append("\n## 11. How this PRP is checked beyond the implementer's view\n\n");
        sb.Append("- Hidden acceptance tests, written by a separate session from this PRP and the public interface only; they must fail on the base commit and catch deliberately broken versions of the code.\n");
        sb.Append("- The baseline: every test that passed before must still pass.\n");
        sb.Append("- A diff guard: protected paths, and changes far from section 6.2, need a reason.\n");
        sb.Append("- A reviewer with fresh context, given only this PRP and the diff, goes requirement by requirement.\n");
        if (Warnings.Count > 0) sb.Append($"\n> Not fully checked when written: {string.Join("; ", Warnings)}\n");
        return sb.ToString();
    }

    private static string Cell(string s) => s.Replace("|", "\\|").Replace("\r", "").Replace("\n", " ").Trim();
}

/// <summary><c>spec/spec.json</c>: how the PRP was made.</summary>
public sealed record SpecReport(int Requirements, IReadOnlyDictionary<string, int> Checks, int Examples, int Tasks, int Submissions, IReadOnlyList<string> Warnings, AgentRunSummary Agent);

/// <summary><c>spec/ready.json</c>: the human's approval of one exact PRP.</summary>
/// <param name="EditedByHand">prp.md differed from the generated text when marked Ready; prp.md is the contract.</param>
public sealed record ReadyMark(DateTime At, string PrpSha256, string JsonSha256, bool EditedByHand = false);
