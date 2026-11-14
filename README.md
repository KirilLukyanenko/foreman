# Foreman

Foreman takes a software task (a freelance job post, a ticket, an email) and a link to an unfamiliar repository,
and drives AI agents to a verified change: a branch, a PR description in the repository's own style, and a report
for the client, with the cost and time of every step. People decide at three points: which questions go to the
client, whether the spec is ready, and whether the result is sent.

The design, and what was taken from published coding agents and research, is in
[docs/architecture.md](docs/architecture.md).

## Status

Milestone 1, recon, done:

| Stage | What it does | Output |
|---|---|---|
| fetch | Shallow clone (200 commits, nothing after the base commit), no submodules, hooks off | `fetch/fetch.json` |
| triage | Stacks (.NET, Node, Python; others reported), SDK pins, test frameworks, CI commands, instruction files (with a hidden-Unicode check), install-time code, git style. No model. | `triage/triage.json` |
| security | gitleaks (files and history, values redacted), osv-scanner against an offline OSV copy, Opengrep with GitLab's MIT sast-rules; one image, every tool pinned by SHA-256; the scan has no network and the database download never sees the repository. No model. | `security/security.json` |
| codemap | Definitions and references of every source file (Roslyn for C#, with exact symbol resolution and nothing executed; tree-sitter in the sandbox for 15 other languages), PageRank over the file graph, the best definitions printed to a token budget. A second map personalised to the task, and BM25 candidates for it. No model. | `codemap/repomap.md`, `repomap-task.md`, `index.json` |
| environment | The deterministic plan first, verified in a fresh copy. Only if it has nothing, or fewer than half of the tests pass, the **environment agent** works in the sandbox with four tools (`run`, `use_image`, `submit`, `give_up`) until a fresh-copy verification accepts its setup and test scripts | `environment/environment.json`, `tools.jsonl` |
| baseline | Sets the accepted environment up again from scratch (snapshotting system packages), keeps the snapshot as the job's environment image, runs the tests 3 times with the network off; test names with run-time values are normalised. No model. | `baseline/baseline.json`, per-test verdicts |
| dossier | Four read-only **readers** in parallel (architecture, conventions, where the task lives, risks) with `list_files`, `read_file`, `search`, `code_map`, `find_symbol`; every file, line and symbol they cite is checked by code before a section is accepted. Plus a short rules file for the implementer, built by code from verified facts. | `dossier/dossier.md`, `dossier/AGENTS.md` |

Runs so far, failures and fixes included: [docs/recon-runs.md](docs/recon-runs.md). Nine repositories in .NET,
TypeScript, JavaScript, Python, Go and Rust; the agent was needed for three of them and took 3–4 tool calls and
under 90 seconds each.

Every agent (the environment agent, the readers) works through a **toolbox**, and two interchangeable model loops connect a model to it: `api` (our own loop on the
Anthropic C# SDK, with prompt caching, effort and refusal fallbacks; needs an API key, not run yet) and
`claude-code` (Claude Code headless on a subscription, built-in tools off, the sandbox tools through Foreman's MCP
server). The model never runs anything on the host.

Localization is measured on 10 real fixed issues (`foreman eval localization`): [results](evals/localization/results.md).

Milestone 1 (recon) is complete. Next: intake and clarify.

## Run

Requires .NET 10 SDK, Docker, git; for the agent, Claude Code (`claude`) signed in, or `ANTHROPIC_API_KEY`.

```bash
dotnet run --project src/Foreman.Cli -- recon <repo path or git url> [--task task.md] [--agent auto|claude-code|api|none] [--model claude-opus-5-5] [--reader-model claude-sonnet-5-5] [--budget 10]
dotnet run --project src/Foreman.Cli -- recon --job <id> --force environment,baseline     # rerun stages
dotnet run --project src/Foreman.Cli -- show <id>
dotnet run --project src/Foreman.Cli -- eval localization [--readers]                    # 10 real issues, needs gh
```

Jobs are stored in `%LOCALAPPDATA%/Foreman/jobs` (or `$FOREMAN_HOME/jobs`).

## Sandbox

The repository's code runs only inside Docker: one volume per job, a non-root user, no Linux capabilities,
`no-new-privileges`, CPU, memory and process limits, and a wall-clock timeout. The network is on only to install
dependencies; build and test run with `--network none`. Files move in and out with `docker cp`, so the container
never sees the host filesystem and no secrets are passed in.

## Tests

```bash
dotnet test
```
