# Implement eval

Run: **crlf-fix**.

2026-11-15, implementer `claude-opus-5-5`, 1 real fixed issues, one run each. Every case went through the whole pipeline at the commit before its fix; the PRP was marked Ready by the harness instead of a human. **Verified** is Foreman's own gate (tests against the baseline, the new tests' fail-to-pass, the diff guard, lint, secrets, the reviewer). **Resolved** is the maintainers' own tests from the fix, applied over Foreman's change (SWE-bench style: every FAIL_TO_PASS and PASS_TO_PASS test passes); no agent ever saw them.

Resolved 1/1 graded; verified by the gate 1/1; verified and resolved 1, verified but not resolved 0, not verified but resolved 0. Implementing and reviewing cost 0.66 USD, planning (environment agent, spec) 0.78 USD (list-price estimates; the runs were on a subscription).

Resolved rate 100% of graded runs (95% Wilson interval 21%–100%).

**Foreman's own hidden acceptance tests** (written from the PRP by a separate session, tested by code on the base commit, a reference fix and its mutants): written for 1/1 cases; 0 of 45 pass on the maintainers' fix, all of them in 0/1 cases; writing them cost 0.42 USD.

| Case | Language | Gate | Rounds | Review | Hidden tests | Acceptance tests | Change | Cost | Time |
|---|---|---|---|---|---|---|---|---|---|
| FluentValidation-FluentValidation-2240 | C# | verified | 1 | approve | **resolved** (2778 fail-to-pass, 0 pass-to-pass) | 45 tests, 45 fail before; mutants n/a; 0/45 pass on the maintainers' fix | 5 files +231 −1 | $0.66 | 19 min |

The issues, the PRPs, the patches and the hidden tests stay in the job folders and the eval cache, not in this repository.
