# Implement and verify runs

Runs of milestone 3, failures and fixes included. All on Claude Code (subscription); costs are list-price
estimates.

## 15 November 2026: the machinery without a model

An integration test drives the implementer's tools the way a model would, on a tiny Python repository with a bug,
against real Docker (`ImplementDockerTests`, 42 s): the workspace is a copy of the baseline volume; `bash` runs as uid
1000 with no network (a socket to 1.1.1.1 fails); the editor refuses `../etc/passwd`; `submit` is sent back while the
new test fails; after the fix `run_tests` shows no regression and `submit` is accepted; the verifier, on fresh copies,
finds the new test failing before the change and passing after it.

Two bugs found on the way:
- **Line endings.** The host's git had `core.autocrlf=true` (the Windows default), so every clone since milestone 1
  checked out CRLF, and that copy went into the sandbox. Python's tests didn't mind, but a diff taken in the sandbox
  would not have applied to the real repository, and a shell script with CRLF fails. The fetch stage now clones with
  `core.autocrlf=false`, and a test checks the bytes.
- A stage couldn't write into a subfolder of its own folder (`implement/round-1/`); `Job` now creates it.

A hand-written fix for the humanize issue then went through `foreman verify --no-review` in 17 s: 603 tests pass,
no regressions, the 4 new test cases fail on the code before the change and pass with it. The CI lint command
(`uvx --with tox-uv tox -e mypy`) can't run in the offline sandbox before or after the change, and the report says so.

## 15 November 2026: the first real run (humanize)

`foreman run --stages implement` on the full-pipeline humanize job, its PRP marked Ready. Verified in the first
round: 103 s, about $0.44 for the implementer and the reviewer together.

- **The implementer** (Opus 5.5, 6 tool calls) read the function and its tests, appended the new parametrised tests
  and ran them (they failed with `0.5 Bytes` where `0 Bytes` was expected), changed the two returns to `int(bytes_)`,
  added a docstring example, ran the suite (624 pass, no regressions) and submitted; the checks accepted it at once.
  It followed the PRP's gotchas: the new cases went into a separate test, not the table the existing test negates,
  and the `abs_bytes == 1` check still sees the untruncated value.
- **Verify**: 25 new tests pass, 21 of them fail on the code before the change (the other 4 pin behaviour that
  didn't change, such as nan and inf); no diff guard finding.
- **The reviewer** (2 tool calls, 24 s) approved with all 7 requirements met and evidence for each, and said what
  the gate couldn't show: the mypy check from CI never ran, so that part of R6 is unconfirmed.
- The PR description was good but named the PRP's requirement ids ("R1–R4"), which mean nothing to a maintainer.
  The prompt now says the PR is written for the maintainers, without the PRP, its ids or Foreman.

## 15 November 2026: the first implement eval (9 real issues)

`foreman eval implement` on the nine fixed issues of the localization eval, graded with the maintainers' own tests
from each fix (see `evals/implement/README.md`). The first run: resolved 2 of 3 graded, verified by the gate 3 of 9.
Reading every failure by hand found two bugs in the environments, one weakness in the spec writer, and one PRP that
chose a narrower fix than the maintainers did.

- **All four C# cases: the tests ran the old binaries.** The deterministic .NET plan, and the environment agent after
  it, built in setup and tested with `dotnet test --no-build`. The baseline can't see the problem: before any change
  the binaries and the code agree. In the implementer's own session it built by hand, so `run_tests` showed its new
  tests; the verifier's fresh copy didn't, so every round ended with "the change adds no new passing test". Two of
  the implementers stopped with `give_up` (kind `environment`) and diagnosed it exactly: `--no-build` on a fresh copy
  runs the stale test assembly. The hidden tests were never graded for the same reason.
- **itsdangerous: the tests imported a copy.** The environment agent installed the package with `pip install .`, so
  the tests imported the copy in site-packages and no change to `src/` reached them. The maintainers' tests couldn't
  even import their new name on the fixed code.
- **The fix for both:** an environment must test the code as it is when the tests run, since later stages change it
  and run the test script again without setup. The deterministic .NET plan now tests with `--no-restore` (so
  `dotnet test` builds, offline and incrementally); the environment agent's `submit` refuses `--no-build` and
  non-editable installs of the project (`WorkingTree`, unit-tested) and its prompt says why; the implement stage
  refuses an environment that still has either. The eval cache of the base and gold runs is now keyed by the
  environment too, so a rebuilt environment runs them again (the first fix was graded against the stale cache).
- **rich and itsdangerous: the PRP forbade the edit the fix needs.** Both PRPs changed behaviour that an existing
  test asserts, and both said that test must keep passing unchanged. The implementer stopped with `spec_problem` in
  the first round each time, naming the test and the line, instead of special-casing anything. The maintainers'
  fixes edit exactly those assertions. The stop is the designed outcome; the PRP was wrong. The spec prompt now says to
  search the tests for what pins the old behaviour and put each such test in `allowed_edits` with the assertion that
  changes.
- **hono: verified but not resolved.** The PRP (from clarify's default) fixed the crash in RegExpRouter, the router
  the default app uses, and put TrieRouter's different behaviour out of scope with a skip entry; the implementer did
  exactly that and the reviewer approved. The maintainers fixed TrieRouter as well, and one hidden test checks it. A
  defensible but narrower reading of the issue, decided at the spec, not in the code.

## 15 November 2026: the implement eval after the fixes

The same nine issues, everything rerun from the fetch (environments, baselines and PRPs included):
[results](../evals/implement/results.md).

- **Verified by the gate 6/9, every one in the first round; resolved 5/5 of the gradeable ones.** No change the gate
  passed failed the maintainers' tests. Implementing and reviewing cost $0.38–0.78 a case, $4.45 in all (list-price
  estimates); planning cost about as much again.
- **All four C# cases now pass the gate** (they failed every round before). The environment agent wrote
  `--no-restore` on its own from the new prompt line; `submit` never had to refuse.
- **MediatR can't be graded:** the maintainers' new tests pass on the code before their fix too. The bug needs a DI
  container with variance (DryIoc), and their tests use Microsoft's. Foreman's own reproduction test did fail before
  the change, as the gate requires.
- **Counts for C#:** when the fix's tests don't compile on the base commit, the whole test project is missing from
  the base run, so its old tests count as fail-to-pass (SWE-bench does the same; it can't tell an untouched test from
  one the fix rewrote). Resolved means every test that passes with the maintainers' fix passes with ours, either way.
- **The three stops were all the PRP, and all were right.** I checked each claim against the code:
  - itsdangerous: the spec now searched for tests that pin the old behaviour, as the new prompt line asks, but wrote
    the decision "no existing test pins `hashlib.sha1`" when `test_serializer.py:180` does. The implementer quoted the
    line and stopped (`test_problem`).
  - rich: an acceptance check compares `Style(color='red')` with what the decoder produces for SGR 31, but
    `Style.__eq__` compares hashes that include the colour's name (`color(1)`), so it can't hold within the PRP's scope.
  - hono: the PRP said LinearRouter returns one match for `/files/:wildcard{.*}?` and asked a test to show no router
    throws; LinearRouter throws `UnsupportedPathError` for a label with a star.
  The first run's hono PRP was different and implementable; one spec per case is noisy. The next lever is the spec
  (its claims about current behaviour checked by running them, as the implementer did), not the implementer.
