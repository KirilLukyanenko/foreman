using System.Diagnostics;
using System.Text;
using Foreman.Core;
using Foreman.Recon.Triage;
using Foreman.Sandbox;

namespace Foreman.Recon.CodeMap;

public sealed record CodeMapOptions(int MapTokens = 3000, FocusMode Focus = FocusMode.Lexical, int MaxFileBytes = 512 * 1024);

public sealed record LanguageCount(string Language, int Files, int Definitions, int References, int Errors);

public sealed record FocusSummary(FocusMode Mode, IReadOnlyList<string> Files, IReadOnlyList<string> Identifiers, IReadOnlyList<string> Keywords, int KeywordFiles);

public sealed record MapSummary(string File, int Tokens, int TokenBudget, int FilesShown, int SymbolsShown, IReadOnlyList<RankedFile> TopFiles, FocusSummary? Focus);

public sealed record CodeMapReport(
    string Commit,
    int FilesParsed,
    int Definitions,
    IReadOnlyList<LanguageCount> Languages,
    IReadOnlyList<string> Generated,
    IReadOnlyList<string> Vendored,
    IReadOnlyList<string> SkippedLarge,
    IReadOnlyList<string> ParseErrors,
    IReadOnlyList<DotnetProject> Projects,
    MapSummary General,
    MapSummary? Task,
    IReadOnlyDictionary<string, string> Extractors,
    double Seconds);

/// <summary>
/// The code map, no model: definitions and references of every source file (Roslyn for C#, tree-sitter in the
/// sandbox for the rest), then two ranked maps: the repository as a whole, and personalised to the task when the job
/// has one. The index is kept (<c>index.json</c>) so later stages and tools can re-rank it for any query.
/// </summary>
public sealed class CodeMapStage(CodeMapOptions? options = null, SandboxLimits? limits = null) : IStage
{
    private readonly CodeMapOptions _options = options ?? new CodeMapOptions();

    public string Name => "codemap";

    public async Task RunAsync(Job job, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var files = RepoFiles.Scan(job.SourceDir);
        var commit = job.ReadJson<FetchResult>("fetch", "fetch.json").Commit;

        var generated = new List<string>();
        var large = new List<string>();
        var vendored = new List<string>();
        var selected = new List<(string Path, string Language)>();
        foreach (var path in files.All)
        {
            if (CodeLanguages.For(path) is not { } language) continue;
            var full = files.FullPath(path);
            if (GeneratedFiles.IsGenerated(path, p => GeneratedFiles.Head(files.FullPath(p)))) { generated.Add(path); continue; }
            if (CodePaths.IsVendored(path)) { vendored.Add(path); continue; }
            if (new FileInfo(full).Length > _options.MaxFileBytes) { large.Add(path); continue; }
            selected.Add((path, language));
        }

        var extractors = new Dictionary<string, string>();
        var csharp = selected.Where(f => f.Language == CodeLanguages.CSharp).Select(f => f.Path).ToList();
        IReadOnlyList<FileTags> csharpTags = [];
        IReadOnlyList<DotnetProject> projects = [];
        var others = selected.Where(f => f.Language != CodeLanguages.CSharp).ToList();
        if (csharp.Count > 0)
        {
            try
            {
                (csharpTags, projects) = RoslynExtractor.Extract(job.SourceDir, csharp, files.WithExtension(".csproj").ToList(), ct);
                extractors["csharp"] = "roslyn " + typeof(Microsoft.CodeAnalysis.CSharp.CSharpCompilation).Assembly.GetName().Version;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Roslyn should not fail on any input, but a map from tree-sitter beats no map.
                job.Event(Name, "roslyn_failed", new { error = e.Message });
                others.AddRange(csharp.Select(p => (p, "csharp")));
            }
        }
        var (treeSitterTags, versions) = await TreeSitterExtractor.ExtractAsync(job, job.SourceDir, others, limits, ct);
        foreach (var (k, v) in versions) extractors[k] = v;

        var index = new CodeIndex(csharpTags.Concat(treeSitterTags).OrderBy(f => f.Path, StringComparer.Ordinal).ToList());
        job.WriteJson(Name, "index.json", index);

        var general = WriteMap(job, index, TaskFocus.Empty, "repomap.md", "the repository as a whole", commit);
        MapSummary? task = null;
        if (job.Manifest.TaskFile is { } taskFile && File.Exists(Path.Combine(job.Dir, taskFile)))
        {
            var taskText = File.ReadAllText(Path.Combine(job.Dir, taskFile));
            string Read(string p) => File.ReadAllText(Path.Combine(job.SourceDir, p));
            var focus = TaskFocusFinder.Find(taskText, index, _options.Focus, Read);
            task = WriteMap(job, index, focus, "repomap-task.md", "personalised to the task", commit);
            // BM25 of the task over the files: on real issues it beat the map at finding the files a fix changes
            // (docs/codemap-eval.md), so the task reader gets it as its list of first candidates.
            var documents = index.Files.Where(f => f.Error is null).ToDictionary(f => f.Path, f => Read(f.Path), StringComparer.Ordinal);
            job.WriteJson(Name, "candidates.json", Bm25.Rank(taskText, documents).Where(r => r.Rank > 0).Take(30).ToList());
        }

        var languages = index.Files.GroupBy(f => f.Language)
            .Select(g => new LanguageCount(g.Key, g.Count(), g.Sum(f => f.Defs.Count), g.Sum(f => f.Refs.Sum(r => r.Count)), g.Count(f => f.Error is not null)))
            .OrderByDescending(l => l.Files).ToList();
        var report = new CodeMapReport(commit, index.Files.Count, index.Files.Sum(f => f.Defs.Count), languages, generated, vendored, large,
            index.Files.Where(f => f.Error is not null).Select(f => $"{f.Path}: {f.Error}").ToList(), projects, general, task, extractors,
            Math.Round(clock.Elapsed.TotalSeconds, 1));
        job.WriteJson(Name, "codemap.json", report);
        if (projects.Count > 0) job.WriteJson(Name, "projects.json", projects);
        job.Event(Name, "mapped", new { files = report.FilesParsed, definitions = report.Definitions, generated = generated.Count, generalTokens = general.Tokens, taskTokens = task?.Tokens });
    }

    private MapSummary WriteMap(Job job, CodeIndex index, TaskFocus focus, string file, string what, string commit)
    {
        var map = RepoMap.Build(index, focus, _options.MapTokens);
        var languages = string.Join(", ", index.Files.GroupBy(f => f.Language).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}"));
        var sb = new StringBuilder();
        sb.Append($"# Repository map, {what}\n\n");
        sb.Append($"Commit `{commit[..Math.Min(12, commit.Length)]}`. {index.Files.Count} files parsed ({languages}); the {map.SymbolsShown} highest-ranked ");
        sb.Append($"definitions in {map.FilesShown} files, {map.Tokens} tokens (estimated). Numbers are line numbers; `⋮` marks skipped lines. ");
        sb.Append("Generated files are left out.\n");
        if (!focus.IsEmpty)
        {
            sb.Append($"\nThe ranking starts from what the task mentions: ");
            sb.Append(string.Join("; ", new[]
            {
                focus.Files.Count > 0 ? "files " + string.Join(", ", focus.Files.Take(10).Select(f => $"`{f}`")) : null,
                focus.Identifiers.Count > 0 ? "names " + string.Join(", ", focus.Identifiers.Take(15).Select(i => $"`{i}`")) : null,
                focus.Keywords.Count > 0 ? "words " + string.Join(", ", focus.Keywords.Take(15)) : null,
            }.Where(s => s is not null)));
            sb.Append(".\n");
        }
        sb.Append("\n```text\n").Append(map.Text).Append("```\n");
        job.WriteText(Name, file, sb.ToString());

        var summary = focus.IsEmpty && focus.Mode == FocusMode.None ? null
            : new FocusSummary(focus.Mode, focus.Files.ToList(), focus.Identifiers.ToList(), focus.Keywords.Take(40).ToList(), focus.KeywordFiles.Count);
        return new MapSummary(file, map.Tokens, map.TokenBudget, map.FilesShown, map.SymbolsShown, map.Files.Take(30).ToList(), summary);
    }
}
