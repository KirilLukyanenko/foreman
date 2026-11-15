# Implement eval

2026-11-15, implementer `claude-opus-5-5`, 9 real fixed issues, one run each. Every case went through the whole pipeline at the commit before its fix; the PRP was marked Ready by the harness instead of a human. **Verified** is Foreman's own gate (tests against the baseline, the new tests' fail-to-pass, the diff guard, lint, secrets, the reviewer). **Resolved** is the maintainers' own tests from the fix, applied over Foreman's change (SWE-bench style: every FAIL_TO_PASS and PASS_TO_PASS test passes); no agent ever saw them.

Resolved 6/8 graded; verified by the gate 9/9; verified and resolved 6, verified but not resolved 2, not verified but resolved 0. Implementing and reviewing cost 5.21 USD, planning (environment agent, spec) 5.89 USD (list-price estimates; the runs were on a subscription).

| Case | Language | Gate | Rounds | Review | Hidden tests | Change | Cost | Time |
|---|---|---|---|---|---|---|---|---|
| python-humanize-humanize-239 | Python | verified | 1 | approve | **resolved** (2 fail-to-pass, 603 pass-to-pass) | 2 files +31 −2 | $0.41 | 5 min |
| pallets-itsdangerous-375 | Python | verified | 2 | approve | **resolved** (297 fail-to-pass, 0 pass-to-pass) | 4 files +99 −8 | $0.79 | 7 min |
| Textualize-rich-3577 | Python | verified | 1 | approve | not resolved: 1/4 fail-to-pass failing, 0/950 pass-to-pass broken | 4 files +37 −2 | $0.43 | 9 min |
| unjs-defu-119 | TypeScript | verified | 1 | approve | **resolved** (1 fail-to-pass, 20 pass-to-pass) | 5 files +48 −1 | $0.40 | 5 min |
| honojs-hono-4325 | TypeScript | verified | 1 | approve | not resolved: 1/2 fail-to-pass failing, 0/3582 pass-to-pass broken | 3 files +69 −1 | $0.49 | 17 min |
| ardalis-GuardClauses-339 | C# | verified | 1 | approve | **resolved** (959 fail-to-pass, 0 pass-to-pass) | 2 files +125 −3 | $0.53 | 6 min |
| jbogard-MediatR-1118 | C# | verified | 1 | approve | not graded: the fix's tests have no fail-to-pass test in this environment | 5 files +185 −3 | $0.63 | 12 min |
| FluentValidation-FluentValidation-2240 | C# | verified | 1 | approve | **resolved** (2778 fail-to-pass, 0 pass-to-pass) | 5 files +222 −0 | $0.78 | 19 min |
| App-vNext-Polly-2815 | C# | verified | 1 | approve | **resolved** (4 fail-to-pass, 6016 pass-to-pass) | 6 files +177 −2 | $0.74 | 24 min |

The issues, the PRPs, the patches and the hidden tests stay in the job folders and the eval cache, not in this repository.
