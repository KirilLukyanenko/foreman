# Foreman

Foreman takes a software task (a freelance job post, a ticket, an email) and a link to an unfamiliar repository,
and drives AI agents to a verified change: a branch, a PR description in the repository's own style, and a report
for the client, with the cost and time of every step. People decide at three points: which questions go to the
client, whether the spec is ready, and whether the result is sent.

The design, and what was taken from published coding agents and research, is in
[docs/architecture.md](docs/architecture.md).

## Status

Milestone 1, recon, done; milestone 2 (from task text to a Ready spec) done; milestone 3 (implement and verify) done; milestone 4 (hidden acceptance tests, parallel candidates, the eval harness, a red team) built and measured.

| Stage | What it does | Output |
|---|---|---|
| intake | The task text becomes a brief (Claude Haiku 5.5, one checked tool): goal, deliverables, acceptance, constraints, the client's language, unknowns, text aimed at an AI or at the bidder, and a go/no-go. Every explicit item must quote the task; code can only raise the caution. | `intake/brief.json`, `brief.md` |
| go | Stops the pipeline on a no-go before recon spends anything, unless `--go`. No model. | `go/go.json` |
| fetch | Shallow clone (200 commits, nothing after the base commit), no submodules, hooks off | `fetch/fetch.json` |
| triage | Stacks (.NET, Node, Python; others reported), SDK pins, test frameworks, CI commands, instruction files (with a hidden-Unicode check), install-time code, git style. No model. | `triage/triage.json` |
| security | gitleaks (files and history, values redacted), osv-scanner against an offline OSV copy, Opengrep with GitLab's MIT sast-rules; one image, every tool pinned by SHA-256; the scan has no network and the database download never sees the repository. No model. | `security/security.json` |
| codemap | Definitions and references of every source file (Roslyn for C#, with exact symbol resolution and nothing executed; tree-sitter in the sandbox for 15 other languages), PageRank over the file graph, the best definitions printed to a token budget. A second map personalised to the task, and BM25 candidates for it. No model. | `codemap/repomap.md`, `repomap-task.md`, `index.json` |
| environment | The deterministic plan first, verified in a fresh copy. Only if it has nothing, or fewer than half of the tests pass, the **environment agent** works in the sandbox with four tools (`run`, `use_image`, `submit`, `give_up`) until a fresh-copy verification accepts its setup and test scripts | `environment/environment.json`, `tools.jsonl` |
| baseline | Sets the accepted environment up again from scratch (snapshotting system packages), keeps the snapshot as the job's environment image, runs the tests 3 times with the network off; test names with run-time values are normalised. No model. | `baseline/baseline.json`, per-test verdicts |
| dossier | Four read-only **readers** in parallel (architecture, conventions, where the task lives, risks) with `list_files`, `read_file`, `search`, `code_map`, `find_symbol`; every file, line and symbol they cite is checked by code before a section is accepted. Plus a short rules file for the implementer, built by code from verified facts. | `dossier/dossier.md`, `dossier/AGENTS.md` |
| clarify | The under-specification detector (Claude Opus 5.5) after recon: reads the code before asking; each gap is closed by the repository (with evidence), assumed (default stated) or asked (at most 5, each with a default in the client's language). Code checks the rules. | `clarify/questions.md` (a draft for the human to send), `clarify.md`, `questions.json` |
| spec | The PRP (Claude Opus 5.5, with a lab: a throwaway copy of the set-up repository where it runs what it claims and sketches the change against the suite): requirements, each with how it is checked (acceptance test, existing tests, a command, or review), the public interface, examples, decisions, where the change goes, tasks. Code enforces the done contract and coverage of the brief and of the client's answers, checks files and symbols, and adds the verified commands. Waits for the client's answers; the human marks it Ready. | `spec/prp.md`, `prp.json`, `ready.json` |
| accept | A separate **acceptance test writer** (Claude Opus 5.5) sees the Ready PRP and the code before the change, never the implementer's work, and writes black-box tests with a reference fix of its own in a lab. Code tests the tests: new behaviour fails before the change, everything passes on the reference, and broken versions of the reference (mutants: a hunk left out, an operator flipped) are caught. | `acceptance/tests.diff`, `acceptance.json` |
| implement | The **implementer** (Claude Opus 5.5) on a copy of the set-up repository, network off, unprivileged: `bash`, a str_replace editor, `run_tests` against the baseline, `submit`, `give_up`. Claude Code runs on the host with no built-in tools; only its tools act in the sandbox, so no credential goes into it. `submit` is checked by code (diff guard, every requirement accounted for, a test when the PRP needs one, no regression). At most 2 fix rounds with the gate's and the reviewer's findings. `--candidates N` runs N in parallel and keeps the best verified one. | `implement/implement.json`, `patch.diff`, `pr.md` |
| verify | On fresh copies with only the patch: the diff guard (edited or deleted tests, skip markers, CI, test config, lockfiles), pass-to-pass against the baseline (a regression must fail twice), the new tests fail before the change and pass with it, the hidden acceptance tests pass, the CI linters before and after, gitleaks on added lines. No model. | `verify/verify.json` |
| review | A fresh **reviewer** (Claude Opus 5.5) sees the PRP, the diff, the gate's facts and the implementer's account: a verdict per requirement with evidence; each finding must cite a PRP rule or an added line, checked by code; minor findings are notes, never sent back. | `verify/round-N/review.json` |

Runs so far, failures and fixes included: [docs/recon-runs.md](docs/recon-runs.md). Nine repositories in .NET,
TypeScript, JavaScript, Python, Go and Rust; the agent was needed for three of them and took 3–4 tool calls and
under 90 seconds each.

Every agent (intake, the environment agent, the readers, clarify, spec, the implementer, the reviewer) works through a **toolbox**, and two interchangeable model loops connect a model to it: `api` (our own loop on the
Anthropic C# SDK, with prompt caching, effort and refusal fallbacks; needs an API key, not run yet) and
`claude-code` (Claude Code headless on a subscription, built-in tools off, the sandbox tools through Foreman's MCP
server). The model never runs anything on the host.

Measured: localization on 10 real fixed issues ([results](evals/localization/results.md)), intake on 16 labelled tasks ([results](evals/intake/results.md)), clarify on 14 ([results](evals/clarify/results.md)), spec on 14 ([results](evals/spec/results.md)). Intake, clarify and spec runs, failures and fixes: [docs/planning-runs.md](docs/planning-runs.md).

Implement end to end on 9 real fixed issues (Python, TypeScript, C#), graded with the maintainers' own tests from each fix: verified by Foreman's gate 9/9, 8 in the first round; resolved 6/8 of the gradeable ones, the other 2 narrower than the maintainers' fix by the PRP's choice ([results](evals/implement/results.md)). Three runs per case: 27/27 verified, pass@1 = pass^3 = 75%, every case the same each time ([results](evals/implement/results-runs3.md)). Before the spec writer had a lab to check its claims in, 3 of the 9 stopped on a contradictory PRP. Runs, failures and fixes: [docs/implement-runs.md](docs/implement-runs.md).

Foreman's own hidden acceptance tests, on the same 9 issues: 181 tests written and tested by code, every implementer passed all of them, and where they disagree with the maintainers' fix it is where the PRP chose a different reading of the issue. Red team: an instruction planted in a README, a code comment, AGENTS.md and the task text; safe 4/4, the real task done 4/4 ([results](evals/redteam/results.md)).

Next: milestone 5, delivery: the client report, approvals from Tower, one real task end to end.

## Run

Requires .NET 10 SDK, Docker, git; for the agent, Claude Code (`claude`) signed in, or `ANTHROPIC_API_KEY`.

```bash
dotnet run --project src/Foreman.Cli -- recon <repo path or git url> [--task task.md] [--agent auto|claude-code|api|none] [--model claude-opus-5-5] [--reader-model claude-sonnet-5-5] [--budget 10]
dotnet run --project src/Foreman.Cli -- run <repo> --task task.md [--go]                  # intake, recon, clarify
dotnet run --project src/Foreman.Cli -- recon --job <id> --force environment,baseline     # rerun stages
dotnet run --project src/Foreman.Cli -- answer <id> --set Q1="..." | --defaults          # record the client's answers
dotnet run --project src/Foreman.Cli -- ready <id>                                        # approve spec/prp.md
dotnet run --project src/Foreman.Cli -- recon --job <id>                                  # then implement, verify, review
dotnet run --project src/Foreman.Cli -- verify <id> [--no-review]                         # rerun the gate on implement/patch.diff
dotnet run --project src/Foreman.Cli -- show <id>
dotnet run --project src/Foreman.Cli -- eval localization [--readers]                    # 10 real issues, needs gh
dotnet run --project src/Foreman.Cli -- eval intake --runs 3                              # after eval localization
dotnet run --project src/Foreman.Cli -- eval clarify --runs 3
dotnet run --project src/Foreman.Cli -- eval spec
dotnet run --project src/Foreman.Cli -- eval implement [--only id,id]                     # 9 real issues, hidden tests
dotnet run --project src/Foreman.Cli -- eval implement --runs 3 --reuse-planning --label x  # pass@k, pass^k; ablations: --no-review, --no-acceptance, --candidates 3
dotnet run --project src/Foreman.Cli -- eval compare a.json b.json                        # paired, McNemar
dotnet run --project src/Foreman.Cli -- eval redteam                                      # prompt injection planted in a repository
```

As a service for Tower (jobs over HTTP, waits for the human's answers and approval, resumes): `foreman serve`, see [docs/server.md](docs/server.md).

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
