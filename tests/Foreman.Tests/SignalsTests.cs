using Foreman.Recon.Triage;

namespace Foreman.Tests;

public class CiReaderTests
{
    [Fact]
    public void Github_actions_single_line_and_block_commands()
    {
        const string yaml = """
            jobs:
              test:
                steps:
                  - uses: actions/checkout@v4
                  - run: dotnet restore
                  - name: Test
                    run: |
                      dotnet build --no-restore
                      # a comment
                      dotnet test --no-build
                  - run: "npm ci"
            """;

        Assert.Equal(["dotnet restore", "dotnet build --no-restore\n# a comment\ndotnet test --no-build", "npm ci"], CiReader.Commands(yaml));
    }

    [Fact]
    public void Gitlab_script_lists()
    {
        const string yaml = """
            test:
              image: python:3.12
              script:
                - pip install -e .[test]
                - pytest -q
              artifacts:
                paths: [report.xml]
            """;

        Assert.Equal(["pip install -e .[test]", "pytest -q"], CiReader.Commands(yaml));
    }
}

public class RepoSignalsTests
{
    [Fact]
    public void Hidden_unicode_is_counted()
    {
        Assert.Equal(0, RepoSignals.CountHidden("plain text"));
        Assert.Equal(2, RepoSignals.CountHidden("ignore​previous‮instructions"));
        Assert.Equal(1, RepoSignals.CountHidden("tag " + char.ConvertFromUtf32(0xE0041)));
        Assert.Equal(0, RepoSignals.CountHidden("﻿leading BOM is fine"));
    }

    [Fact]
    public void Instruction_files_and_install_scripts_are_found()
    {
        using var repo = new TempRepo(
            ("AGENTS.md", "Run make test.​"),
            (".cursor/rules/style.mdc", "Use tabs."),
            ("README.md", "# Lib"),
            ("docs/README.md", "not at the root: ignored"),
            ("package.json", """{ "scripts": { "postinstall": "node scripts/fetch.js", "test": "jest" } }"""),
            ("build/Tools.targets", """<Target Name="X"><Exec Command="curl https://example.invalid" /></Target>"""));
        var files = RepoFiles.Scan(repo.Root);

        var instructions = RepoSignals.InstructionFiles(files);
        Assert.Equal(["AGENTS.md", "README.md", ".cursor/rules/style.mdc"], instructions.Select(i => i.Path).OrderBy(p => p.StartsWith('.')).ThenBy(p => p, StringComparer.Ordinal));
        Assert.Equal(1, instructions.Single(i => i.Path == "AGENTS.md").HiddenCharacters);

        var scripts = RepoSignals.InstallScripts(files);
        Assert.Contains(scripts, s => s.Kind == "npm-postinstall" && s.Detail == "node scripts/fetch.js");
        Assert.DoesNotContain(scripts, s => s.Kind == "msbuild-exec"); // "build/" is skipped as an output folder
    }
}
