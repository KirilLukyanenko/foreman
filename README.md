# Foreman

Foreman takes a software task (a freelance job post, a ticket, an email) and a link to an unfamiliar repository,
and drives AI agents to a verified change: a branch, a PR description in the repository's own style, and a report
for the client, with the cost and time of every step. People decide at three points: which questions go to the
client, whether the spec is ready, and whether the result is sent.

The design, and what was taken from published coding agents and research, is in
[docs/architecture.md](docs/architecture.md).

## Status

Milestone 1, recon, in progress. Working now:

| Stage | What it does | Output |
|---|---|---|
| fetch | Shallow clone (200 commits, nothing after the base commit), no submodules, hooks off | `fetch/fetch.json` |
| triage | Stacks (.NET, Node, Python; others reported), SDK pins, test frameworks, CI commands, instruction files (with a hidden-Unicode check), install-time code, git style. No model. | `triage/triage.json` |
| environment | The deterministic plan first, verified in a fresh copy. Only if it has nothing, or fewer than half of the tests pass, the **environment agent** works in the sandbox with four tools (`run`, `use_image`, `submit`, `give_up`) until a fresh-copy verification accepts its setup and test scripts | `environment/environment.json`, `tools.jsonl` |
| baseline | Sets the accepted environment up again from scratch (snapshotting system packages), keeps the snapshot as the job's environment image, runs the tests 3 times with the network off; test names with run-time values are normalised. No model. | `baseline/baseline.json`, per-test verdicts |

Runs so far, failures and fixes included: [docs/recon-runs.md](docs/recon-runs.md). Nine repositories in .NET,
TypeScript, JavaScript, Python, Go and Rust; the agent was needed for three of them and took 3–4 tool calls and
under 90 seconds each.

The environment agent has two interchangeable model loops over the same tools: `api` (our own loop on the
Anthropic C# SDK, with prompt caching, effort and refusal fallbacks; needs an API key, not run yet) and
`claude-code` (Claude Code headless on a subscription, built-in tools off, the sandbox tools through Foreman's MCP
server). The model never runs anything on the host.

Next: the code map, the dossier readers, security scans.

## Run

Requires .NET 10 SDK, Docker, git; for the agent, Claude Code (`claude`) signed in, or `ANTHROPIC_API_KEY`.

```bash
dotnet run --project src/Foreman.Cli -- recon <repo path or git url> [--agent auto|claude-code|api|none] [--model claude-opus-5-5] [--budget 10]
dotnet run --project src/Foreman.Cli -- recon --job <id> --force environment,baseline     # rerun stages
dotnet run --project src/Foreman.Cli -- show <id>
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
