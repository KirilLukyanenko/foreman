using System.Text.Json;
using Foreman.Core;
using Foreman.Recon.Dossier;

namespace Foreman.Planning.Clarify;

/// <summary>
/// One gap in the task and how it is closed. <c>repository</c>: the code answers it (with evidence); <c>assume</c>:
/// Foreman goes with the default and says so; <c>ask</c>: the client is asked, and the default is what happens if
/// they just say yes.
/// </summary>
public sealed record Gap(
    string Topic,
    string What,
    string Resolution,
    string Answer,
    string Impact,
    string? Question = null,
    string? ClientDefault = null,
    IReadOnlyList<string>? Options = null,
    IReadOnlyList<Citation>? Evidence = null,
    string? Id = null);

public sealed record ClarifySubmission(string Verdict, IReadOnlyList<Gap> Gaps, IReadOnlyList<string> Warnings, int Submissions, int ToolCalls);

/// <summary>
/// Clarify's tools: the read-only repository tools (<see cref="RepoReader"/>) and <c>submit_gaps</c>. The checks
/// are the Ask-or-Assume rules in code: at most <see cref="MaxQuestions"/> questions, each with a default; a gap
/// closed by the repository must cite files and lines that exist; low-impact gaps are assumed, not asked; questions
/// are in the client's language; the verdict agrees with the questions.
/// </summary>
public sealed class ClarifyTools : IToolbox
{
    public const int MaxQuestions = 5;
    public const int MaxGaps = 20;
    public const int MaxToolCalls = 40;
    public const int MaxSubmissions = 3;

    public static readonly string[] Topics = ["behaviour", "scope", "interface", "data", "compatibility", "environment", "acceptance", "ux", "performance", "security", "delivery"];
    public static readonly string[] Resolutions = ["repository", "assume", "ask"];
    public static readonly string[] Impacts = ["high", "medium", "low"];
    public static readonly string[] Verdicts = ["clear", "underspecified"];

    private readonly Job _job;
    private readonly string _dir;
    private readonly string _language;
    private readonly RepoReader _repo;
    private bool _started;
    private int _submissions;

    public ClarifyTools(Job job, string language)
    {
        _job = job;
        _language = language;
        _repo = new RepoReader(job);
        _dir = job.StageDir("clarify");
    }

    public int ToolCalls { get; private set; }
    public ClarifySubmission? Result { get; private set; }
    public bool Finished => Result is not null;
    public string? Outcome => Finished ? "submitted" : null;
    public static string ResultFile(Job job) => Path.Combine(job.Dir, "clarify", "submission.json");

    public IReadOnlyList<ToolDefinition> Definitions => [.. RepoReader.ReadTools, SubmitGaps];

    public Task StartAsync(CancellationToken ct)
    {
        _repo.Start();
        _started = true;
        if (File.Exists(ResultFile(_job))) File.Delete(ResultFile(_job));
        return Task.CompletedTask;
    }

    public async Task<ToolOutput> CallAsync(string name, IReadOnlyDictionary<string, JsonElement> input, CancellationToken ct)
    {
        ToolOutput output;
        if (Finished) output = new ToolOutput("already submitted; stop now", true);
        else if (++ToolCalls > MaxToolCalls) output = new ToolOutput($"tool budget of {MaxToolCalls} calls is used up: call submit_gaps now", true);
        else
        {
            if (!_started) await StartAsync(ct);
            output = name == "submit_gaps" ? Submit(input) : _repo.Call(name, input);
        }
        ToolInput.Log(_dir, ToolCalls, name, input, output);
        return output;
    }

    private ToolOutput Submit(IReadOnlyDictionary<string, JsonElement> input)
    {
        _submissions++;
        var verdict = ToolInput.String(input, "verdict") ?? "";
        var gaps = ToolInput.Array<Gap>(input, "gaps");
        var errors = gaps is null ? ["gaps must be an array of gap objects (it may be empty)"] : Check(verdict, gaps, _language, _repo);
        if (errors.Count > 0 && _submissions < MaxSubmissions)
            return new ToolOutput($"NOT ACCEPTED (attempt {_submissions} of {MaxSubmissions}), fix these and call submit_gaps again:\n- " + string.Join("\n- ", errors), true);

        Result = new ClarifySubmission(verdict, Number(gaps ?? []), errors, _submissions, ToolCalls);
        _job.WriteJson("clarify", "submission.json", Result);
        return new ToolOutput(errors.Count == 0 ? "ACCEPTED. You are done: stop now." : "Kept with the problems listed as warnings. Stop now.", false);
    }

    internal static List<string> Check(string verdict, IReadOnlyList<Gap> gaps, string language, RepoReader? repo)
    {
        var errors = new List<string>();
        if (!Verdicts.Contains(verdict)) errors.Add($"verdict must be one of {string.Join(", ", Verdicts)}");
        if (gaps.Count > MaxGaps) errors.Add($"at most {MaxGaps} gaps: merge related ones");
        var asks = gaps.Where(g => g.Resolution == "ask").ToList();
        if (asks.Count > MaxQuestions) errors.Add($"{asks.Count} questions; at most {MaxQuestions}. Keep the ones whose answer changes what gets built; assume the rest and state the default");
        if (verdict == "clear" && asks.Count > 0) errors.Add("verdict is clear but there are questions: either assume them or set verdict to underspecified");
        if (verdict == "underspecified" && asks.Count == 0) errors.Add("verdict is underspecified but nothing is asked: ask what matters, or set verdict to clear");

        foreach (var (g, i) in gaps.Select((g, i) => (g, i + 1)))
        {
            var at = $"gap #{i}";
            if (!Topics.Contains(g.Topic)) errors.Add($"{at}: topic must be one of {string.Join(", ", Topics)}");
            if (!Resolutions.Contains(g.Resolution)) errors.Add($"{at}: resolution must be one of {string.Join(", ", Resolutions)}");
            if (!Impacts.Contains(g.Impact)) errors.Add($"{at}: impact must be one of {string.Join(", ", Impacts)}");
            if (string.IsNullOrWhiteSpace(g.What)) errors.Add($"{at}: what is empty");
            if (string.IsNullOrWhiteSpace(g.Answer)) errors.Add($"{at}: answer is empty: {(g.Resolution == "repository" ? "say what the code shows" : "give the default Foreman goes with")}");
            if (g.Resolution == "repository" && g.Evidence is not { Count: > 0 }) errors.Add($"{at}: resolved by the repository, so cite the files and lines that show it");
            if (g.Resolution == "ask")
            {
                if (string.IsNullOrWhiteSpace(g.Question)) errors.Add($"{at}: a question for the client is required");
                else if (!InLanguage(g.Question, language)) errors.Add($"{at}: write the question in the client's language ({language})");
                if (string.IsNullOrWhiteSpace(g.ClientDefault)) errors.Add($"{at}: client_default is required: the default as the client will read it, in their language");
                else if (!InLanguage(g.ClientDefault, language)) errors.Add($"{at}: write client_default in the client's language ({language})");
                if (g.Impact == "low") errors.Add($"{at}: low impact gaps are assumed, not asked");
                if (g.Options is { Count: > 4 }) errors.Add($"{at}: at most 4 options");
            }
            else if (!string.IsNullOrWhiteSpace(g.Question) || !string.IsNullOrWhiteSpace(g.ClientDefault)) errors.Add($"{at}: only gaps resolved by ask carry a question and a client_default");
            if (repo is null) continue;
            foreach (var c in g.Evidence ?? []) if (repo.CheckLine(c.Path, c.Line) is { } e) errors.Add($"{at} evidence {c.Path}:{c.Line}: {e}");
            foreach (var e in repo.CheckInlineReferences(g.What + "\n" + g.Answer)) errors.Add($"{at}: {e}");
        }
        return errors;
    }

    /// <summary>A cheap check for the languages where a script tells them apart: Cyrillic ones and English.</summary>
    internal static bool InLanguage(string text, string language)
    {
        var letters = text.Where(char.IsLetter).ToList();
        if (letters.Count == 0) return false;
        var cyrillic = letters.Count(c => c is >= 'Ѐ' and <= 'ӿ') / (double)letters.Count;
        return language.Split('-')[0] switch
        {
            "ru" or "uk" or "be" or "bg" or "sr" or "mk" or "kk" => cyrillic > 0.5,
            "en" => cyrillic < 0.2,
            _ => true,
        };
    }

    /// <summary>Questions first, most important first, then assumptions, then what the code answered: Q1, A1, R1.</summary>
    internal static IReadOnlyList<Gap> Number(IReadOnlyList<Gap> gaps)
    {
        int Rank(string impact) => Array.IndexOf(Impacts, impact) is var i and >= 0 ? i : 3;
        var result = new List<Gap>();
        foreach (var (resolution, prefix) in new[] { ("ask", "Q"), ("assume", "A"), ("repository", "R") })
        {
            var n = 0;
            foreach (var g in gaps.Where(g => g.Resolution == resolution).OrderBy(g => Rank(g.Impact)))
                result.Add(g with { Id = $"{prefix}{++n}", Evidence = (g.Evidence ?? []).Select(c => c with { Path = RepoReader.Clean(c.Path) }).ToList(), Options = g.Options ?? [] });
        }
        result.AddRange(gaps.Where(g => !Resolutions.Contains(g.Resolution)));
        return result;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static readonly ToolDefinition SubmitGaps = new("submit_gaps",
        $"Submit every gap you found and how each is closed. Foreman checks the evidence, the defaults, the question limit ({MaxQuestions}) and the client's language; problems come back to fix.",
        RepoReader.Schema(new
        {
            type = "object",
            properties = new
            {
                verdict = new { type = "string", @enum = Verdicts, description = "clear: a developer can build the right thing from the task and the repository; underspecified: at least one answer would change what gets built." },
                gaps = new
                {
                    type = "array",
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            topic = new { type = "string", @enum = Topics },
                            what = new { type = "string", description = "What is missing or ambiguous, in English." },
                            resolution = new { type = "string", @enum = Resolutions },
                            answer = new { type = "string", description = "repository: what the code shows. assume or ask: the default Foreman goes with, in English." },
                            impact = new { type = "string", @enum = Impacts, description = "How much a wrong guess would cost: high = wrong deliverable or a rework; low = easy to adjust in review." },
                            question = new { type = "string", description = "Only for ask: the question for the client, in the client's language, plain, one thing." },
                            client_default = new { type = "string", description = "Only for ask: the default as the client will read it, in their language: what you will do if they just reply yes." },
                            options = new { type = "array", items = new { type = "string" }, description = "Only for ask, optional: 2 to 4 short answers the client can pick from, in the client's language." },
                            evidence = RepoReader.CitationSchema,
                        },
                        required = new[] { "topic", "what", "resolution", "answer", "impact" },
                        additionalProperties = false,
                    },
                },
            },
            required = new[] { "verdict", "gaps" },
            additionalProperties = false,
        }));
}
