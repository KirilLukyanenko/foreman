# Clarify eval

2026-11-17, `claude-opus-5-5` (intake on its default), 14 cases × 3 runs. Checks passed: **143/144**. On the real issues (fixed as written): 0.1 questions per run on average, 28/30 runs asked nothing. Cost 9.60 USD (list-price estimate; the runs were on a subscription).

| Case | What it tests | Verdicts | Asked / assumed / from code per run | Checks | Failed |
|---|---|---|---|---|---|
| guardclauses-string-length | asks for a guard the library already has (StringTooLong) | underspecified, underspecified, underspecified | 1/1/1; 1/1/2; 1/1/3 | 12/12 |  |
| hono-rate-limit | a feature with real open choices: storage across runtimes, the key, headers, limits | underspecified, underspecified, underspecified | 1/2/3; 2/1/2; 1/3/1 | 15/15 |  |
| humanize-ru-sizes | Russian client; 1000 or 1024 per KB, and whether through the library's locales or a new option | underspecified, underspecified, underspecified | 1/3/1; 1/2/2; 1/2/2 | 15/15 |  |
| itsdangerous-stronger-algorithm | the digest is already configurable; the real question is whether to change the default | underspecified, underspecified, underspecified | 1/2/1; 1/1/1; 1/1/1 | 12/12 |  |
| ardalis-GuardClauses-339 | real issue | clear, clear, clear | 0/1/3; 0/4/1; 0/1/2 | 9/9 |  |
| App-vNext-Polly-2815 | real issue | clear, clear, clear | 0/1/2; 0/1/2; 0/2/2 | 9/9 |  |
| FluentValidation-FluentValidation-2240 | real issue | clear, clear, clear | 0/3/2; 0/4/2; 0/3/2 | 9/9 |  |
| jbogard-MediatR-1118 | real issue | underspecified, underspecified,  | 1/2/1; 1/4/0; 0/0/0 | 8/9 | #3 accepted cleanly = gaps must be an array of gap objects (it may be empty) |
| honojs-hono-4325 | real issue | clear, clear, clear | 0/1/2; 0/2/1; 0/1/2 | 9/9 |  |
| unjs-defu-119 | real issue | clear, clear, clear | 0/2/2; 0/2/2; 0/2/2 | 9/9 |  |
| pallets-itsdangerous-375 | real issue | clear, clear, clear | 0/2/2; 0/3/1; 0/2/2 | 9/9 |  |
| python-humanize-humanize-239 | real issue | clear, clear, clear | 0/2/2; 0/1/2; 0/2/1 | 9/9 |  |
| Textualize-rich-3577 | real issue | clear, clear, clear | 0/2/1; 0/4/0; 0/2/2 | 9/9 |  |
| spf13-cobra-2240 | real issue | clear, clear, clear | 0/1/2; 0/1/2; 0/2/2 | 9/9 |  |

## Questions asked

- **guardclauses-string-length** #1
  - Q1 (high) The library already has this check: Guard.Against.StringTooLong(userName, 50) throws an ArgumentException when a string is longer than 50 characters, and it already has tests. Is that what you need, or is something missing from it?
- **guardclauses-string-length** #2
  - Q1 (high) The library already has a guard for this: Guard.Against.StringTooLong(userName, 50) throws when the string is longer than 50 characters, and it already has tests. Is that enough for you, or do you need something it doesn't do yet?
- **guardclauses-string-length** #3
  - Q1 (high) The library already has this guard: Guard.Against.StringTooLong(input, maxLength), which throws an ArgumentException when the string is longer than the maximum. Do you need anything beyond it?
- **hono-rate-limit** #1
  - Q1 (medium) The Hono documentation website is kept in a separate repository. Should the docs be just the in-code documentation, or should we also write the page for the hono.dev website?
- **hono-rate-limit** #2
  - Q1 (high) Where should the request counts be kept? An in-memory store only works within a single server process. For it to work across several instances or on Cloudflare Workers, you need a pluggable store (for example Redis or KV).
  - Q2 (medium) Hono's middleware docs live on the hono.dev website, which is a separate repository. Should I also write a docs page for the website, or are code comments and usage examples inside this repository enough?
- **hono-rate-limit** #3
  - Q1 (high) Do you only need the limit to work on a single server (counts kept in memory), or must it also hold across several servers / serverless instances (counts kept in a shared store like Redis or Cloudflare KV that you plug in)?
- **humanize-ru-sizes** #1
  - Q1 (high) Русские размеры должны включаться через стандартный переключатель языка библиотеки (один вызов humanize.i18n.activate("ru_RU") при старте вашего сервиса) или стать поведением по умолчанию для всех без всяких настроек?
- **humanize-ru-sizes** #2
  - Q1 (high) В ваших примерах «10 КБ» и «3 ГБ» без дробной части, а «1,5 МБ» с ней. Сейчас библиотека всегда печатает один знак после запятой, то есть «10,0 КБ». Убирать «,0» у целых значений?
- **humanize-ru-sizes** #3
  - Q1 (high) Как включать русский вывод? Мы предлагаем стандартный механизм библиотеки: в сервисе один раз вызывается humanize.i18n.activate("ru_RU"), и naturalsize начинает писать «КБ», «МБ». Но тогда на русский переключатся и другие функции humanize, если вы их используете, например «3 секунды назад». Это подходит?
- **itsdangerous-stronger-algorithm** #1
  - Q1 (high) itsdangerous can already sign with SHA-256 or SHA-512 if you pass that option when creating the signer. Should we change the library default away from SHA-1, or keep the default and make the stronger option documented, tested and easy to switch to? Changing the default means tokens you have already issued stop being valid, unless SHA-1 is kept as a fallback for checking them.
- **itsdangerous-stronger-algorithm** #2
  - Q1 (high) itsdangerous can already sign with SHA-256 or SHA-512 if you pass a setting (digest_method), but the built-in default is still SHA-1. Changing the default would make every token already issued stop verifying unless SHA-1 is still accepted as a fallback. Should we keep the default as it is and document and test how to switch to the stronger option, or change the library default?
- **itsdangerous-stronger-algorithm** #3
  - Q1 (high) itsdangerous can already sign with SHA-256 if you pass it as an option. Do you want SHA-256 to become the default for everyone? That would make all existing signed tokens invalid unless old SHA-1 tokens are explicitly accepted as a fallback.
- **jbogard-MediatR-1118** #1
  - Q1 (high) On the default Microsoft container, a handler for a base event only runs for derived events because MediatR registers it a second time, and that second registration is what DryIoc runs twice. Should we keep the registrations and skip duplicate handlers when publishing, or remove the extra registration (which stops base-event handlers from running for derived events on the Microsoft container)?
- **jbogard-MediatR-1118** #2
  - Q1 (high) MediatR deliberately registers a base-event handler (C1) for derived events (E2), because the default Microsoft container would not call C1 for E2 otherwise. DryIoc already does this matching itself, so C1 runs twice. How should we fix it?

The written tasks are synthetic, made for this eval; the issues are fetched from GitHub by `foreman eval localization` and are not stored here.
