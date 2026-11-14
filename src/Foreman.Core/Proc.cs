using System.Diagnostics;
using System.Text;

namespace Foreman.Core;

public sealed record ProcResult(int ExitCode, string Stdout, string Stderr, TimeSpan Duration, bool TimedOut)
{
    public bool Ok => ExitCode == 0 && !TimedOut;

    /// <summary>The last lines of stdout and stderr together, for error messages and reports.</summary>
    public string Tail(int lines = 40)
    {
        var all = (Stdout + "\n" + Stderr).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join('\n', all.TakeLast(lines));
    }
}

/// <summary>Runs a process with arguments passed as a list (never through a shell string) and an optional timeout.</summary>
public static class Proc
{
    public static async Task<ProcResult> RunAsync(
        string exe,
        IEnumerable<string> args,
        string? cwd = null,
        TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string?>? env = null,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = cwd ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env is not null)
            foreach (var (k, v) in env) psi.Environment[k] = v;

        var clock = Stopwatch.StartNew();
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {exe}");
        var stdout = p.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = p.StandardError.ReadToEndAsync(CancellationToken.None);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is { } t) cts.CancelAfter(t);
        var timedOut = false;
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = !ct.IsCancellationRequested;
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await p.WaitForExitAsync(CancellationToken.None);
            if (!timedOut) throw;
        }

        return new ProcResult(p.ExitCode, await stdout, await stderr, clock.Elapsed, timedOut);
    }

    public static async Task<string> GitAsync(string cwd, params string[] args)
    {
        var r = await RunAsync("git", args, cwd);
        if (!r.Ok) throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {r.Stderr.Trim()}");
        return r.Stdout;
    }
}
