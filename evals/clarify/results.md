# Clarify eval

2026-11-15, `claude-opus-5-5` (intake on its default), 14 cases × 3 runs. Checks passed: **143/144**. On the real issues (fixed as written): 0.1 questions per run on average, 27/30 runs asked nothing. Cost 7.98 USD (list-price estimate; the runs were on a subscription).

| Case | What it tests | Verdicts | Asked / assumed / from code per run | Checks | Failed |
|---|---|---|---|---|---|
| guardclauses-string-length | asks for a guard the library already has (StringTooLong) | underspecified, underspecified, underspecified | 1/2/2; 1/1/2; 1/1/1 | 12/12 |  |
| hono-rate-limit | a feature with real open choices: storage across runtimes, the key, headers, limits | underspecified, underspecified, underspecified | 1/2/2; 2/3/3; 2/1/2 | 15/15 |  |
| humanize-ru-sizes | Russian client; 1000 or 1024 per KB, and whether through the library's locales or a new option | underspecified, underspecified, underspecified | 2/1/3; 1/3/2; 1/3/1 | 15/15 |  |
| itsdangerous-stronger-algorithm | the digest is already configurable; the real question is whether to change the default | underspecified, underspecified, underspecified | 1/1/2; 3/1/1; 1/2/1 | 11/12 | #2 at most 2 questions = 3 |
| ardalis-GuardClauses-339 | real issue | clear, clear, clear | 0/1/3; 0/2/2; 0/4/2 | 9/9 |  |
| App-vNext-Polly-2815 | real issue | clear, clear, clear | 0/1/3; 0/1/2; 0/2/3 | 9/9 |  |
| FluentValidation-FluentValidation-2240 | real issue | clear, clear, clear | 0/2/3; 0/4/1; 0/4/1 | 9/9 |  |
| jbogard-MediatR-1118 | real issue | underspecified, underspecified, underspecified | 1/3/1; 1/3/0; 1/3/0 | 9/9 |  |
| honojs-hono-4325 | real issue | clear, clear, clear | 0/2/1; 0/3/0; 0/2/1 | 9/9 |  |
| unjs-defu-119 | real issue | clear, clear, clear | 0/2/2; 0/2/1; 0/2/2 | 9/9 |  |
| pallets-itsdangerous-375 | real issue | clear, clear, clear | 0/3/0; 0/3/1; 0/2/2 | 9/9 |  |
| python-humanize-humanize-239 | real issue | clear, clear, clear | 0/1/2; 0/2/1; 0/2/1 | 9/9 |  |
| Textualize-rich-3577 | real issue | clear, clear, clear | 0/4/1; 0/3/1; 0/4/0 | 9/9 |  |
| spf13-cobra-2240 | real issue | clear, clear, clear | 0/2/1; 0/2/1; 0/2/3 | 9/9 |  |

## Questions asked

- **guardclauses-string-length** #1
  - Q1 (high) The library already has this guard: Guard.Against.StringTooLong(userName, 50) throws when the name is longer than 50 characters, and it already has tests. Is that what you need, or do you want something different?
- **guardclauses-string-length** #2
  - Q1 (high) The library already has this guard: Guard.Against.StringTooLong(userName, 50) throws when a string is longer than 50 characters, and it already has tests. Does that cover what you need, or do you want it to work differently?
- **guardclauses-string-length** #3
  - Q1 (high) The library already has a guard for this, Guard.Against.StringTooLong(value, maxLength). Was something about it not working for you, or should I just add tests for the 50-character case and document it?
- **hono-rate-limit** #1
  - Q1 (high) Should request counts be kept only in the server's memory, or should users also be able to plug in shared storage (such as Redis or Cloudflare KV) so limits work across several servers?
- **hono-rate-limit** #2
  - Q1 (high) Should the rate limiter only count requests in the server's own memory, or should it also let you plug in shared storage (for example Redis or Cloudflare KV) so limits hold across several servers?
  - Q2 (medium) Hono's middleware docs are hosted on hono.dev, which is a separate repository. Should the docs be JSDoc in the code plus a ready-to-paste docs page in the pull request, or do you also want a separate pull request to the website repository?
- **hono-rate-limit** #3
  - Q1 (high) Should the rate limiter just count requests in the server's memory, or should it also let you plug in a shared store (like Redis or Cloudflare KV) so the limit holds across several servers or edge locations?
  - Q2 (medium) Hono's documentation website is kept separately from the main code. Should I also write the website page for the rate limiter, or are code comments with examples enough?
- **humanize-ru-sizes** #1
  - Q1 (high) В ваших примерах «10 КБ» и «3 ГБ» написаны без дробной части. Убирать «,0» у целых чисел, то есть писать «10 КБ», а не «10,0 КБ»?
  - Q2 (medium) Считать 1 КБ = 1000 байт (как сейчас в библиотеке) или 1 КБ = 1024 байта?
- **humanize-ru-sizes** #2
  - Q1 (high) Как включать русский вывод? Обычно в humanize язык включают один раз вызовом humanize.i18n.activate("ru_RU"), а по умолчанию остаётся английский. Или вам нужно, чтобы naturalsize всегда выводил по-русски, без всяких настроек?
- **humanize-ru-sizes** #3
  - Q1 (high) Вам подойдёт, если русские размеры включаются вызовом humanize.i18n.activate("ru_RU") (как это уже сделано для дат и чисел в humanize), или naturalsize должен всегда выводить по-русски без всяких настроек?
- **itsdangerous-stronger-algorithm** #1
  - Q1 (high) itsdangerous can already sign with stronger algorithms like SHA-256 if you pass a setting, but the default is still SHA-1. Should we change the default for everyone, which breaks tokens that already exist unless we also accept old SHA-1 tokens, or keep the default and make the stronger option easier to find and use?
- **itsdangerous-stronger-algorithm** #2
  - Q1 (high) itsdangerous can already sign with SHA-256 or SHA-512 if you pass it as an option. Do you want the library's default changed so that everyone gets the stronger hash without configuring anything?
  - Q2 (high) After the switch, should tokens already signed with SHA-1 still be accepted automatically, or should they be rejected unless you configure a fallback yourself?
  - Q3 (medium) Which algorithm should replace SHA-1: SHA-256 (shorter tokens) or SHA-512 (longer tokens)?
- **itsdangerous-stronger-algorithm** #3
  - Q1 (high) The library can already sign with stronger algorithms (e.g. SHA-256/SHA-512) via a setting, but the default is still SHA-1. What do you want changed: make the stronger algorithm the new default for everyone, or keep the default and just make the stronger option easier to use and documented?
- **jbogard-MediatR-1118** #1
  - Q1 (high) Today, a handler for a base event also runs for derived events on the default .NET container because MediatR registers it for both. Removing that extra registration fixes DryIoc, but on the default container C1 would then stop handling E2. Should we keep that behaviour and instead make sure each handler runs only once per publish, whatever the container?
- **jbogard-MediatR-1118** #2
  - Q1 (high) With the default Microsoft container, a handler for a base event only runs for a derived event because MediatR adds that extra registration. Removing it would stop that from working there. Should we keep the registration and remove the duplicate when the event is published, so each handler runs once on every container?
- **jbogard-MediatR-1118** #3
  - Q1 (high) MediatR adds the extra registration on purpose: with the standard .NET container it is the only way a handler for a base event (C1 for E1) also runs for a derived event (E2). Removing it would stop C1 running for E2 at all on the standard container. The double call comes from DryIoc adding its own matching on top. What should we deliver?

The written tasks are synthetic, made for this eval; the issues are fetched from GitHub by `foreman eval localization` and are not stored here.
