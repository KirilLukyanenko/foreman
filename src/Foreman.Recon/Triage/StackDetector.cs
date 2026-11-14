using System.Text.Json;
using System.Text.RegularExpressions;

namespace Foreman.Recon.Triage;

/// <summary>Detects the build stacks of a repository from its files alone. Nothing is executed.</summary>
public static partial class StackDetector
{
    private static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private static readonly (string File, string Stack)[] Unsupported =
    [
        ("go.mod", "go"), ("Cargo.toml", "rust"), ("pom.xml", "java-maven"), ("build.gradle", "jvm-gradle"),
        ("build.gradle.kts", "jvm-gradle"), ("Gemfile", "ruby"), ("composer.json", "php"), ("mix.exs", "elixir"),
        ("Package.swift", "swift"), ("CMakeLists.txt", "cmake"), ("pubspec.yaml", "dart"),
    ];

    private static readonly Dictionary<string, string> LanguageByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = "C#", [".fs"] = "F#", [".vb"] = "VB.NET", [".razor"] = "Razor", [".cshtml"] = "Razor",
        [".ts"] = "TypeScript", [".tsx"] = "TypeScript", [".mts"] = "TypeScript", [".cts"] = "TypeScript",
        [".js"] = "JavaScript", [".jsx"] = "JavaScript", [".mjs"] = "JavaScript", [".cjs"] = "JavaScript",
        [".vue"] = "Vue", [".svelte"] = "Svelte", [".py"] = "Python", [".go"] = "Go", [".rs"] = "Rust",
        [".java"] = "Java", [".kt"] = "Kotlin", [".rb"] = "Ruby", [".php"] = "PHP", [".swift"] = "Swift",
        [".c"] = "C", [".h"] = "C", [".cpp"] = "C++", [".hpp"] = "C++", [".sql"] = "SQL", [".sh"] = "Shell",
        [".ps1"] = "PowerShell", [".css"] = "CSS", [".scss"] = "CSS", [".html"] = "HTML", [".dart"] = "Dart",
    };

    public static IReadOnlyList<LanguageShare> Languages(RepoFiles files) =>
        files.All
            .Select(f => LanguageByExtension.GetValueOrDefault(Path.GetExtension(f)))
            .Where(l => l is not null)
            .GroupBy(l => l!)
            .Select(g => new LanguageShare(g.Key, g.Count()))
            .OrderByDescending(l => l.Files)
            .Take(10)
            .ToList();

    public static IReadOnlyList<string> UnsupportedStacks(RepoFiles files) =>
        Unsupported
            .Where(u => files.WithName(u.File).Any(f => RepoFiles.Depth(f) <= 2))
            .Select(u => u.Stack)
            .Distinct()
            .ToList();

    // ---------------------------------------------------------------- .NET

    public static DotnetStack? Dotnet(RepoFiles files)
    {
        var projects = files.WithExtension(".csproj", ".fsproj", ".vbproj").ToList();
        if (projects.Count == 0) return null;

        var solutions = files.WithExtension(".sln", ".slnx").OrderBy(RepoFiles.Depth).ThenBy(f => f, StringComparer.Ordinal).ToList();
        var testProjects = new List<string>();
        var frameworks = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var testFrameworks = new SortedSet<string>(StringComparer.Ordinal);

        var buildFiles = projects.Concat(files.WithName("Directory.Build.props")).Concat(files.WithName("Directory.Packages.props"));
        foreach (var f in buildFiles)
        {
            var text = files.Read(f);
            foreach (Match m in TargetFrameworkRegex().Matches(text))
                foreach (var tf in m.Groups[1].Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    if (!tf.Contains('$')) frameworks.Add(tf);
            if (!projects.Contains(f)) continue;

            var found = TestFrameworksIn(text);
            if (found.Count > 0 || IsTestProjectRegex().IsMatch(text))
            {
                testProjects.Add(f);
                testFrameworks.UnionWith(found);
            }
        }

        string? sdk = null;
        var runner = "vstest";
        var globalJson = files.All.FirstOrDefault(f => f.Equals("global.json", StringComparison.OrdinalIgnoreCase));
        if (globalJson is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(files.Read(globalJson), Lenient);
                if (doc.RootElement.TryGetProperty("sdk", out var s) && s.TryGetProperty("version", out var v)) sdk = v.GetString();
                if (doc.RootElement.TryGetProperty("test", out var t) && t.TryGetProperty("runner", out var r)
                    && string.Equals(r.GetString(), "Microsoft.Testing.Platform", StringComparison.OrdinalIgnoreCase))
                    runner = "mtp";
            }
            catch (JsonException) { }
        }

        return new DotnetStack(
            solutions, projects, testProjects, frameworks.ToList(), sdk, runner, testFrameworks.ToList(),
            files.WithName("Directory.Packages.props").Any());
    }

    internal static List<string> TestFrameworksIn(string project)
    {
        var found = new List<string>();
        if (Regex.IsMatch(project, @"""xunit\.v3", RegexOptions.IgnoreCase)) found.Add("xunit.v3");
        else if (Regex.IsMatch(project, @"""xunit[""\.]", RegexOptions.IgnoreCase)) found.Add("xunit");
        if (Regex.IsMatch(project, @"""NUnit[""\d]", RegexOptions.IgnoreCase)) found.Add("nunit");
        if (Regex.IsMatch(project, @"MSTest", RegexOptions.IgnoreCase)) found.Add("mstest");
        if (Regex.IsMatch(project, @"""TUnit[""\.]", RegexOptions.IgnoreCase)) found.Add("tunit");
        return found;
    }

    // ---------------------------------------------------------------- Node

    public static NodeStack? Node(RepoFiles files)
    {
        var packageJson = files.WithName("package.json")
            .Where(f => RepoFiles.Depth(f) <= 2)
            .OrderBy(RepoFiles.Depth).ThenBy(f => f, StringComparer.Ordinal)
            .FirstOrDefault();
        if (packageJson is null) return null;

        var dir = Path.GetDirectoryName(packageJson)?.Replace('\\', '/') ?? "";
        string In(string name) => dir.Length == 0 ? name : $"{dir}/{name}";

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(files.Read(packageJson), Lenient);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return new NodeStack(packageJson, "npm", null, null, null, null, false);
        }

        string? lockfile = null;
        string? manager = null;
        foreach (var (file, pm) in new[] { ("pnpm-lock.yaml", "pnpm"), ("yarn.lock", "yarn"), ("bun.lock", "bun"), ("bun.lockb", "bun"), ("package-lock.json", "npm"), ("npm-shrinkwrap.json", "npm") })
        {
            if (files.Exists(In(file)) || files.Exists(file)) { lockfile = files.Exists(In(file)) ? In(file) : file; manager = pm; break; }
        }
        if (root.TryGetProperty("packageManager", out var pmField) && pmField.GetString() is { } declared)
            manager = declared.Split('@')[0];
        manager ??= "npm";

        string? nodeVersion = null;
        foreach (var f in new[] { In(".nvmrc"), In(".node-version"), ".nvmrc", ".node-version" })
            if (files.Exists(f)) { nodeVersion = files.Read(f).Trim(); break; }
        if (nodeVersion is null && root.TryGetProperty("engines", out var engines) && engines.TryGetProperty("node", out var en))
            nodeVersion = en.GetString();

        string? testScript = null;
        if (root.TryGetProperty("scripts", out var scripts) && scripts.ValueKind == JsonValueKind.Object && scripts.TryGetProperty("test", out var ts))
            testScript = ts.GetString();

        var deps = new HashSet<string>(StringComparer.Ordinal);
        foreach (var section in new[] { "dependencies", "devDependencies" })
            if (root.TryGetProperty(section, out var d) && d.ValueKind == JsonValueKind.Object)
                foreach (var p in d.EnumerateObject()) deps.Add(p.Name);

        string? framework = null;
        foreach (var fw in new[] { "vitest", "jest", "mocha", "ava", "jasmine" })
            if (deps.Contains(fw) || (testScript?.Contains(fw, StringComparison.Ordinal) ?? false)) { framework = fw; break; }
        if (framework is null && testScript is not null && testScript.Contains("node --test", StringComparison.Ordinal)) framework = "node";

        var workspaces = root.TryGetProperty("workspaces", out _) || files.Exists(In("pnpm-workspace.yaml"));
        return new NodeStack(packageJson, manager, lockfile, nodeVersion, testScript, framework, workspaces);
    }

    // ---------------------------------------------------------------- Python

    public static PythonStack? Python(RepoFiles files)
    {
        var manifests = files.All
            .Where(f => RepoFiles.Depth(f) <= 1)
            .Where(f => Path.GetFileName(f) is "pyproject.toml" or "setup.py" or "setup.cfg" or "Pipfile"
                        || RequirementsRegex().IsMatch(Path.GetFileName(f)))
            .ToList();
        if (manifests.Count == 0 || !files.WithExtension(".py").Any()) return null;

        var pyproject = files.Exists("pyproject.toml") ? files.Read("pyproject.toml") : "";
        var manager =
            files.Exists("uv.lock") ? "uv" :
            files.Exists("poetry.lock") || pyproject.Contains("[tool.poetry]", StringComparison.Ordinal) ? "poetry" :
            files.Exists("pdm.lock") ? "pdm" :
            files.Exists("Pipfile") ? "pipenv" : "pip";

        string? version = files.Exists(".python-version") ? files.Read(".python-version").Trim() : null;
        if (version is null && RequiresPythonRegex().Match(pyproject) is { Success: true } rp) version = rp.Groups[1].Value;

        var testSignals = string.Join('\n', manifests.Select(files.Read))
            + (files.Exists("tox.ini") ? files.Read("tox.ini") : "")
            + (files.Exists("pytest.ini") ? "pytest" : "");
        string? framework =
            testSignals.Contains("pytest", StringComparison.OrdinalIgnoreCase) || files.WithName("conftest.py").Any() ? "pytest" :
            files.All.Any(f => TestFileRegex().IsMatch(Path.GetFileName(f))) ? "unittest" : null;

        // Extras (pip install .[tests]) and dependency groups (PEP 735, uv sync --group tests) install differently.
        // TOML allows both a table ([project.optional-dependencies] tests = ...) and a dotted key inside [project]
        // (optional-dependencies.tests = ...).
        List<string> TestKeys(string table, string dottedPrefix) =>
            TomlKeyRegex().Matches(TomlSection(pyproject, table)).Select(m => m.Groups[1].Value)
                .Concat(Regex.Matches(pyproject, $@"^\s*{Regex.Escape(dottedPrefix)}\.([A-Za-z0-9_-]+)\s*=", RegexOptions.Multiline).Select(m => m.Groups[1].Value))
                .Where(k => TestExtraRegex().IsMatch(k)).Distinct().ToList();

        return new PythonStack(
            manifests, manager, version, framework,
            manifests.Where(f => RequirementsRegex().IsMatch(Path.GetFileName(f)) && IsInstallableRequirements(f)).ToList(),
            TestKeys("project.optional-dependencies", "optional-dependencies"),
            TestKeys("dependency-groups", "dependency-groups"));
    }

    /// <summary>Requirements for running the code or its tests; docs, lint and type-check sets are left out.</summary>
    internal static bool IsInstallableRequirements(string path) =>
        !path.StartsWith("docs/", StringComparison.OrdinalIgnoreCase)
        && !Regex.IsMatch(Path.GetFileName(path), "doc|lint|mypy|typing|type|format|style|release|pre-commit", RegexOptions.IgnoreCase);

    /// <summary>The body of a TOML table, up to the next table header. Enough for key names; not a TOML parser.</summary>
    internal static string TomlSection(string toml, string table)
    {
        var header = $"[{table}]";
        var start = toml.IndexOf(header, StringComparison.Ordinal);
        if (start < 0) return "";
        start += header.Length;
        var next = Regex.Match(toml[start..], @"^\s*\[", RegexOptions.Multiline);
        return next.Success ? toml.Substring(start, next.Index) : toml[start..];
    }

    [GeneratedRegex(@"<TargetFrameworks?>\s*([^<]+?)\s*</TargetFrameworks?>", RegexOptions.IgnoreCase)]
    private static partial Regex TargetFrameworkRegex();

    [GeneratedRegex(@"Microsoft\.NET\.Test\.Sdk|<IsTestProject>\s*true|Sdk=""MSTest\.Sdk", RegexOptions.IgnoreCase)]
    private static partial Regex IsTestProjectRegex();

    [GeneratedRegex(@"^requirements([-_.][\w.-]+)?\.(txt|in)$", RegexOptions.IgnoreCase)]
    private static partial Regex RequirementsRegex();

    [GeneratedRegex(@"requires-python\s*=\s*[""']([^""']+)[""']")]
    private static partial Regex RequiresPythonRegex();

    [GeneratedRegex(@"^(test_.+|.+_test)\.py$")]
    private static partial Regex TestFileRegex();

    [GeneratedRegex(@"^\s*([A-Za-z0-9_.-]+)\s*=", RegexOptions.Multiline)]
    private static partial Regex TomlKeyRegex();

    [GeneratedRegex(@"^(test|tests|testing|dev)$", RegexOptions.IgnoreCase)]
    private static partial Regex TestExtraRegex();
}
