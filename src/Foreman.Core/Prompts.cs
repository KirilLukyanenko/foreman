using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Foreman.Core;

public sealed record Prompt(string Name, string Text, string Sha256);

/// <summary>
/// Prompts are files under <c>prompts/</c>, embedded in the assembly. Every run records the hash of the exact text it
/// used, so a change in results can be traced to a change in a prompt.
/// </summary>
public static class Prompts
{
    public static Prompt Load(string name, IReadOnlyDictionary<string, string>? values = null)
    {
        var assembly = typeof(Prompts).Assembly;
        using var stream = assembly.GetManifestResourceStream($"prompts/{name}.md")
            ?? throw new InvalidOperationException($"prompt '{name}' is not embedded; resources: {string.Join(", ", assembly.GetManifestResourceNames())}");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var text = reader.ReadToEnd().Replace("\r\n", "\n");
        foreach (var (k, v) in values ?? new Dictionary<string, string>())
            text = text.Replace("{{" + k + "}}", v, StringComparison.Ordinal);
        return new Prompt(name, text, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))));
    }
}
