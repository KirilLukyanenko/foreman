using System.Text;
using System.Text.RegularExpressions;
using Foreman.Core;
using Foreman.Recon.Baseline;
using Foreman.Recon.CodeMap;
using Foreman.Recon.Environment;
using Foreman.Recon.Triage;

namespace Foreman.Recon.Dossier;

/// <summary>
/// The rules file for an implementer, kept outside the client's repository: only facts Foreman verified or read from
/// configuration, and nothing a capable agent would do anyway. An ETH study (arXiv 2602.11988) found long generated
/// context files lowered task success and raised cost by over 20%; the exact commands, the tests that already fail,
/// the files not to touch and the configured conventions are what such files are good for. Everything else is in
/// the dossier, read on demand.
/// </summary>
public static partial class RulesFile
{
    private static readonly string[] LockFiles =
        ["package-lock.json", "pnpm-lock.yaml", "yarn.lock", "bun.lockb", "poetry.lock", "uv.lock", "Pipfile.lock", "Cargo.lock", "go.sum", "packages.lock.json", "composer.lock", "Gemfile.lock"];

    private static readonly string[] EditorConfigKeys = ["indent_style", "indent_size", "end_of_line", "charset", "insert_final_newline", "max_line_length"];

    public static string Build(Job job, DossierSection? task)
    {
        var files = RepoFiles.Scan(job.SourceDir);
        var triage = job.ReadJson<TriageReport>("triage", "triage.json");
        var sb = new StringBuilder();
        sb.Append($"# Working in this repository\n\nFacts Foreman verified for `{job.Manifest.Source}`. Background, when you need it: `dossier.md`.\n");

        // Commands: only an environment that was proven in a fresh copy.
        sb.Append("\n## Build and test\n\n");
        var environment = File.Exists(Path.Combine(job.Dir, "environment", "environment.json"))
            ? job.ReadJson<EnvironmentReport>("environment", "environment.json") : null;
        var ready = environment?.Stacks.Where(s => s.Ready && s.Spec is not null).ToList() ?? [];
        if (ready.Count == 0)
            sb.Append("- No environment has been verified yet: don't trust a test run you set up by hand as the baseline.\n");
        foreach (var s in ready)
        {
            sb.Append($"- {s.Stack}: image `{s.Spec!.Image}`; setup steps: {string.Join(", ", s.Spec.Setup.Select(x => x.Name))} (full scripts in `environment/environment.json`). Tests, network off:\n");
            sb.Append("  ```bash\n");
            foreach (var line in s.Spec.TestScript.Split('\n').Where(l => l.Trim().Length > 0).Take(12)) sb.Append("  ").Append(line.TrimEnd()).Append('\n');
            sb.Append("  ```\n");
        }

        if (File.Exists(Path.Combine(job.Dir, "baseline", "baseline.json")))
        {
            sb.Append("\n## Tests before any change\n\n");
            foreach (var s in job.ReadJson<BaselineReport>("baseline", "baseline.json").Stacks)
            {
                if (s.Counts is not { } c) { sb.Append($"- {s.Stack}: {s.Status}{(s.Reason is null ? "" : $": {s.Reason}")}\n"); continue; }
                sb.Append($"- {s.Stack}: {c.Total} tests, {c.Pass} pass in all runs. A change must keep those passing.\n");
                var tests = s.TestsFile is not null && File.Exists(Path.Combine(job.Dir, "baseline", s.TestsFile))
                    ? Json.Read<List<TestBaseline>>(File.ReadAllText(Path.Combine(job.Dir, "baseline", s.TestsFile))) ?? [] : [];
                AppendTests(sb, "Known-red (fail before any change; not caused by you)", tests.Where(t => t.Verdict == Verdict.KnownRed).Select(t => t.Id).ToList(), c.KnownRed);
                foreach (var note in environment?.Stacks.FirstOrDefault(e => e.Stack == s.Stack)?.Spec?.Notes.Where(n => n.StartsWith("expected failures", StringComparison.Ordinal)) ?? [])
                    sb.Append($"    Why, from the environment: {note["expected failures: ".Length..]}\n");
                AppendTests(sb, "Flaky (results differ between runs; not evidence either way)", tests.Where(t => t.Verdict == Verdict.Flaky).Select(t => t.Id).ToList(), c.Flaky);
            }
        }

        sb.Append("\n## Don't edit by hand\n\n");
        var codemap = File.Exists(Path.Combine(job.Dir, "codemap", "codemap.json")) ? job.ReadJson<CodeMapReport>("codemap", "codemap.json") : null;
        var any = false;
        if (codemap is { Generated.Count: > 0 })
        {
            sb.Append($"- Generated ({codemap.Generated.Count}): {string.Join(", ", codemap.Generated.Take(8).Select(f => $"`{f}`"))}{(codemap.Generated.Count > 8 ? ", …" : "")}. Change the generator's input instead.\n");
            any = true;
        }
        if (codemap is { Vendored.Count: > 0 })
        {
            var folders = codemap.Vendored.Select(f => f[..(f.LastIndexOf('/') + 1)]).Distinct().Take(5);
            sb.Append($"- Vendored code: {string.Join(", ", folders.Select(f => $"`{f}`"))}.\n");
            any = true;
        }
        var locks = files.All.Where(f => LockFiles.Contains(Path.GetFileName(f))).Take(6).ToList();
        if (locks.Count > 0)
        {
            sb.Append($"- Lockfiles: {string.Join(", ", locks.Select(f => $"`{f}`"))}: change them only through the package manager.\n");
            any = true;
        }
        if (!any) sb.Append("- Nothing found that is generated, vendored or a lockfile.\n");

        var conventions = Conventions(files, triage);
        if (conventions.Count > 0)
        {
            sb.Append("\n## Conventions from configuration\n\n");
            foreach (var c in conventions) sb.Append("- ").Append(c).Append('\n');
        }

        if (task?.Locations is { Count: > 0 } locations)
        {
            sb.Append("\n## Where the task is\n\n");
            foreach (var l in locations.Take(5))
                sb.Append($"- `{l.Path}`{(l.Symbols.Count == 0 ? "" : ": " + string.Join(", ", l.Symbols.Take(6).Select(s => $"`{s}`")))} ({l.Confidence})\n");
            sb.Append("  Details and open questions: the task section of `dossier.md`.\n");
        }
        return sb.ToString();
    }

    /// <summary>
    /// A few names, or, when there are many, where they are: "377, all in `compledger.integrationtests`" says more in
    /// one line than ten long test names.
    /// </summary>
    internal static void AppendTests(StringBuilder sb, string label, IReadOnlyList<string> names, int count)
    {
        if (count == 0) return;
        sb.Append($"  - {label}: {count}");
        if (names.Count is > 0 and <= 5)
        {
            sb.Append(": ").Append(string.Join("; ", names.Select(n => $"`{n}`")));
        }
        else if (names.Count > 5)
        {
            var groups = names.GroupBy(Group).OrderByDescending(g => g.Count()).ToList();
            sb.Append(groups.Count == 1
                ? $", all in `{groups[0].Key}`"
                : ": " + string.Join(", ", groups.Take(4).Select(g => $"{g.Count()} in `{g.Key}`")) + (groups.Count > 4 ? $", {groups.Count - 4} more groups" : ""));
            sb.Append("; full list in `baseline/`");
        }
        sb.Append('\n');
    }

    /// <summary>The test's project or module: the part before <c>::</c>, or the dotted name without its last two parts.</summary>
    private static string Group(string id)
    {
        var sep = id.IndexOf("::", StringComparison.Ordinal);
        if (sep > 0) return id[..sep];
        var parts = id.Split('.', '/');
        return parts.Length > 2 ? string.Join('.', parts[..^2]) : id;
    }

    internal static List<string> Conventions(RepoFiles files, TriageReport triage)
    {
        var result = new List<string>();
        var main = triage.Languages.FirstOrDefault()?.Language;
        if (files.Exists(".editorconfig"))
        {
            var settings = EditorConfig(files.Read(".editorconfig"), MainExtension(main));
            if (settings.Count > 0) result.Add($".editorconfig: {string.Join(", ", settings.Select(kv => $"{kv.Key} = {kv.Value}"))}");
        }
        foreach (var props in files.All.Where(f => Path.GetFileName(f) == "Directory.Build.props" && RepoFiles.Depth(f) <= 1))
        {
            var text = files.Read(props);
            var found = new[] { "Nullable", "TreatWarningsAsErrors", "LangVersion", "ImplicitUsings", "AnalysisLevel", "EnforceCodeStyleInBuild" }
                .Select(k => (k, Regex.Match(text, $@"<{k}>\s*([^<]+?)\s*</{k}>")))
                .Where(x => x.Item2.Success).Select(x => $"{x.k} = {x.Item2.Groups[1].Value}").ToList();
            if (found.Count > 0) result.Add($"`{props}`: {string.Join(", ", found)} (applies to every project)");
        }
        var linters = files.All.Where(f => RepoFiles.Depth(f) <= 1 && LinterConfig().IsMatch(Path.GetFileName(f))).Take(8).ToList();
        if (files.Exists("pyproject.toml"))
        {
            var py = files.Read("pyproject.toml");
            foreach (var tool in new[] { "ruff", "black", "isort", "mypy", "pyright", "pylint", "flake8" })
                if (py.Contains($"[tool.{tool}", StringComparison.Ordinal)) linters.Add($"pyproject.toml [tool.{tool}]");
        }
        if (linters.Count > 0) result.Add($"Formatters and linters configured: {string.Join(", ", linters.Select(l => $"`{l}`"))}; run them on what you change.");
        if (triage.Git.ConventionalCommitShare is { } share && triage.Git.CommitsSeen >= 10)
            result.Add(share >= 0.6
                ? $"Commit messages follow Conventional Commits ({share * 100:0}% of the last {triage.Git.CommitsSeen})."
                : $"Commit messages are free-form (Conventional Commits in {share * 100:0}% of the last {triage.Git.CommitsSeen}).");
        return result;
    }

    /// <summary>Settings from the <c>[*]</c> section and any section that names the main language's extension.</summary>
    internal static Dictionary<string, string> EditorConfig(string text, string? extension)
    {
        var settings = new Dictionary<string, string>();
        var applies = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';') continue;
            if (line.StartsWith('['))
            {
                var section = line.Trim('[', ']');
                applies = section == "*" || (extension is not null && section.Contains(extension, StringComparison.OrdinalIgnoreCase));
                continue;
            }
            var eq = line.IndexOf('=');
            if (!applies || eq < 0) continue;
            var key = line[..eq].Trim().ToLowerInvariant();
            if (EditorConfigKeys.Contains(key)) settings[key] = line[(eq + 1)..].Trim();
        }
        return settings;
    }

    private static string? MainExtension(string? language) => language switch
    {
        "C#" => "cs",
        "TypeScript" => "ts",
        "JavaScript" => "js",
        "Python" => "py",
        "Go" => "go",
        "Rust" => "rs",
        _ => null,
    };

    [GeneratedRegex(@"^(\.eslintrc.*|eslint\.config\..*|\.prettierrc.*|prettier\.config\..*|biome\.jsonc?|ruff\.toml|\.ruff\.toml|\.flake8|mypy\.ini|\.pylintrc|\.golangci\.ya?ml|rustfmt\.toml|\.rustfmt\.toml|clippy\.toml|\.rubocop\.yml|stylecop\.json|\.globalconfig|.*\.ruleset|\.stylelintrc.*|deno\.jsonc?)$", RegexOptions.IgnoreCase)]
    private static partial Regex LinterConfig();
}
