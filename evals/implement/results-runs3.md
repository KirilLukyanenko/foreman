# Implement eval

Run: **runs3**.

2026-11-16, implementer `claude-opus-5-5`, 9 real fixed issues, 3 implement runs each on the same PRP. Every case went through the whole pipeline at the commit before its fix; the PRP was marked Ready by the harness instead of a human. **Verified** is Foreman's own gate (tests against the baseline, the new tests' fail-to-pass, the diff guard, lint, secrets, the reviewer). **Resolved** is the maintainers' own tests from the fix, applied over Foreman's change (SWE-bench style: every FAIL_TO_PASS and PASS_TO_PASS test passes); no agent ever saw them.

Resolved 18/24 graded; verified by the gate 27/27; verified and resolved 18, verified but not resolved 6, not verified but resolved 0. Implementing and reviewing cost 12.26 USD, planning (environment agent, spec) 0.00 USD (list-price estimates; the runs were on a subscription).

Resolved rate 75% of graded runs (95% Wilson interval 55%–88%).

Per case, over 3 runs (pass@1 and pass@3: at least one of 1 or 3 runs resolves; pass^3: all 3 do, which is what one client gets when they need it every time):

| Case | Verified | Resolved | pass@1 | pass@3 | pass^3 |
|---|---|---|---|---|---|
| python-humanize-humanize-239 | 3/3 | 3/3 | 100% | 100% | 100% |
| pallets-itsdangerous-375 | 3/3 | 3/3 | 100% | 100% | 100% |
| Textualize-rich-3577 | 3/3 | 0/3 | 0% | 0% | 0% |
| unjs-defu-119 | 3/3 | 3/3 | 100% | 100% | 100% |
| honojs-hono-4325 | 3/3 | 0/3 | 0% | 0% | 0% |
| ardalis-GuardClauses-339 | 3/3 | 3/3 | 100% | 100% | 100% |
| FluentValidation-FluentValidation-2240 | 3/3 | 3/3 | 100% | 100% | 100% |
| App-vNext-Polly-2815 | 3/3 | 3/3 | 100% | 100% | 100% |
| **mean** | | | 75% | 75% | 75% |

**Foreman's own hidden acceptance tests** (written from the PRP by a separate session, tested by code on the base commit, a reference fix and its mutants): written for 9/9 cases; 124 of 181 pass on the maintainers' fix, all of them in 5/9 cases; writing them cost 2.75 USD.

| Case | Language | Gate | Rounds | Review | Hidden tests | Acceptance tests | Change | Cost | Time |
|---|---|---|---|---|---|---|---|---|---|
| python-humanize-humanize-239 | Python | verified | 1 | approve | **resolved** (2 fail-to-pass, 603 pass-to-pass) | 7 tests, 6 fail before; mutants n/a; 6/7 pass on the maintainers' fix | 2 files +8 −2 | $0.38 | 2 min |
| python-humanize-humanize-239 | Python | verified | 1 | approve | **resolved** (2 fail-to-pass, 603 pass-to-pass) | 7 tests, 6 fail before; mutants n/a; 6/7 pass on the maintainers' fix | 2 files +8 −2 | $0.31 | 2 min |
| python-humanize-humanize-239 | Python | verified | 1 | approve | **resolved** (2 fail-to-pass, 603 pass-to-pass) | 7 tests, 6 fail before; mutants n/a; 6/7 pass on the maintainers' fix | 2 files +8 −2 | $0.30 | 2 min |
| pallets-itsdangerous-375 | Python | verified | 1 | approve | **resolved** (297 fail-to-pass, 0 pass-to-pass) | 13 tests, 6 fail before; mutants 50 % killed; 13/13 pass on the maintainers' fix | 4 files +49 −9 | $0.45 | 2 min |
| pallets-itsdangerous-375 | Python | verified | 1 | approve | **resolved** (297 fail-to-pass, 0 pass-to-pass) | 13 tests, 6 fail before; mutants 50 % killed; 13/13 pass on the maintainers' fix | 4 files +44 −8 | $0.35 | 2 min |
| pallets-itsdangerous-375 | Python | verified | 1 | approve | **resolved** (297 fail-to-pass, 0 pass-to-pass) | 13 tests, 6 fail before; mutants 50 % killed; 13/13 pass on the maintainers' fix | 4 files +48 −8 | $0.38 | 2 min |
| Textualize-rich-3577 | Python | verified | 1 | approve | not resolved: 1/4 fail-to-pass failing, 2/950 pass-to-pass broken | 13 tests, 11 fail before; mutants n/a; 10/13 pass on the maintainers' fix | 4 files +43 −3 | $0.42 | 2 min |
| Textualize-rich-3577 | Python | verified | 1 | approve | not resolved: 1/4 fail-to-pass failing, 2/950 pass-to-pass broken | 13 tests, 11 fail before; mutants n/a; 10/13 pass on the maintainers' fix | 4 files +43 −3 | $0.36 | 2 min |
| Textualize-rich-3577 | Python | verified | 1 | approve | not resolved: 1/4 fail-to-pass failing, 2/950 pass-to-pass broken | 13 tests, 11 fail before; mutants n/a; 10/13 pass on the maintainers' fix | 4 files +45 −3 | $0.34 | 2 min |
| unjs-defu-119 | TypeScript | verified | 1 | approve | **resolved** (1 fail-to-pass, 20 pass-to-pass) | 5 tests, 4 fail before; mutants n/a; 5/5 pass on the maintainers' fix | 5 files +25 −1 | $0.32 | 2 min |
| unjs-defu-119 | TypeScript | verified | 1 | approve | **resolved** (1 fail-to-pass, 20 pass-to-pass) | 5 tests, 4 fail before; mutants n/a; 5/5 pass on the maintainers' fix | 5 files +30 −1 | $0.27 | 2 min |
| unjs-defu-119 | TypeScript | verified | 1 | approve | **resolved** (1 fail-to-pass, 20 pass-to-pass) | 5 tests, 4 fail before; mutants n/a; 5/5 pass on the maintainers' fix | 5 files +25 −1 | $0.29 | 2 min |
| honojs-hono-4325 | TypeScript | verified | 1 | approve | not resolved: 1/2 fail-to-pass failing, 0/3582 pass-to-pass broken | 16 tests, 11 fail before; mutants n/a; 8/16 pass on the maintainers' fix | 3 files +71 −1 | $0.42 | 6 min |
| honojs-hono-4325 | TypeScript | verified | 1 | approve | not resolved: 1/2 fail-to-pass failing, 0/3582 pass-to-pass broken | 16 tests, 11 fail before; mutants n/a; 8/16 pass on the maintainers' fix | 3 files +83 −1 | $0.39 | 6 min |
| honojs-hono-4325 | TypeScript | verified | 1 | approve | not resolved: 1/2 fail-to-pass failing, 0/3582 pass-to-pass broken | 16 tests, 11 fail before; mutants n/a; 8/16 pass on the maintainers' fix | 3 files +83 −1 | $0.37 | 6 min |
| ardalis-GuardClauses-339 | C# | verified | 1 | approve | **resolved** (959 fail-to-pass, 0 pass-to-pass) | 17 tests, 17 fail before; mutants n/a; 17/17 pass on the maintainers' fix | 2 files +124 −2 | $0.55 | 3 min |
| ardalis-GuardClauses-339 | C# | verified | 1 | approve | **resolved** (959 fail-to-pass, 0 pass-to-pass) | 17 tests, 17 fail before; mutants n/a; 17/17 pass on the maintainers' fix | 2 files +125 −2 | $0.52 | 3 min |
| ardalis-GuardClauses-339 | C# | verified | 1 | approve | **resolved** (959 fail-to-pass, 0 pass-to-pass) | 17 tests, 17 fail before; mutants n/a; 17/17 pass on the maintainers' fix | 2 files +118 −3 | $0.78 | 966 min |
| jbogard-MediatR-1118 | C# | verified | 1 | approve | not graded: the fix's tests have no fail-to-pass test in this environment | 23 tests, 6 fail before; mutants n/a; 23/23 pass on the maintainers' fix | 3 files +185 −0 | $0.62 | 4 min |
| jbogard-MediatR-1118 | C# | verified | 1 | approve | not graded: the fix's tests have no fail-to-pass test in this environment | 23 tests, 6 fail before; mutants n/a; 23/23 pass on the maintainers' fix | 3 files +180 −0 | $0.56 | 4 min |
| jbogard-MediatR-1118 | C# | verified | 1 | approve | not graded: the fix's tests have no fail-to-pass test in this environment | 23 tests, 6 fail before; mutants n/a; 23/23 pass on the maintainers' fix | 3 files +190 −0 | $0.55 | 4 min |
| FluentValidation-FluentValidation-2240 | C# | verified | 1 | approve | **resolved** (2778 fail-to-pass, 0 pass-to-pass) | 45 tests, 45 fail before; mutants n/a; 0/45 pass on the maintainers' fix | 5 files +168 −0 | $0.68 | 6 min |
| FluentValidation-FluentValidation-2240 | C# | verified | 1 | approve | **resolved** (2778 fail-to-pass, 0 pass-to-pass) | 45 tests, 45 fail before; mutants n/a; 0/45 pass on the maintainers' fix | 5 files +210 −0 | $0.63 | 5 min |
| FluentValidation-FluentValidation-2240 | C# | verified | 1 | approve | **resolved** (2778 fail-to-pass, 0 pass-to-pass) | 45 tests, 45 fail before; mutants n/a; 0/45 pass on the maintainers' fix | 5 files +195 −2 | $0.63 | 5 min |
| App-vNext-Polly-2815 | C# | verified | 1 | approve | **resolved** (4 fail-to-pass, 6016 pass-to-pass) | 42 tests, 34 fail before; mutants 100 % killed; 42/42 pass on the maintainers' fix | 3 files +134 −2 | $0.49 | 16 min |
| App-vNext-Polly-2815 | C# | verified | 1 | approve | **resolved** (4 fail-to-pass, 6016 pass-to-pass) | 42 tests, 34 fail before; mutants 100 % killed; 42/42 pass on the maintainers' fix | 3 files +128 −2 | $0.49 | 13 min |
| App-vNext-Polly-2815 | C# | verified | 1 | approve | **resolved** (4 fail-to-pass, 6016 pass-to-pass) | 42 tests, 34 fail before; mutants 100 % killed; 42/42 pass on the maintainers' fix | 3 files +139 −2 | $0.42 | 12 min |

The issues, the PRPs, the patches and the hidden tests stay in the job folders and the eval cache, not in this repository.
