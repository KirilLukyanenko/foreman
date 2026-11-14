namespace Foreman.Tests;

/// <summary>A throwaway folder with the given files, for detector tests.</summary>
internal sealed class TempRepo : IDisposable
{
    public TempRepo(params (string Path, string Content)[] files)
    {
        Root = Path.Combine(Path.GetTempPath(), "foreman-tests", Guid.NewGuid().ToString("N"));
        foreach (var (path, content) in files)
        {
            var full = Path.Combine(Root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public void Dispose()
    {
        try { Foreman.Core.Dirs.Delete(Root); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
