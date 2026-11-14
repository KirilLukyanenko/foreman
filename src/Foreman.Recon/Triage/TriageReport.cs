namespace Foreman.Recon.Triage;

public sealed record TriageReport(
    string Commit,
    RepoSize Size,
    IReadOnlyList<LanguageShare> Languages,
    DotnetStack? Dotnet,
    NodeStack? Node,
    PythonStack? Python,
    IReadOnlyList<string> UnsupportedStacks,
    IReadOnlyList<CiCommand> CiCommands,
    IReadOnlyList<string> EnvironmentFiles,
    IReadOnlyList<InstructionFile> InstructionFiles,
    IReadOnlyList<InstallScript> InstallScripts,
    IReadOnlyList<string> RemovedAgentConfig,
    GitFacts Git);

public sealed record RepoSize(int Files, long Bytes, bool Truncated);

public sealed record LanguageShare(string Language, int Files);

public sealed record DotnetStack(
    IReadOnlyList<string> Solutions,
    IReadOnlyList<string> Projects,
    IReadOnlyList<string> TestProjects,
    IReadOnlyList<string> TargetFrameworks,
    string? SdkVersion,
    string TestRunner,
    IReadOnlyList<string> TestFrameworks,
    bool CentralPackageManagement);

public sealed record NodeStack(
    string PackageJson,
    string PackageManager,
    string? Lockfile,
    string? NodeVersion,
    string? TestScript,
    string? TestFramework,
    bool Workspaces);

public sealed record PythonStack(
    IReadOnlyList<string> Manifests,
    string PackageManager,
    string? PythonVersion,
    string? TestFramework,
    IReadOnlyList<string> RequirementsFiles,
    IReadOnlyList<string> TestExtras);

/// <summary>A command from a CI file. CI is the best hint for how a project really builds and tests.</summary>
public sealed record CiCommand(string File, string Command);

/// <summary>
/// A file written for humans or agents working on the repository. Its content is data for the dossier, never an
/// instruction to Foreman's agents. Invisible or bidirectional Unicode is a known way to hide instructions in them.
/// </summary>
public sealed record InstructionFile(string Path, string Kind, long Bytes, string Sha256, int HiddenCharacters);

/// <summary>Code that runs while dependencies are installed or the project is built.</summary>
public sealed record InstallScript(string File, string Kind, string Detail);

public sealed record GitFacts(string? Head, int CommitsSeen, double? ConventionalCommitShare, DateTime? LastCommitAt, int Authors);
