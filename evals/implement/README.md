# Implement eval

`foreman eval implement [--only id,id] [--keep-spec]` runs the whole pipeline on the real fixed issues of the
localization eval (Go's cobra is left out: Foreman can't build Go yet) and grades the result with the fix's own tests.

For each case:

1. The job from the localization and spec evals (the issue at the commit before its fix, intake, clarify with its
   defaults accepted) is fetched again, triaged, scanned, mapped, set up and baselined. The dossier's rules file is
   rebuilt with the baseline, and the PRP is rewritten with the verified test command (`--keep-spec` keeps the old one).
2. The harness marks the PRP Ready. In real use a human does this; the event log records that the harness did.
3. The accept stage writes Foreman's own hidden acceptance tests from the PRP (tested on the base commit, a reference
   fix and its mutants), then the implement stage runs: one candidate (`--candidates N` for more), Foreman's gate with
   those tests and the reviewer, at most two fix rounds.
4. **Hidden tests**, SWE-bench style. The maintainers' merged change is fetched once into the eval cache (two commits,
   no checkout) and split into its test files and its code files (the case's `test_files_changed` and `gold_files`).
   The test files run three ways, each on a fresh copy of the baseline volume with the network off: on the base
   commit, with the maintainers' code change, and with Foreman's patch. For Foreman's run, the test files the fix
   touched are reset to the base commit first, so Foreman's own edits to them don't decide the grade. FAIL_TO_PASS
   are the tests that fail on the base and pass with the maintainers' change; PASS_TO_PASS pass both ways.
   **Resolved** means all of both pass with Foreman's patch.

No agent sees the hidden tests or the maintainers' change. The grade measures the pipeline end to end: a PRP that
picks a different but defensible behaviour can fail hidden tests that assert the maintainers' exact choice, so every
"not resolved" is read by hand in `results.md`.

**Foreman's acceptance tests are graded too:** they run on the maintainers' fix. A test that fails there asserts
something the maintainers didn't do: a different reading of the issue in the PRP, or a wrong test.

Repeated runs and ablations: `--runs N` runs implement N times per case on the same PRP and reports pass@1, pass@k and
pass^k; `--reuse-planning` keeps a case's environment, PRP and acceptance tests from the last run, so `--no-review`,
`--no-acceptance` or `--candidates N` change only what they are about; `--label name` writes `results-name.*`, and
`foreman eval compare a.json b.json` pairs two runs with McNemar's exact test.

Outputs: `results.md`, `results.json`. Issues, PRPs, patches and hidden tests stay in the job folders and in
`<home>/eval-cache`, not in this repository.
