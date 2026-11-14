using System.Security.Cryptography;
using System.Text;
using Foreman.Core;

namespace Foreman.Recon;

/// <summary>
/// Foreman's own tool images (the code map extractor, the security scanners), built from files embedded in this
/// assembly and tagged with a hash of them: a changed Dockerfile or script is a new tag, an unchanged one is reused.
/// </summary>
public static class ToolImages
{
    /// <param name="repository">Image name without tag, e.g. <c>foreman-tools/security</c>.</param>
    /// <param name="files">Embedded resource names; each is written into the build context under its file name.</param>
    public static async Task<string> EnsureAsync(string repository, IReadOnlyList<string> files, CancellationToken ct = default)
    {
        var contents = files.Select(f => (Name: Path.GetFileName(f), Text: Resource(f))).ToList();
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\0", contents.Select(c => c.Name + "\0" + c.Text)))))[..12];
        var tag = $"{repository}:{hash}";
        if ((await Proc.RunAsync("docker", ["image", "inspect", tag], ct: ct)).Ok) return tag;

        var context = Directory.CreateTempSubdirectory("foreman-image-").FullName;
        try
        {
            foreach (var (name, text) in contents)
                File.WriteAllText(Path.Combine(context, name), text, new UTF8Encoding(false));
            var r = await Proc.RunAsync("docker", ["build", "--quiet", "-t", tag, context], timeout: TimeSpan.FromMinutes(20), ct: ct);
            if (!r.Ok) throw new InvalidOperationException($"building {repository} failed: {r.Tail(10)}");
            return tag;
        }
        finally
        {
            Dirs.Delete(context);
        }
    }

    public static string Resource(string name)
    {
        using var stream = typeof(ToolImages).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"resource {name} is not embedded");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Replace("\r\n", "\n");
    }
}
