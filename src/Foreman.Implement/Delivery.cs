using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Foreman.Core;
using Foreman.Planning.Clarify;
using Foreman.Planning.Intake;
using Foreman.Planning.Spec;
using Foreman.Recon.Dossier;

namespace Foreman.Implement;

/// <summary>
/// Something true about the job that the client report may say, with the evidence in <paramref name="Text"/>.
/// <paramref name="MustMention"/>: the report must tell the client (a requirement not done, a default assumed because
/// the client didn't answer, a check that couldn't run, a case the tests left open).
/// </summary>
public sealed record Fact(string Id, string Kind, string Text, bool MustMention = false);

/// <summary>One line of the report and the facts it rests on.</summary>
public sealed record ReportItem(string Text, IReadOnlyList<string> Facts);

public sealed record ReportHeadings(string Done, string Checked, string Open);

/// <summary>What the report writer submits: everything in the client's language, every line tied to facts.</summary>
public sealed record ReportDraft(string Subject, ReportItem Summary, IReadOnlyList<ReportItem> Done, IReadOnlyList<ReportItem> Checked, IReadOnlyList<ReportItem> Open, ReportHeadings Headings);

/// <summary>
/// The facts of a finished job, gathered by code from its files: the client report may say these and nothing else.
/// Every number the report uses must be in a fact it cites, so it can't round up a test count or invent a duration.
/// </summary>
public static class DeliveryFacts
{
    public static IReadOnlyList<Fact> Of(Job job)
    {
        var facts = new List<Fact>();
        void Add(string kind, string text, bool must = false) => facts.Add(new Fact($"F{facts.Count + 1}", kind, text, must));

        var brief = job.ReadJson<Brief>("intake", "brief.json");
        Add("task", $"The task: {brief.Title}. Goal: {brief.Goal}");
        foreach (var (c, i) in brief.Acceptance.Where(c => c.Explicit).Select((c, i) => (c, i + 1)))
            Add("criterion", $"The client's acceptance criterion C{i}: {c.Text}");

        if (File.Exists(Path.Combine(job.Dir, "clarify", "answers.json")))
            foreach (var a in job.ReadJson<List<Answer>>("clarify", "answers.json"))
                Add(a.Source == "client" ? "answer" : "default",
                    a.Source == "client" ? $"The client answered \"{a.Question}\": {a.Value}" : $"The client didn't answer \"{a.Question}\"; the default was used: {a.Value}",
                    must: a.Source != "client");
        if (File.Exists(Path.Combine(job.Dir, "clarify", "questions.json")))
            foreach (var g in job.ReadJson<List<Gap>>("clarify", "questions.json").Where(g => g.Resolution == "assume"))
                Add("assumption", $"Assumed without asking: {g.What} → {g.Answer}", must: g.Impact is "high" or "medium");

        var prp = job.ReadJson<Prp>("spec", "prp.json");
        var implement = job.ReadJson<ImplementReport>("implement", "implement.json");
        var statuses = implement.Submission?.Requirements.ToDictionary(r => r.Id, StringComparer.Ordinal) ?? [];
        foreach (var r in prp.Spec.Requirements)
        {
            if (statuses.TryGetValue(r.Id, out var s) && s.Status == "not_done")
                Add("not_done", $"Not done: {r.Statement} Why: {s.Note ?? "no reason given"}", must: true);
            else
                Add("done", $"Done: {r.Statement}{(statuses.TryGetValue(r.Id, out var d) ? $" Where: {d.Where}" : "")}");
        }
        foreach (var o in prp.Spec.OutOfScope.Take(5)) Add("out_of_scope", $"Left out on purpose (out of scope): {o}");

        if (File.Exists(Path.Combine(job.Dir, "verify", "verify.json")))
        {
            var v = job.ReadJson<VerifyReport>("verify", "verify.json");
            Add("change", $"The change: {v.FilesChanged} files, {v.LinesAdded} lines added and {v.LinesRemoved} removed.");
            if (v.Tests is { } t)
            {
                // The rest of the suite explained, or "605 of 629 pass" reads like 24 failures the change caused.
                var rest = new List<string>();
                if (t.Skipped > 0) rest.Add($"{t.Skipped} are skipped by the project itself");
                if (t.KnownRedStillFailing > 0) rest.Add($"{t.KnownRedStillFailing} were already failing before the change and still do");
                Add("tests", $"The project's own test suite after the change: {t.Passed} of {t.Ran} tests pass{(rest.Count > 0 ? $" ({string.Join("; ", rest)})" : "")}; " +
                             $"{t.Regressions.Count} tests that passed before fail now.");
                if (t.NewPassing.Count > 0) Add("new_tests", $"{t.NewPassing.Count} new tests were added and pass.");
            }
            if (v.Repro is { Reproducing.Count: > 0 } repro)
                Add("repro", $"{repro.Reproducing.Count} of the new tests fail on the code before the change and pass with it: they show the problem and its fix.");
            if (v.Hidden is { } h)
                Add("hidden", $"{h.Passing.Count} of {h.Tests} independent acceptance tests, written from the specification by a separate reviewer who never saw the change, pass.");
            foreach (var l in v.Lint.Where(l => l.After == "pass"))
                Add("lint", $"The project's check `{l.Command}` passes after the change.");
            foreach (var l in v.Lint.Where(l => l.After != "pass" && l.Before != "pass"))
                Add("lint_not_run", $"The project's check `{l.Command}` couldn't run in the isolated environment (it failed before the change too), so it wasn't confirmed.", must: true);
            if (v.Review is { } review)
            {
                Add("review", $"A separate code review checked every requirement: {review.Requirements.Count(r => r.Verdict == "met")} of {review.Requirements.Count} met.");
                foreach (var n in review.Notes.Take(5)) Add("review_note", $"A reviewer's minor remark, not acted on: {n.Problem}");
            }
        }
        if (File.Exists(Path.Combine(job.Dir, "acceptance", "acceptance.json")))
            foreach (var g in job.ReadJson<AcceptanceReport>("acceptance", "acceptance.json").Gaps)
                Add("test_gap", $"A case the specification left open, so no test checks it: {g}", must: true);
        foreach (var n in (implement.Submission?.Notes ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(5))
            Add("note", $"Noticed while working, not changed: {n}");

        var finished = job.ReadState().GetValueOrDefault("implement")?.FinishedAt ?? DateTime.UtcNow;
        var hours = (finished - job.Manifest.CreatedAt).TotalHours;
        Add("time", hours < 1 ? $"From the task to the checked change: {Math.Max(1, (int)Math.Round(hours * 60))} minutes." : $"From the task to the checked change: {Math.Round(hours, 1).ToString(CultureInfo.InvariantCulture)} hours.");
        return facts;
    }
}

/// <summary>
/// The report writer's only tool: <c>submit_report</c>. Code checks the meaning: every line cites facts that exist,
/// every number in a line is in a fact it cites, every fact the client must hear is in the open part, the text is in
/// the client's language, and it doesn't name Foreman's internals (requirement ids, the PRP).
/// </summary>
public sealed partial class DeliveryTools(Job job, IReadOnlyList<Fact> facts, string language) : IToolbox
{
    public const int MaxSubmissions = 3;
    public const int MaxToolCalls = 6;
    private int _submissions;

    public int ToolCalls { get; private set; }
    public ReportDraft? Result { get; private set; }
    public IReadOnlyList<string> Warnings { get; private set; } = [];
    public bool Finished => Result is not null;
    public string? Outcome => Finished ? "submitted" : null;
    public static string ResultFile(Job job) => Path.Combine(job.Dir, "deliver", "submission.json");

    public IReadOnlyList<ToolDefinition> Definitions => [SubmitReport];

    public Task StartAsync(CancellationToken ct)
    {
        if (File.Exists(ResultFile(job))) File.Delete(ResultFile(job));
        return Task.CompletedTask;
    }

    public Task<ToolOutput> CallAsync(string name, IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct)
    {
        ToolOutput output;
        if (Finished) output = new ToolOutput("already submitted; stop now", true);
        else if (++ToolCalls > MaxToolCalls) output = new ToolOutput("tool budget used up", true);
        else output = name == "submit_report" ? Submit(input) : new ToolOutput($"unknown tool {name}: the only tool is submit_report", true);
        ToolInput.Log(job.StageDir("deliver"), ToolCalls, name, input, output);
        return Task.FromResult(output);
    }

    private ToolOutput Submit(IReadOnlyDictionary<string, JsonElement> input)
    {
        _submissions++;
        ReportDraft? draft;
        try { draft = JsonSerializer.SerializeToElement(input).Deserialize<ReportDraft>(ToolInput.SnakeCase); }
        catch (JsonException e) { return Rejected([$"the input doesn't match the schema: {e.Message}"]); }
        if (draft is null) return Rejected(["empty input"]);
        draft = draft with { Done = draft.Done ?? [], Checked = draft.Checked ?? [], Open = draft.Open ?? [] };
        var errors = Check(draft, facts, language);
        if (errors.Count > 0 && _submissions < MaxSubmissions) return Rejected(errors);
        Result = draft;
        Warnings = errors;
        job.WriteJson("deliver", "submission.json", new { draft, warnings = errors, submissions = _submissions });
        return new ToolOutput(errors.Count == 0 ? "ACCEPTED. You are done: stop now." : "Kept with the problems listed as warnings. Stop now.", false);
    }

    private ToolOutput Rejected(IReadOnlyList<string> errors) =>
        new($"NOT ACCEPTED (attempt {_submissions} of {MaxSubmissions}), fix these and call submit_report again:\n- " + string.Join("\n- ", errors), true);

    internal static List<string> Check(ReportDraft d, IReadOnlyList<Fact> facts, string language)
    {
        var errors = new List<string>();
        var byId = facts.ToDictionary(f => f.Id, StringComparer.Ordinal);
        var all = new List<(string Part, ReportItem Item)> { ("summary", d.Summary) };
        all.AddRange(d.Done.Select(i => ("done", i)));
        all.AddRange(d.Checked.Select(i => ("checked", i)));
        all.AddRange(d.Open.Select(i => ("open", i)));
        if (string.IsNullOrWhiteSpace(d.Subject)) errors.Add("subject is empty");
        if (d.Done.Count == 0) errors.Add("done: at least one line");
        if (d.Checked.Count == 0) errors.Add("checked: at least one line on how the change was checked");
        if (d.Done.Count + d.Checked.Count + d.Open.Count > 20) errors.Add("at most 20 lines in all: the client reads this in a minute");

        foreach (var (part, item) in all)
        {
            var label = $"{part}: \"{RepoReader.Trim(item.Text ?? "", 60)}\"";
            if (string.IsNullOrWhiteSpace(item.Text)) { errors.Add($"{part}: a line is empty"); continue; }
            var cited = (item.Facts ?? []).Where(byId.ContainsKey).Select(id => byId[id]).ToList();
            foreach (var id in (item.Facts ?? []).Where(id => !byId.ContainsKey(id))) errors.Add($"{label} cites {id}, which doesn't exist");
            if (cited.Count == 0) { errors.Add($"{label} cites no fact: every line must rest on the facts"); continue; }
            var known = cited.SelectMany(f => Numbers(f.Text)).ToHashSet(StringComparer.Ordinal);
            foreach (var n in Numbers(item.Text).Where(n => !known.Contains(n)))
                errors.Add($"{label} says {n}, which none of its facts ({string.Join(", ", cited.Select(f => f.Id))}) says");
            if (Internal().Match(item.Text) is { Success: true } m)
                errors.Add($"{label} names \"{m.Value}\": the client doesn't know Foreman's internals (requirement ids, the PRP, Foreman); say it in their terms");
        }
        foreach (var f in facts.Where(f => f.MustMention && !d.Open.Any(i => (i.Facts ?? []).Contains(f.Id))))
            errors.Add($"{f.Id} must be in the open part, the client has to hear it: {RepoReader.Trim(f.Text, 120)}");

        var text = string.Join(' ', all.Select(a => a.Item.Text).Append(d.Subject));
        if (LanguageProblem(text, language) is { } problem) errors.Add(problem);
        return errors;
    }

    /// <summary>Every run of digits, so "9,0" and "9.0" both read as 9 and 0, and "2 808" as 2 and 808.</summary>
    internal static IEnumerable<string> Numbers(string text) => Digits().Matches(text).Select(m => m.Value.TrimStart('0') is { Length: > 0 } v ? v : "0");

    /// <summary>Null if the text is in the client's language as far as its script shows (Cyrillic or Latin).</summary>
    internal static string? LanguageProblem(string text, string language)
    {
        var letters = text.Where(char.IsLetter).ToList();
        if (letters.Count < 20) return null;
        var cyrillic = letters.Count(c => c is >= 'Ѐ' and <= 'ӿ') / (double)letters.Count;
        return language switch
        {
            "ru" or "uk" or "be" or "bg" or "sr" when cyrillic < 0.5 => $"the client writes in {language}: the report must be in {language}",
            "en" or "de" or "fr" or "es" or "it" or "pl" or "pt" or "nl" when cyrillic > 0.2 => $"the client writes in {language}: the report must be in {language}",
            _ => null,
        };
    }

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();

    [GeneratedRegex(@"\b(?:R\d{1,2}|PRP|Foreman)\b")]
    private static partial Regex Internal();

    private static object Item(string description) => new
    {
        type = "object",
        description,
        properties = new
        {
            text = new { type = "string" },
            facts = new { type = "array", items = new { type = "string" }, description = "The fact ids (F1, F2...) this line rests on." },
        },
        required = new[] { "text", "facts" },
        additionalProperties = false,
    };

    private static object Items(string description) => new { type = "array", items = Item("One line."), description };

    private static readonly ToolDefinition SubmitReport = new("submit_report",
        "Submit the client report, everything in the client's language. Foreman checks that every line cites facts that exist, every number in a line " +
        "is in a fact it cites, every fact marked must_mention is in `open`, the language is the client's, and no internal names appear.",
        RepoReader.Schema(new
        {
            type = "object",
            properties = new
            {
                subject = new { type = "string", description = "A short subject line, as for an email." },
                summary = Item("Two or three sentences: what was wrong or wanted, what is true now, whether anything needs the client."),
                done = Items("What changed, in the client's terms, one line each."),
                @checked = Items("How the change was checked, one line each."),
                open = Items("What the client should know: everything not done, assumed or not confirmed. Empty only if there is nothing."),
                headings = new
                {
                    type = "object",
                    properties = new { done = new { type = "string" }, @checked = new { type = "string" }, open = new { type = "string" } },
                    required = new[] { "done", "checked", "open" },
                    additionalProperties = false,
                    description = "The three section titles in the client's language.",
                },
            },
            required = new[] { "subject", "summary", "done", "checked", "open", "headings" },
            additionalProperties = false,
        }));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>The report as the client reads it. Facts stay in delivery.json for the human.</summary>
    public static string Markdown(ReportDraft d)
    {
        var sb = new StringBuilder($"# {d.Subject.Trim()}\n\n{d.Summary.Text.Trim()}\n");
        void Section(string title, IReadOnlyList<ReportItem> items)
        {
            if (items.Count == 0) return;
            sb.Append($"\n## {title.Trim()}\n\n");
            foreach (var i in items) sb.Append($"- {i.Text.Trim()}\n");
        }
        Section(d.Headings.Done, d.Done);
        Section(d.Headings.Checked, d.Checked);
        Section(d.Headings.Open, d.Open);
        return sb.ToString();
    }
}
