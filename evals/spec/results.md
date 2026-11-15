# Spec eval

2026-11-15, `claude-opus-5-5`, 14 cases × 1 runs; clarify ran first and its defaults were accepted. Delivered 14/14; accepted on the first submission 8, kept with warnings 0. Requirements checked by something executable: 108/124. On the real issues the touch points include the fix's files: 75 % on average, all of them in 7/10 runs. Cost 7.94 USD (list-price estimate; the runs were on a subscription), 375 s per PRP on average.

| Case | What it is | Requirements (executable / acceptance test) | Examples | Tasks | Fix's files in touch points | Submissions | Time |
|---|---|---|---|---|---|---|---|
| guardclauses-string-length | the guard exists; the default is to keep it and add the missing piece | 8 (7 / 6) | 7 | 1 |  | 1 | 67 s |
| hono-rate-limit | a new middleware with a pluggable store (clarify's default) | 13 (12 / 11) | 9 | 4 |  | 2 | 189 s |
| humanize-ru-sizes | Russian sizes through the library's locales (clarify's default) | 12 (11 / 10) | 8 | 3 |  | 1 | 117 s |
| itsdangerous-stronger-algorithm | the digest is configurable; the default decides the scope | 9 (8 / 7) | 8 | 4 |  | 2 | 193 s |
| ardalis-GuardClauses-339 | real issue | 8 (6 / 6) | 7 | 3 | 100 % | 1 | 64 s |
| App-vNext-Polly-2815 | real issue | 6 (6 / 5) | 6 | 2 | 100 % | 1 | 81 s |
| FluentValidation-FluentValidation-2240 | real issue | 12 (10 / 10) | 8 | 3 | 100 % | 2 | 166 s |
| jbogard-MediatR-1118 | real issue | 9 (6 / 5) | 6 | 4 | 50 % (missed `src/MediatR/Registration/ServiceRegistrar.cs`) | 1 | 112 s |
| honojs-hono-4325 | real issue | 9 (8 / 7) | 6 | 3 | 0 % (missed `src/router/reg-exp-router/node.ts`, `src/router/trie-router/node.ts`) | 2 | 3814 s |
| unjs-defu-119 | real issue | 7 (7 / 5) | 5 | 2 | 100 % | 2 | 106 s |
| pallets-itsdangerous-375 | real issue | 8 (7 / 6) | 6 | 3 | 100 % | 2 | 119 s |
| python-humanize-humanize-239 | real issue | 8 (7 / 6) | 7 | 2 | 100 % | 1 | 57 s |
| Textualize-rich-3577 | real issue | 9 (8 / 8) | 8 | 3 | 0 % (missed `rich/ansi.py`) | 1 | 111 s |
| spf13-cobra-2240 | real issue | 6 (5 / 5) | 5 | 3 | 100 % | 1 | 58 s |

The written tasks are synthetic (see `../clarify/tasks`); the issues are fetched from GitHub by `foreman eval localization` and are not stored here. The PRPs themselves stay in the job folders.
