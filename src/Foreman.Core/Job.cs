using System.Text;
using System.Text.Json;

namespace Foreman.Core;

/// <param name="Waiting">The stage can't go on until a human acts (the client's answers, the PRP marked Ready, a no-go
/// overridden); <see cref="StageState.Error"/> says what. Running the job again resumes it.</param>
public enum StageStatus { Pending, Running, Done, Failed, Waiting }

/// <summary>A stage stops for a human. <paramref name="For"/>: <c>answers</c>, <c>ready</c> or <c>go</c>.</summary>
public sealed class JobWaitingException(string @for, string message) : InvalidOperationException(message)
{
    public string For { get; } = @for;
}

public sealed record StageState(StageStatus Status, DateTime? StartedAt = null, DateTime? FinishedAt = null, string? Error = null);

public sealed record JobManifest(string Id, string Source, DateTime CreatedAt, string? TaskFile);

/// <summary>
/// A job is a folder: <c>job.json</c> (what it is), <c>state.json</c> (stage statuses), <c>events.jsonl</c>
/// (append-only audit log) and one subfolder per stage holding that stage's outputs. Every stage reads its inputs
/// from these files, so any stage can be rerun on its own.
/// </summary>
public sealed class Job
{
    private readonly object _lock = new();

    private Job(string dir, JobManifest manifest)
    {
        Dir = dir;
        Manifest = manifest;
    }

    public string Dir { get; }
    public JobManifest Manifest { get; }
    public string Id => Manifest.Id;

    /// <summary>The repository as cloned, read-only for stages: triage reads it, the sandbox copies it.</summary>
    public string SourceDir => Path.Combine(Dir, "source");

    public string StageDir(string stage)
    {
        var d = Path.Combine(Dir, stage);
        Directory.CreateDirectory(d);
        return d;
    }

    public static Job Create(string home, string source, string? taskFile, string? id = null)
    {
        if (id is null)
        {
            // Two jobs for the same repository in the same second (the service takes requests in parallel) get -2, -3...
            var stem = NewId(source);
            id = stem;
            for (var n = 2; Directory.Exists(Path.Combine(home, "jobs", id)); n++) id = $"{stem}-{n}";
        }
        var dir = Path.Combine(home, "jobs", id);
        if (Directory.Exists(dir)) throw new InvalidOperationException($"job {id} already exists at {dir}");
        Directory.CreateDirectory(dir);
        var manifest = new JobManifest(id, source, DateTime.UtcNow, taskFile is null ? null : "task.md");
        if (taskFile is not null) File.Copy(taskFile, Path.Combine(dir, "task.md"));
        File.WriteAllText(Path.Combine(dir, "job.json"), Json.Write(manifest));
        var job = new Job(dir, manifest);
        job.Event("job", "created", new { source });
        return job;
    }

    public static Job Open(string home, string id)
    {
        var dir = Path.Combine(home, "jobs", id);
        var file = Path.Combine(dir, "job.json");
        if (!File.Exists(file)) throw new InvalidOperationException($"job {id} not found at {dir}");
        return new Job(dir, Json.Read<JobManifest>(File.ReadAllText(file))!);
    }

    public static string NewId(string source)
    {
        var name = Path.GetFileNameWithoutExtension(source.TrimEnd('/', '\\'));
        var slug = new string(name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        if (slug.Length == 0) slug = "repo";
        if (slug.Length > 40) slug = slug[..40];
        return $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{slug}";
    }

    // ---- stage state ----

    public Dictionary<string, StageState> ReadState()
    {
        var file = Path.Combine(Dir, "state.json");
        return File.Exists(file)
            ? Json.Read<Dictionary<string, StageState>>(File.ReadAllText(file)) ?? []
            : [];
    }

    public void SetState(string stage, StageState state)
    {
        lock (_lock)
        {
            var all = ReadState();
            all[stage] = state;
            WriteAtomic(Path.Combine(Dir, "state.json"), Json.Write(all));
        }
    }

    /// <summary>Forgets a stage's state, so the next run does it again (its outputs were removed).</summary>
    public void ClearState(string stage)
    {
        lock (_lock)
        {
            var all = ReadState();
            if (all.Remove(stage)) WriteAtomic(Path.Combine(Dir, "state.json"), Json.Write(all));
        }
    }

    // ---- artifacts ----

    public string WriteJson<T>(string stage, string file, T value)
    {
        var path = Path.Combine(StageDir(stage), file);
        WriteAtomic(path, Json.Write(value));
        return path;
    }

    public T ReadJson<T>(string stage, string file)
    {
        var path = Path.Combine(Dir, stage, file);
        if (!File.Exists(path)) throw new InvalidOperationException($"{stage}/{file} is missing: run stage '{stage}' first");
        return Json.Read<T>(File.ReadAllText(path))!;
    }

    public string WriteText(string stage, string file, string text)
    {
        var path = Path.Combine(StageDir(stage), file);
        WriteAtomic(path, text);
        return path;
    }

    // ---- audit log ----

    public void Event(string stage, string kind, object? data = null)
    {
        var line = JsonSerializer.Serialize(new { ts = DateTime.UtcNow, stage, kind, data }, Json.Line);
        lock (_lock) File.AppendAllText(Path.Combine(Dir, "events.jsonl"), line + "\n", new UTF8Encoding(false));
    }

    private static void WriteAtomic(string path, string text)
    {
        // A stage may keep files in subfolders of its own (implement/round-2/...).
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }
}
