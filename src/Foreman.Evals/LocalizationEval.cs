using System.Globalization;
using System.Text;
using System.Text.Json;
using Foreman.Core;
using Foreman.Recon;
using Foreman.Recon.CodeMap;
using Foreman.Recon.Dossier;

namespace Foreman.Evals;

/// <summary>One real issue: the repository at the commit before its fix, and the source files the fix changed.</summary>
public sealed record LocalizationCase(
    string Id,
    string Repo,
    int Issue,
    string IssueUrl,
    string Fix,
    string BaseCommit,
    string Language,
    int SourceFilesInRepo,
    IReadOnlyList<string> GoldFiles,
    IReadOnlyList<string>? TestFilesChanged,
    string? WhyNotTrivial);

/// <summary>Where each gold file landed in one method's ranking (1-based; null = not ranked at all).</summary>
public sealed record MethodResult(string Method, IReadOnlyList<int?> GoldRanks, IReadOnlyList<bool>? GoldInMap, int? MapTokens);

public sealed record CaseResult(string Id, string Language, int FilesIndexed, IReadOnlyList<string> GoldFiles, IReadOnlyList<string> GoldNotIndexed, IReadOnlyList<MethodResult> Methods);

public sealed record MethodSummary(string Method, double RecallAt1, double RecallAt3, double RecallAt5, double RecallAt10, double HitAt5, double Mrr, double? InMap);

public sealed record LocalizationReport(DateTime At, int Cases, int MapTokens, IReadOnlyList<MethodSummary> Summary, IReadOnlyList<CaseResult> Results);

/// <summary>
/// File-level localization on real fixed issues, no model: does a ranking put the files the real fix changed near
/// the top? Compares BM25 (the SWE-bench retrieval baseline), the code map without the task, the map personalised
/// with exact mentions (Aider's signals), the map with the task's words too, and a reciprocal-rank fusion of BM25 and
/// the map. The issue text is fetched from GitHub at run time and stays in the job folder, not in this repository.
/// </summary>
public sealed class LocalizationEval(string home, int mapTokens = 3000, Action<string>? log = null, IAgentDriver? reader = null, string? readerModel = null)
{
    public static readonly JsonSerializerOptions CaseJson = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public const string Bm25Method = "bm25";
    public const string MapNone = "map, no task";
    public const string MapExact = "map + exact mentions";
    public const string MapKeywords = "map + task words";
    public const string MapLexical = "map seeded by bm25";
    public const string Hybrid = "bm25 + map (RRF)";
    public const string Reader = "task reader";
    public const string ReaderThenBm25 = "task reader, then bm25";

    public static IReadOnlyList<LocalizationCase> ReadCases(string path) =>
        JsonSerializer.Deserialize<List<LocalizationCase>>(File.ReadAllText(path), CaseJson) ?? [];

    public static string JobId(LocalizationCase c) => "eval-" + c.Id.ToLowerInvariant();

    /// <summary>Fetches, triages and maps the repository at the case's base commit; reuses a job that did it before.</summary>
    public async Task<Job> PrepareAsync(LocalizationCase c, bool force, CancellationToken ct)
    {
        var id = JobId(c);
        Job job;
        if (Directory.Exists(Path.Combine(home, "jobs", id)) && !force)
        {
            job = Job.Open(home, id);
        }
        else
        {
            if (Directory.Exists(Path.Combine(home, "jobs", id))) Dirs.Delete(Path.Combine(home, "jobs", id));
            var taskFile = Path.Combine(Path.GetTempPath(), $"foreman-{id}-task.md");
            File.WriteAllText(taskFile, await IssueTextAsync(c, ct), new UTF8Encoding(false));
            job = Job.Create(home, c.Repo, taskFile, id);
            File.Delete(taskFile);
        }

        var options = new ReconOptions(Commit: c.BaseCommit);
        var pipeline = new Pipeline([new FetchStage(options), new TriageStage(), new CodeMapStage(new CodeMapOptions(mapTokens))]);
        if (!await pipeline.RunAsync(job, log: s => log?.Invoke($"  {c.Id}: {s}"), ct: ct))
            throw new InvalidOperationException($"{c.Id}: recon failed, see {job.Dir}");
        return job;
    }

    public async Task<LocalizationReport> RunAsync(IReadOnlyList<LocalizationCase> cases, bool force, CancellationToken ct)
    {
        var results = new List<CaseResult>();
        foreach (var c in cases)
        {
            log?.Invoke($"{c.Id} ({c.Language})");
            var job = await PrepareAsync(c, force, ct);
            if (reader is not null && (force || !File.Exists(Path.Combine(job.Dir, "dossier", "task", "section.json"))))
            {
                // The task reader alone: the other sections don't change where the task lives.
                var dossier = new DossierStage(reader, new DossierOptions(readerModel ?? DossierOptions.DefaultModel, Sections: ["task"]), s => log?.Invoke($"  {c.Id}: {s.Trim()}"));
                await new Pipeline([dossier]).RunAsync(job, new HashSet<string> { "dossier" }, s => log?.Invoke($"  {c.Id}: {s}"), ct);
            }
            results.Add(Score(c, job));
        }
        return Summarise(results);
    }

    public CaseResult Score(LocalizationCase c, Job job)
    {
        var index = job.ReadJson<CodeIndex>("codemap", "index.json");
        var task = File.ReadAllText(Path.Combine(job.Dir, job.Manifest.TaskFile!));
        var indexed = index.Files.Where(f => f.Error is null).Select(f => f.Path).ToHashSet(StringComparer.Ordinal);

        var methods = new List<MethodResult>();
        var documents = indexed.ToDictionary(p => p, p => File.ReadAllText(Path.Combine(job.SourceDir, p)), StringComparer.Ordinal);
        var bm25 = Bm25.Rank(task, documents);
        methods.Add(Ranks(Bm25Method, bm25, c.GoldFiles, null));

        RepoMapResult? lexicalMap = null;
        foreach (var (name, mode) in new[] { (MapNone, FocusMode.None), (MapExact, FocusMode.Exact), (MapKeywords, FocusMode.Keywords), (MapLexical, FocusMode.Lexical) })
        {
            var map = RepoMap.Build(index, TaskFocusFinder.Find(task, index, mode, p => documents[p]), mapTokens);
            if (mode == FocusMode.Lexical) lexicalMap = map;
            methods.Add(Ranks(name, map.Files, c.GoldFiles, map));
        }
        methods.Add(Ranks(Hybrid, Fuse(bm25, lexicalMap!.Files), c.GoldFiles, null));

        var sectionFile = Path.Combine(job.Dir, "dossier", "task", "section.json");
        if (File.Exists(sectionFile))
        {
            var located = (Json.Read<DossierSection>(File.ReadAllText(sectionFile))!.Locations ?? []).Select(l => l.Path).Distinct().ToList();
            var asRanking = located.Select((p, i) => new RankedFile(p, -i)).ToList();
            methods.Add(Ranks(Reader, asRanking, c.GoldFiles, null));
            var thenBm25 = asRanking.Concat(bm25.Where(b => !located.Contains(b.Path))).ToList();
            methods.Add(Ranks(ReaderThenBm25, thenBm25, c.GoldFiles, null));
        }

        return new CaseResult(c.Id, c.Language, indexed.Count, c.GoldFiles, c.GoldFiles.Where(g => !indexed.Contains(g)).ToList(), methods);
    }

    /// <summary>Reciprocal-rank fusion (k = 60): robust to the two scores being on different scales.</summary>
    internal static IReadOnlyList<RankedFile> Fuse(params IReadOnlyList<RankedFile>[] rankings)
    {
        var score = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var ranking in rankings)
            for (var i = 0; i < ranking.Count; i++)
                score[ranking[i].Path] = score.GetValueOrDefault(ranking[i].Path) + 1.0 / (60 + i + 1);
        return score.OrderByDescending(s => s.Value).ThenBy(s => s.Key, StringComparer.Ordinal).Select(s => new RankedFile(s.Key, s.Value)).ToList();
    }

    private static MethodResult Ranks(string method, IReadOnlyList<RankedFile> ranking, IReadOnlyList<string> gold, RepoMapResult? map)
    {
        var position = ranking.Select((r, i) => (r.Path, i)).ToDictionary(x => x.Path, x => x.i + 1, StringComparer.Ordinal);
        var ranks = gold.Select(g => position.TryGetValue(g, out var p) ? p : (int?)null).ToList();
        var inMap = map is null ? null : gold.Select(g => map.Text.Split('\n').Contains(g)).ToList();
        return new MethodResult(method, ranks, inMap, map?.Tokens);
    }

    public LocalizationReport Summarise(IReadOnlyList<CaseResult> results)
    {
        // Methods every case has; the reader rows appear only when every case was read.
        var names = results.FirstOrDefault()?.Methods.Select(m => m.Method).Where(n => results.All(r => r.Methods.Any(m => m.Method == n))).ToList() ?? [];
        var summary = names.Select(name =>
        {
            var per = results.Select(r => r.Methods.First(m => m.Method == name)).ToList();
            double RecallAt(int k) => per.Average(m => m.GoldRanks.Count(r => r <= k) / (double)m.GoldRanks.Count);
            var hit5 = per.Average(m => m.GoldRanks.Any(r => r <= 5) ? 1.0 : 0.0);
            var mrr = per.Average(m => m.GoldRanks.Where(r => r is not null).Select(r => 1.0 / r!.Value).DefaultIfEmpty(0).Max());
            double? inMap = per.All(m => m.GoldInMap is not null) ? per.Average(m => m.GoldInMap!.Count(x => x) / (double)m.GoldInMap!.Count) : null;
            return new MethodSummary(name, RecallAt(1), RecallAt(3), RecallAt(5), RecallAt(10), hit5, mrr, inMap);
        }).ToList();
        return new LocalizationReport(DateTime.UtcNow, results.Count, mapTokens, summary, results);
    }

    public static string Markdown(LocalizationReport report, IReadOnlyList<LocalizationCase> cases)
    {
        string P(double v) => (v * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
        var sb = new StringBuilder();
        sb.Append($"{report.Cases} cases, map budget {report.MapTokens} tokens. Recall@k: share of the fix's source files in the top k files, averaged over cases. ");
        sb.Append("Hit@5: at least one of them in the top 5. MRR: 1 / rank of the best-placed one. In map: share shown in the rendered map.\n\n");
        sb.Append("| Method | Recall@1 | Recall@3 | Recall@5 | Recall@10 | Hit@5 | MRR | In map |\n|---|---|---|---|---|---|---|---|\n");
        foreach (var s in report.Summary)
            sb.Append($"| {s.Method} | {P(s.RecallAt1)} | {P(s.RecallAt3)} | {P(s.RecallAt5)} | {P(s.RecallAt10)} | {P(s.HitAt5)} | {s.Mrr.ToString("0.00", CultureInfo.InvariantCulture)} | {(s.InMap is { } m ? P(m) : "")} |\n");

        sb.Append("\nRank of each gold file per method (— = not ranked):\n\n| Case | Lang | Files | ");
        sb.Append(string.Join(" | ", report.Summary.Select(s => s.Method))).Append(" |\n|---|---|---|");
        sb.Append(string.Concat(report.Summary.Select(_ => "---|"))).Append('\n');
        foreach (var r in report.Results)
        {
            var c = cases.First(x => x.Id == r.Id);
            sb.Append($"| [{r.Id}]({c.IssueUrl}) | {r.Language} | {r.FilesIndexed} | ");
            sb.Append(string.Join(" | ", report.Summary.Select(s => r.Methods.First(m => m.Method == s.Method)).Select(m => string.Join(", ", m.GoldRanks.Select(x => x?.ToString(CultureInfo.InvariantCulture) ?? "—")))));
            sb.Append(" |\n");
        }
        return sb.ToString();
    }

    private static async Task<string> IssueTextAsync(LocalizationCase c, CancellationToken ct)
    {
        var repo = new Uri(c.Repo).AbsolutePath.Trim('/');
        var r = await Proc.RunAsync("gh", ["api", $"repos/{repo}/issues/{c.Issue}"], timeout: TimeSpan.FromMinutes(1), ct: ct);
        if (!r.Ok) throw new InvalidOperationException($"{c.Id}: fetching the issue failed: {r.Tail(3)}");
        using var doc = JsonDocument.Parse(r.Stdout);
        var title = doc.RootElement.GetProperty("title").GetString();
        var body = doc.RootElement.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : "";
        return $"# {title}\n\n{body}\n";
    }
}
