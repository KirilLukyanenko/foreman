using System.Text;
using System.Text.Json;
using Foreman.Core;
using Foreman.Sandbox;

namespace Foreman.Recon.CodeMap;

/// <summary>
/// Definitions and references for every language but C#, from tree-sitter's tags queries, run in the sandbox: the
/// selected source files are copied into a volume of their own and parsed by Foreman's extractor image as the
/// unprivileged user, with no network. Parsers are native code fed with untrusted input, so they stay off the host.
/// </summary>
public static class TreeSitterExtractor
{
    public const string ImageRepository = "foreman-tools/codemap";

    /// <summary>The extractor image, tagged with a hash of its Dockerfile and script; built on first use.</summary>
    public static Task<string> EnsureImageAsync(CancellationToken ct = default) =>
        ToolImages.EnsureAsync(ImageRepository, ["codemap/Dockerfile", "codemap/extract.py"], ct);

    public static async Task<(IReadOnlyList<FileTags> Files, IReadOnlyDictionary<string, string> Versions)> ExtractAsync(
        Job job, string sourceDir, IReadOnlyList<(string Path, string Language)> files, SandboxLimits? limits, CancellationToken ct)
    {
        if (files.Count == 0) return ([], new Dictionary<string, string>());
        var image = await EnsureImageAsync(ct);

        var stage = job.StageDir("codemap");
        var staging = Path.Combine(stage, "input");
        var output = Path.Combine(stage, "extract");
        Dirs.Delete(staging);
        Dirs.Delete(output);
        foreach (var (path, _) in files)
        {
            var target = Path.Combine(staging, "src", path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(sourceDir, path), target);
        }
        File.WriteAllText(Path.Combine(staging, "files.json"), JsonSerializer.Serialize(files.Select(f => new[] { f.Path, f.Language })), new UTF8Encoding(false));

        var sandbox = new DockerSandbox(job.Id, limits, "codemap");
        try
        {
            await sandbox.PopulateAsync(staging, image, []);
            var r = await sandbox.RunAsync(new SandboxRun(image, "python /opt/codemap/extract.py \"$OUT/tags.jsonl\"", Network.None, TimeSpan.FromMinutes(30)), output, ct);
            if (!r.Ok) throw new InvalidOperationException($"the code map extractor failed: {r.Tail(10)}");
        }
        finally
        {
            await sandbox.RemoveAsync();
            Dirs.Delete(staging);
        }

        var tags = File.ReadLines(Path.Combine(output, "tags.jsonl")).Where(l => l.Length > 0).Select(Parse).ToList();
        var versionsFile = Path.Combine(output, "tags.jsonl.versions.json");
        var versions = File.Exists(versionsFile) ? Json.Read<Dictionary<string, string>>(File.ReadAllText(versionsFile)) ?? [] : [];
        versions["image"] = image;
        return (tags, versions);
    }

    internal static FileTags Parse(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var e = doc.RootElement;
        var defs = e.GetProperty("defs").EnumerateArray().Select(d => new CodeSymbol(
            d[0].GetString()!, d[1].GetString()!, d[2].GetInt32(), d[3].GetInt32(), d[4].GetString()!)).ToList();
        var refs = e.GetProperty("refs").EnumerateObject().Select(p => new CodeRef(p.Name, p.Value.GetInt32())).ToList();
        return new FileTags(e.GetProperty("path").GetString()!, e.GetProperty("lang").GetString()!, e.GetProperty("lines").GetInt32(), defs, refs,
            e.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String ? err.GetString() : null);
    }
}
