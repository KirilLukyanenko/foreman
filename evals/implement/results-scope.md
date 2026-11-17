# Implement eval

Run: **scope**.

2026-11-17, implementer `claude-opus-5-5`, 9 real fixed issues, one run each. Every case went through the whole pipeline at the commit before its fix; the PRP was marked Ready by the harness instead of a human. **Verified** is Foreman's own gate (tests against the baseline, the new tests' fail-to-pass, the diff guard, lint, secrets, the reviewer). **Resolved** is the maintainers' own tests from the fix, applied over Foreman's change (SWE-bench style: every FAIL_TO_PASS and PASS_TO_PASS test passes); no agent ever saw them.

Resolved 7/8 graded; verified by the gate 9/9; verified and resolved 7, verified but not resolved 1, not verified but resolved 0. Implementing and reviewing cost 5.26 USD, planning (environment agent, spec) 7.24 USD (list-price estimates; the runs were on a subscription).

Resolved rate 88% of graded runs (95% Wilson interval 53%–98%).

**Foreman's own hidden acceptance tests** (written from the PRP by a separate session, tested by code on the base commit, a reference fix and its mutants): written for 9/9 cases; 178 of 260 pass on the maintainers' fix, all of them in 4/9 cases; writing them cost 3.00 USD.

| Case | Language | Gate | Rounds | Review | Hidden tests | Acceptance tests | Change | Cost | Time |
|---|---|---|---|---|---|---|---|---|---|
| python-humanize-humanize-239 | Python | verified | 1 | approve | **resolved** (2 fail-to-pass, 603 pass-to-pass) | 46 tests, 31 fail before; mutants n/a; 41/46 pass on the maintainers' fix | 2 files +6 −2 | $0.38 | 6 min |
| pallets-itsdangerous-375 | Python | verified | 1 | approve | **resolved** (297 fail-to-pass, 0 pass-to-pass) | 19 tests, 9 fail before; mutants 100 % killed; 19/19 pass on the maintainers' fix | 4 files +79 −7 | $0.49 | 245 min |
| Textualize-rich-3577 | Python | verified | 1 | approve | not resolved: 1/4 fail-to-pass failing, 0/950 pass-to-pass broken | 19 tests, 17 fail before; mutants 100 % killed; 9/19 pass on the maintainers' fix | 4 files +45 −2 | $0.42 | 10 min |
| unjs-defu-119 | TypeScript | verified | 1 | approve | **resolved** (1 fail-to-pass, 20 pass-to-pass) | 13 tests, 10 fail before; mutants n/a; 13/13 pass on the maintainers' fix | 5 files +87 −1 | $0.40 | 7 min |
| honojs-hono-4325 | TypeScript | verified | 1 | approve | **resolved** (2 fail-to-pass, 3581 pass-to-pass) | 24 tests, 12 fail before; mutants 67 % killed; 18/24 pass on the maintainers' fix | 6 files +71 −5 | $0.62 | 50 min |
| ardalis-GuardClauses-339 | C# | verified | 1 | approve | **resolved** (959 fail-to-pass, 0 pass-to-pass) | 16 tests, 16 fail before; mutants n/a; 16/16 pass on the maintainers' fix | 2 files +126 −3 | $0.52 | 8 min |
| jbogard-MediatR-1118 | C# | verified | 1 | approve | not graded: the fix's tests have no fail-to-pass test in this environment | 16 tests, 8 fail before; mutants n/a; 12/16 pass on the maintainers' fix | 5 files +363 −2 | $1.09 | 19 min |
| FluentValidation-FluentValidation-2240 | C# | verified | 1 | approve | **resolved** (2778 fail-to-pass, 0 pass-to-pass) | 57 tests, 57 fail before; mutants n/a; 0/57 pass on the maintainers' fix | 5 files +196 −0 | $0.74 | 20 min |
| App-vNext-Polly-2815 | C# | verified | 1 | approve | **resolved** (4 fail-to-pass, 6016 pass-to-pass) | 50 tests, 40 fail before; mutants 100 % killed; 50/50 pass on the maintainers' fix | 6 files +145 −2 | $0.60 | 35 min |

The issues, the PRPs, the patches and the hidden tests stay in the job folders and the eval cache, not in this repository.
