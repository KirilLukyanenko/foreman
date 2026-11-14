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
