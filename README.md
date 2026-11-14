# Foreman

Foreman takes a software task (a freelance job post, a ticket, an email) and a link to an unfamiliar repository,
and drives AI agents to a verified change: a branch, a PR description in the repository's own style, and a report
for the client, with the cost and time of every step. People decide at three points: which questions go to the
client, whether the spec is ready, and whether the result is sent.

The design, and what was taken from published coding agents and research, is in
[docs/architecture.md](docs/architecture.md).

## Status

Milestone 1, recon, in progress. Working now, without a model:

| Stage | What it does | Output |
|---|---|---|
| fetch | Shallow clone (200 commits, nothing after the base commit), no submodules, hooks off | `fetch/fetch.json` |
| triage | Stacks (.NET, Node, Python), SDK pins, test frameworks, CI commands, instruction files (with a hidden-Unicode check), install-time code, git style | `triage/triage.json` |
| baseline | Copies the repository into a per-job Docker volume (without its agent configuration), installs and builds, then runs the tests 3 times with the network off; test names with run-time values are normalised | `baseline/baseline.json`, per-test verdicts |

First runs on five real repositories (.NET, TypeScript, Python): [docs/recon-runs.md](docs/recon-runs.md).

Next: the environment agent for repositories the deterministic plan can't build, the code map, the dossier
readers, security scans.

## Run

Requires .NET 10 SDK, Docker, git.

```bash
dotnet run --project src/Foreman.Cli -- recon <repo path or git url> [--task task.md] [--ref branch] [--runs 3]
dotnet run --project src/Foreman.Cli -- recon --job <id> --force baseline     # rerun one stage
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
