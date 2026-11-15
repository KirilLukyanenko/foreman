# Implement eval

2026-11-15, implementer `claude-opus-5-5`, 9 real fixed issues, one run each. Every case went through the whole pipeline at the commit before its fix; the PRP was marked Ready by the harness instead of a human. **Verified** is Foreman's own gate (tests against the baseline, the new tests' fail-to-pass, the diff guard, lint, secrets, the reviewer). **Resolved** is the maintainers' own tests from the fix, applied over Foreman's change (SWE-bench style: every FAIL_TO_PASS and PASS_TO_PASS test passes); no agent ever saw them.

Resolved 6/8 graded; verified by the gate 9/9; verified and resolved 6, verified but not resolved 2, not verified but resolved 0. Implementing and reviewing cost 4.41 USD, planning (environment agent, spec) 6.03 USD (list-price estimates; the runs were on a subscription).

**Foreman's own hidden acceptance tests** (written from the PRP by a separate session, tested by code on the base commit, a reference fix and its mutants): written for 8/9 cases; 124 of 136 pass on the maintainers' fix, all of them in 5/8 cases; writing them cost 2.89 USD.

| Case | Language | Gate | Rounds | Review | Hidden tests | Acceptance tests | Change | Cost | Time |
|---|---|---|---|---|---|---|---|---|---|
| python-humanize-humanize-239 | Python | verified | 1 | approve | **resolved** (2 fail-to-pass, 603 pass-to-pass) | 7 tests, 6 fail before; mutants n/a; 6/7 pass on the maintainers' fix | 2 files +8 −2 | $0.40 | 6 min |
| pallets-itsdangerous-375 | Python | verified | 1 | approve | **resolved** (297 fail-to-pass, 0 pass-to-pass) | 13 tests, 6 fail before; mutants 50 % killed; 13/13 pass on the maintainers' fix | 4 files +79 −10 | $0.49 | 7 min |
| Textualize-rich-3577 | Python | verified | 1 | approve | not resolved: 1/4 fail-to-pass failing, 0/950 pass-to-pass broken | 13 tests, 11 fail before; mutants n/a; 10/13 pass on the maintainers' fix | 4 files +44 −3 | $0.38 | 11 min |
| unjs-defu-119 | TypeScript | verified | 1 | approve | **resolved** (1 fail-to-pass, 20 pass-to-pass) | 5 tests, 4 fail before; mutants n/a; 5/5 pass on the maintainers' fix | 5 files +26 −1 | $0.31 | 6 min |
| honojs-hono-4325 | TypeScript | verified | 1 | approve | not resolved: 1/2 fail-to-pass failing, 0/3582 pass-to-pass broken | 16 tests, 11 fail before; mutants n/a; 8/16 pass on the maintainers' fix | 3 files +101 −1 | $0.43 | 20 min |
| ardalis-GuardClauses-339 | C# | verified | 1 | approve | **resolved** (959 fail-to-pass, 0 pass-to-pass) | 17 tests, 17 fail before; mutants n/a; 17/17 pass on the maintainers' fix | 2 files +123 −2 | $0.54 | 10 min |
| jbogard-MediatR-1118 | C# | verified | 1 | approve | not graded: the fix's tests have no fail-to-pass test in this environment | 23 tests, 6 fail before; mutants n/a; 23/23 pass on the maintainers' fix | 3 files +187 −0 | $0.66 | 14 min |
| FluentValidation-FluentValidation-2240 | C# | verified | 1 | approve | **resolved** (2778 fail-to-pass, 0 pass-to-pass) | gave up: I couldn't submit: Foreman can't apply my reference fix. The PRP itself is fine.… | 5 files +164 −0 | $0.69 | 18 min |
| App-vNext-Polly-2815 | C# | verified | 1 | approve | **resolved** (4 fail-to-pass, 6016 pass-to-pass) | 42 tests, 34 fail before; mutants 100 % killed; 42/42 pass on the maintainers' fix | 3 files +151 −2 | $0.51 | 34 min |

The issues, the PRPs, the patches and the hidden tests stay in the job folders and the eval cache, not in this repository.
