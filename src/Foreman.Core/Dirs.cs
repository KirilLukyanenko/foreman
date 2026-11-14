namespace Foreman.Core;

public static class Dirs
{
    /// <summary>
    /// Deletes a folder tree, including read-only files: git marks its object files read-only, and on Windows
    /// <see cref="Directory.Delete(string, bool)"/> refuses to remove them.
    /// </summary>
    public static void Delete(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            var attributes = File.GetAttributes(f);
            if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(f, attributes & ~FileAttributes.ReadOnly);
        }
        Directory.Delete(path, recursive: true);
    }
}
