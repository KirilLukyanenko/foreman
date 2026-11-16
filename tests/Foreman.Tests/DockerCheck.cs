using Foreman.Core;

namespace Foreman.Tests;

/// <summary>Whether the Docker tests can run here: a daemon that answers, or none at all (no docker binary is a skip too).</summary>
internal static class DockerCheck
{
    public static async Task<bool> AvailableAsync()
    {
        try { return (await Proc.RunAsync("docker", ["info", "--format", "{{.ServerVersion}}"], timeout: TimeSpan.FromSeconds(20))).Ok; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }
}
