namespace Foreman.Recon.Triage;

/// <summary>All files of a repository as relative paths with forward slashes, without dependency and build folders.</summary>
public sealed class RepoFiles
{
    private static readonly HashSet<string> SkippedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", "dist", "build", "out", "target", "vendor", "packages",
        ".venv", "venv", "env", "__pycache__", ".tox", ".nox", ".mypy_cache", ".pytest_cache", ".ruff_cache",
        ".next", ".nuxt", ".svelte-kit", ".turbo", ".cache", "coverage", ".gradle", ".idea", ".vs",
    };

    public const int MaxFiles = 200_000;

    private RepoFiles(string root, IReadOnlyList<string> files, bool truncated)
    {
        Root = root;
        All = files;
        Truncated = truncated;
    }

    public string Root { get; }
    public IReadOnlyList<string> All { get; }
    public bool Truncated { get; }

    public static RepoFiles Scan(string root)
    {
        var files = new List<string>();
        var stack = new Stack<string>([root]);
        var truncated = false;
        while (stack.Count > 0 && !truncated)
        {
            var dir = stack.Pop();
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                var info = new DirectoryInfo(sub);
                if (SkippedDirs.Contains(info.Name) || info.LinkTarget is not null) continue;
                stack.Push(sub);
            }
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                if (new FileInfo(f).LinkTarget is not null) continue;
                files.Add(Path.GetRelativePath(root, f).Replace('\\', '/'));
                if (files.Count >= MaxFiles) { truncated = true; break; }
            }
        }
        files.Sort(StringComparer.Ordinal);
        return new RepoFiles(root, files, truncated);
    }

    public string FullPath(string relative) => Path.Combine(Root, relative);

    public string Read(string relative) => File.ReadAllText(FullPath(relative));

    public bool Exists(string relative) => All.Contains(relative, StringComparer.Ordinal);

    public IEnumerable<string> WithName(string fileName) =>
        All.Where(f => string.Equals(Path.GetFileName(f), fileName, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<string> WithExtension(params string[] extensions) =>
        All.Where(f => extensions.Any(e => f.EndsWith(e, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Depth of a relative path: 0 for a file at the root.</summary>
    public static int Depth(string relative) => relative.Count(c => c == '/');
}
