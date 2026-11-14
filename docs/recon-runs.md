# Recon runs

Every recon run on a real repository, failures included. Numbers come from the job folders
(`triage/triage.json`, `baseline/baseline.json`), not from memory.

## 14 November 2026: deterministic baseline, five repositories

Stages: fetch, triage, baseline. No model involved. Three test runs per repository with the network off.
Docker Desktop 29.9.3 on Windows 11 (WSL 2), 4 CPUs and 6 GB per container.

| Repository | Stack | Image | Tests | Pass | Known-red | Flaky | Wall clock |
|---|---|---|---|---|---|---|---|
| comp-ledger (own) | .NET 10, xUnit v3, VSTest | `dotnet/sdk:10.0` | 605 | 228 | 377 | 0 | 52 s |
| ardalis/GuardClauses | .NET (net11.0 target), xUnit | `dotnet/sdk:11.0` | 1,918 | 1,918 | 0 | 0 | 30 s |
| unjs/defu | TypeScript, pnpm 10, Vitest | `node:24-bookworm` | 23 | 23 | 0 | 0 | 16 s |
| pallets/itsdangerous | Python, uv, pytest | `python:3.12-bookworm` | 297 | 297 | 0 | 0 | 25 s |
| expressjs/cors | JavaScript, npm, Mocha | `node:20-bookworm` | 49 | 49 | 0 | 0 | 19 s |

Wall clock includes the clone (GitHub over the network), triage, copying into the volume, install, build and the
three test runs, with images already pulled.

**comp-ledger's 377 known-red tests are its integration tests.** They start PostgreSQL through Testcontainers,
which needs a Docker daemon, and the sandbox has none on purpose. The split matches the repository's own README
(228 unit, 377 integration), which also checks that TRX parsing is right. Making such tests runnable (a database
service next to the sandbox, or Docker-in-Docker under a stricter runtime) is a job for the environment agent and
a decision to record, not something to hide.

## What these runs found in Foreman, and what changed

1. **Bind mounts came up empty.** The first comp-ledger run failed with "project file does not exist": the job
   folder under `%LOCALAPPDATA%` was invisible to Docker Desktop, and the mount was silently empty. The sandbox
   now moves files with `docker cp` and never bind-mounts a host folder. That is also stricter isolation and works
   with a remote Docker host.
2. **96 of GuardClauses' 1,982 tests looked flaky; none were.** Theory tests put `DateTime.Now` and
   `Guid.NewGuid()` into their display names, so each run saw different names. Test ids now replace times, GUIDs
   and addresses with placeholders. Result: 1,918 tests, 0 flaky. (The count dropped by 64 because the same
   tests had been counted under several names.)
3. **pnpm install failed twice.** Corepack needs its link folder to exist, and the variable that keeps Corepack
   offline during tests had leaked into the install step. Both fixed; defu then installed in 5 s.
4. **itsdangerous keeps its test dependencies in `[dependency-groups]`** (PEP 735), which `uv sync --all-extras`
   doesn't install. Triage now reports extras and groups separately, and the plan passes `--group` for test groups.
5. **CI commands were split line by line**, which turned multi-line `run: |` scripts into fragments such as `{`.
   A block scalar is now kept as one script.

## 14 November 2026: the environment agent

Pipeline: fetch, triage, environment, baseline. The environment stage runs the deterministic plan first, in a fresh
copy; the agent is called only when the plan has nothing or fails. Driver: Claude Code headless on a subscription
(`--agent claude-code`), model `claude-opus-5-5`, built-in tools off, the four sandbox tools through Foreman's MCP
server. Costs are Claude Code's list-price estimates; the subscription isn't billed per token.

Regression first, without an agent (`--agent none`): cors 49/49 and itsdangerous 297/297, both "works without an
agent", same numbers as before.

| Repository | Why the agent | Tool calls | Turns | Time | Cost (est.) | Accepted environment | Baseline |
|---|---|---|---|---|---|---|---|
| google/uuid | Go: no deterministic plan | 4 | 5 | 79 s | $0.06 | `golang:1.21-bookworm`, gotestsum for JUnit | 213 tests: 212 pass, 1 skipped, 0 flaky |
| dtolnay/itoa | Rust: no deterministic plan | 4 | 5 | 83 s | $0.07 | `rust:1-bookworm` | 11 tests: 11 pass, 0 flaky |
| comp-ledger | Deterministic plan passed only 228 of 605 tests | 3 | 4 | 55 s | $0.16 | `dotnet/sdk:10.0`, accepted with a reason | 605 tests: 228 pass, 377 known-red, 0 flaky |

**comp-ledger is the interesting one.** Under the new rule (below) the deterministic plan wasn't good enough, so the
agent looked. In three tool calls it found that all 377 failures are integration tests that start PostgreSQL through
Testcontainers, confirmed it from the project's own `tools/verify.sh` and CLAUDE.md, submitted the same build with
`expected_failures_reason`, and said what a person would have to provide: a Docker daemon with the
`postgres:17-alpine` and .NET 10 images. Its final reply also confirmed it had found no instructions in the
repository asking for anything other than building and testing.

**Rough edge:** its test script ends with `exit 0`, which hides the test command's exit code. Verdicts come from the
report, so nothing is wrong in the numbers, but the exit code is lost from the logs. To fix in the prompt.

**Not run yet:** the `api` driver (our own loop on the Claude API) compiles and shares every tool with the Claude Code
driver, but there is no API key on this machine, so it has never made a call.

### What these runs changed

1. **System packages vanished between setup and tests.** The first Docker test of the tools failed with
   "No module named pytest": setup ran in one container, the tests in another, and only the volume survived.
   Setup now ends with `docker commit`, and the tests run on that snapshot. The baseline keeps its last snapshot as
   the job's environment image for the later stages.
2. **A deterministic plan that "worked" with 2 tests, 0 passing.** humanize declares its test dependencies with a
   dotted TOML key (`optional-dependencies.tests = [...]` inside `[project]`), which triage didn't read, so every
   test module failed to import. Two fixes: triage reads dotted keys (and stops installing docs, lint and type-check
   requirement files), and the deterministic plan now has the same bar as the agent: per-test results *and* at
   least half passing, otherwise the agent takes over with the failed attempt as context. After the fix humanize
   needs no agent: 908 tests, 762 pass, 146 skipped, 0 flaky.

### Same repository, two runs

itoa ran twice with the same prompt (before the line about setup time was added). Both were accepted after 4 tool
calls with the same 11 passing tests, but the first downloaded nextest's prebuilt binary (83 s in total) and the
second compiled it with `cargo install` (447 s, most of it in the two setups). One sample per task says little
about an agent; the eval milestone measures each task several times (pass^k) for this reason. The prompt now asks
for prebuilt releases, because setup runs again in every verification and every later stage.

The second run also checked the cleanup fix: Claude Code ends its MCP servers without a graceful shutdown, so the
first runs left their session containers and scratch volumes behind. The driver now removes them itself; after the
rerun nothing was left.

## 14 November 2026: code map, dossier, and the first localization eval

### Code map, no model

Roslyn for C# (exact symbol ids, nothing evaluated or run), tree-sitter in the sandbox for the rest.

| Repository | Files parsed | Definitions | Time |
|---|---|---|---|
| ardalis/GuardClauses | 55 C# (2 projects) | 872 | 3 s |
| comp-ledger | 37 C# | 657 | 4 s |
| python-humanize/humanize | 13 Python | 115 | 4 s (14 s the first time: the extractor image is built once) |
| unjs/defu | 9 TypeScript and JavaScript | 22 | 4 s |
| App-vNext/Polly (eval) | 790 C#, 1 JavaScript | 7,229 | 24 s |

The first GuardClauses map spent a third of its budget on `ThirdParty/JetBrains.Annotations.cs`: vendored code is
now left out of the map (and listed as not to edit). Python signatures over several lines were cut at the opening
parenthesis; they are now joined.

### Localization on 10 real issues

`foreman eval localization --readers`; details and caveats in [evals/localization](../evals/localization/README.md).

| Method | Recall@1 | Recall@5 | Hit@5 | MRR |
|---|---|---|---|---|
| BM25 over files (SWE-bench's retrieval baseline) | 20% | 60% | 70% | 0.38 |
| Code map, no task | 10% | 30% | 30% | 0.19 |
| Code map + exact mentions (Aider's signals) | 10% | 40% | 40% | 0.22 |
| Code map + task words | 10% | 40% | 40% | 0.21 |
| Code map with BM25 as restart points | 10% | 40% | 40% | 0.21 |
| BM25 + map, reciprocal-rank fusion | 20% | 60% | 70% | 0.36 |
| **Task reader** (Sonnet 5.5, read-only tools) | **65%** | **90%** | **100%** | **0.90** |

- No personalisation made the map competitive with BM25 at finding the fixed files: PageRank lifts files many
  others depend on, and the fix is usually in a leaf. Tuning heuristics on ten cases would have been fitting the
  test set, so the map stays what it is good for, context, and BM25's top files become the task reader's first
  candidates.
- The task reader put the fix's main file first in 9 of 10 cases, with 3–8 tool calls, 19–40 s and $0.06–0.18
  each (Claude Code's list-price estimate on a subscription). Its misses are second files of two-file fixes.
- Caveat: one run per case, and the fixes are public, so the model may know them. See the eval's README.

**Bug found by the eval:** three readers (Polly, MediatR, rich) never started: the first message, with two code
maps, was longer than the 32,767 characters of a Windows command line. The Claude Code driver now passes the task
through stdin and the system prompt through `--system-prompt-file`.

### Full dossier

| Repository | Sections | Time | Tool calls | Cost (est.) | Dossier | Rules file |
|---|---|---|---|---|---|---|
| python-humanize/humanize | architecture (smoke test) | 28 s | 7 | $0.12 | 1,632 tokens | 242 tokens, 25 lines |
| comp-ledger, run 1 | architecture, conventions, risks | 53 s | 30 | $0.63 | 5,684 tokens | 632 tokens |
| comp-ledger, run 2 | the same | 38 s | 33 | $0.37 | 5,587 tokens | 533 tokens, 30 lines |
| App-vNext/Polly (eval job) | architecture, conventions, risks, next to the eval's task section | 55 s | 40 | $0.52 | 5,532 tokens | 310 tokens |

All sections were accepted on the first submission, with no warnings. comp-ledger's risks section found what its
author would rank first: row-level security in Postgres that fails closed when the tenant isn't set, the
advisory-lock ordering behind as-known-at reads, never editing applied DbUp migrations, and that 377 known-red
integration tests mean most behaviour can't be verified without Docker. Every `path:line` in it was checked to exist.

The first comp-ledger rules file listed ten long known-red test names (more than half its tokens); it now says
"377, all in `compledger.integrationtests`" and quotes why from the environment agent's note.

comp-ledger's accepted test script still ends with `exit 0` (from the environment run before the prompt said not
to mask the exit code); the rules file shows it as it is. Rerunning that job's environment stage will replace it.
