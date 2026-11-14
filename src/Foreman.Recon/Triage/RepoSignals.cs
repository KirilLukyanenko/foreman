using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Foreman.Recon.Triage;

/// <summary>Instruction files, install-time code and environment definitions found in a repository.</summary>
public static partial class RepoSignals
{
    private static readonly (Func<string, bool> Match, string Kind)[] InstructionKinds =
    [
        (f => f.EndsWith("AGENTS.md", StringComparison.OrdinalIgnoreCase), "agents-md"),
        (f => f.EndsWith("CLAUDE.md", StringComparison.OrdinalIgnoreCase) || f.EndsWith("CLAUDE.local.md", StringComparison.OrdinalIgnoreCase), "claude-md"),
        (f => f.StartsWith(".cursor/rules/", StringComparison.Ordinal) || f == ".cursorrules", "cursor-rules"),
        (f => f == ".github/copilot-instructions.md" || f.StartsWith(".github/instructions/", StringComparison.Ordinal), "copilot-instructions"),
        (f => f == ".windsurfrules" || f.StartsWith(".windsurf/rules/", StringComparison.Ordinal), "windsurf-rules"),
        (f => f.StartsWith(".claude/", StringComparison.Ordinal) && f.EndsWith(".md", StringComparison.Ordinal), "claude-skill-or-agent"),
        (f => RepoFiles.Depth(f) <= 1 && Path.GetFileName(f).StartsWith("CONTRIBUTING", StringComparison.OrdinalIgnoreCase), "contributing"),
        (f => RepoFiles.Depth(f) == 0 && Path.GetFileName(f).StartsWith("README", StringComparison.OrdinalIgnoreCase), "readme"),
        (f => Path.GetFileName(f) == ".editorconfig" && RepoFiles.Depth(f) == 0, "editorconfig"),
    ];

    /// <summary>Agent configuration that could change how an agent behaves or run code when the repo is opened.</summary>
    public static readonly string[] AgentConfigPaths = [".claude", ".mcp.json", ".cursor/mcp.json", ".vscode/mcp.json", ".gemini", ".codex"];

    private static readonly string[] EnvironmentFileNames =
        ["Dockerfile", "docker-compose.yml", "docker-compose.yaml", "compose.yml", "compose.yaml", ".tool-versions", "mise.toml", ".mise.toml", "flake.nix", "shell.nix", "Makefile", "justfile", "Taskfile.yml"];

    public static IReadOnlyList<InstructionFile> InstructionFiles(RepoFiles files)
    {
        var result = new List<InstructionFile>();
        foreach (var f in files.All)
        {
            var kind = InstructionKinds.FirstOrDefault(k => k.Match(f)).Kind;
            if (kind is null) continue;
            var bytes = File.ReadAllBytes(files.FullPath(f));
            var text = System.Text.Encoding.UTF8.GetString(bytes);
            result.Add(new InstructionFile(f, kind, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)), CountHidden(text)));
        }
        return result;
    }

    /// <summary>Zero-width, bidirectional-override and Unicode tag characters: invisible to a reviewer, visible to a model.</summary>
    internal static int CountHidden(string text)
    {
        var count = 0;
        for (var i = 0; i < text.Length; i++)
        {
            int cp = text[i];
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                cp = char.ConvertToUtf32(text[i], text[i + 1]);
                i++;
            }
            if (cp is >= 0x200B and <= 0x200F or >= 0x202A and <= 0x202E or >= 0x2060 and <= 0x2064 or >= 0x2066 and <= 0x2069
                or >= 0xE0000 and <= 0xE007F || cp == 0xFEFF && i > 0)
                count++;
        }
        return count;
    }

    public static IReadOnlyList<InstallScript> InstallScripts(RepoFiles files)
    {
        var result = new List<InstallScript>();
        foreach (var pj in files.WithName("package.json"))
        {
            try
            {
                using var doc = JsonDocument.Parse(files.Read(pj));
                if (doc.RootElement.TryGetProperty("scripts", out var s) && s.ValueKind == JsonValueKind.Object)
                    foreach (var name in new[] { "preinstall", "install", "postinstall", "prepare" })
                        if (s.TryGetProperty(name, out var v))
                            result.Add(new InstallScript(pj, $"npm-{name}", v.GetString() ?? ""));
            }
            catch (JsonException) { }
        }
        foreach (var setup in files.WithName("setup.py"))
            result.Add(new InstallScript(setup, "python-setup-py", "runs as Python code when the package is installed"));
        foreach (var f in files.WithExtension(".csproj", ".fsproj", ".props", ".targets"))
        {
            var m = MsbuildExecRegex().Match(files.Read(f));
            if (m.Success) result.Add(new InstallScript(f, "msbuild-exec", m.Groups[1].Value));
        }
        return result;
    }

    public static IReadOnlyList<string> EnvironmentFiles(RepoFiles files) =>
        files.All.Where(f =>
                f.StartsWith(".devcontainer/", StringComparison.Ordinal) && f.EndsWith(".json", StringComparison.Ordinal)
                || f == ".devcontainer.json"
                || RepoFiles.Depth(f) <= 1 && EnvironmentFileNames.Contains(Path.GetFileName(f), StringComparer.Ordinal))
            .ToList();

    [GeneratedRegex(@"<Exec\s[^>]*Command=""([^""]{0,200})", RegexOptions.IgnoreCase)]
    private static partial Regex MsbuildExecRegex();
}
