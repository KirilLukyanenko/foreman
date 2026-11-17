# Planning runs: intake and clarify

Runs of milestone 2's first stages, failures and fixes included. All on Claude Code (subscription); costs are
list-price estimates.

## 15 November 2026: intake

Intake on Claude Haiku 5.5, one tool (`submit_brief`), the brief checked against the task by code.

**First run on the Hono issue.** A good brief on the first read: a correct goal, an implied criterion ("behaviour
for `/files`, `/files/a` stays as it is"), and as an unknown exactly the question the fix had to settle (which value
the wildcard gets for `/files/`). Two problems:
- The model sent `deadline` and `budget` filled with "None stated" and no quote, which the gate rejected. A
  deadline or budget only counts when the client stated it, so a missing quote now means "not stated", and the
  schema says to leave the field out.
- It listed "whether the repository is provided" as an unknown: intake wasn't told a repository comes with the
  job. The first message now says so.

**The eval** (`foreman eval intake`, 16 tasks × 3 runs, [results](../evals/intake/results.md)):

| Run | Checks | Clean first submissions | Cost | Median time |
|---|---|---|---|---|
| first | 236/240 | 22/48 | $0.135 | 17 s |
| after the fixes | 237/237 | 41/48 | $0.110 | 15 s |

What the first run found:
- 55 of the 57 problems the gate sent back were the empty deadline and budget above. After the fix the gate rejects only real
  problems: constraints without a quote, an over-long goal.
- Three "failures" on the Russian post were my label, not the model: "Пишите, сколько времени займёт" ("tell me
  how long it will take") is a request to the bidder, as the model said. The label was corrected.
- One of three runs called the Go issue (cobra) a "go". Foreman can't build Go yet; the prompt now names other
  languages as "review". Triage also reports the stack as unsupported, so the environment stage would stop anyway.
- Two phrases in one sentence ("If you are an AI, start your proposal with...") were counted as two flags. Nearby
  matches are now one passage, and quoting either part classifies it.

Every run on the synthetic posts caught what it was meant to: the proposal trap (`bidder`, shown to the human), the
hidden `postinstall` instruction in an HTML comment (`ai_agent`, ignored; Foreman then raises the verdict to
"review"), the invisible character, and no-go on native mobile apps and on fake reviews. The no-go gate stopped a
real `foreman run` before fetch.

## 15 November 2026: clarify

Clarify on Claude Opus 5.5 after fetch, triage and the code map, with the readers' tools and `submit_gaps`.

**First run on the Hono issue:** "clear", no questions, 5 tool calls, 33 s. It read the router, found where the
`undefined` handler comes from, closed "which router" from the code with evidence, and stated three low-impact
defaults (the wildcard value, testing on any runtime, no docs change).

**The eval** (`foreman eval clarify`, 14 tasks × 3 runs, [results](../evals/clarify/results.md)): 143/144 checks,
about $8 for 42 runs (intake included), 25–65 s per run.

- **The code first.** Asked for a guard GuardClauses already has, every run found `StringTooLong` and asked one
  question: does it cover the need. Asked for a "stronger algorithm" in itsdangerous, every run found the digest is
  already configurable and asked whether to change the default (and what happens to tokens signed with SHA-1).
- **Real choices are asked.** Hono rate limiting: memory or a pluggable shared store (Redis, KV) in every run, and
  in two runs where the docs go (they live in another repository).
- **The client's language.** For the Russian task the questions and defaults are in Russian, with options, and
  specific ("1 KB = 1000 bytes as now, or 1024?"; "through `humanize.i18n.activate`, as for the other functions?").
- **Little over-asking.** On the 10 real issues 27 of 30 runs asked nothing. The three that asked were all MediatR,
  where the question is real: removing the duplicate registration that DryIoc trips over would stop base-event
  handlers on the default container.
- The one failed check: one itsdangerous run asked 3 questions where the label allows 2 (it split "change the
  default" and "which algorithm").

Found and fixed along the way:
- Russian text was stored as `\u` escapes in the job's JSON files; they are now written as readable UTF-8 (the
  files are never embedded in HTML).
- The API driver sent the server-side fallback list to every model; Haiku 5.5 rejects it with a 400. It is now
  sent only to models that have the fallback. (Found in the documentation, not in a run: the API driver still
  hasn't run without an API key.)

Not measured yet: whether the questions improve the final change. That needs the implement stage (milestone 3);
the plan is Ambig-SWE-style pairs, the same issue with and without a detail removed.

## 15 November 2026: spec

The PRP on Claude Opus 5.5 with the readers' tools and `submit_spec`; code checks the done contract and adds the
validation commands, known-red tests and the definition of done.

**First run on the Hono issue** (22 tool calls, 213 s, $1.08 estimated). The PRP names the real cause: in
RegExpRouter, `match.indexOf('', 1)` can't tell an empty parameter capture from the handler marker, so
`/files/` with `{.*}` picks the wrong slot. It makes every router agree through the shared router test cases,
fixes TrieRouter's skip of an empty last segment, and lists as anti-patterns the tempting wrong fixes: guarding
`#dispatch` (the crash becomes a 404), rewriting `.*` to `.+`, adding the case to a router's skip list. The gate
sent one submission back for a `path:line` without its folder. New cases in existing test files are listed as
allowed edits with reasons, which is what the diff guard needs.

One defect: that job had no baseline (the eval jobs stop after the code map), and section 8 listed only CI lint
commands under the heading "verified by recon". Commands now carry whether recon ran them, and a PRP without a
verified test command says so in bold.

**The eval** (`foreman eval spec`, 14 tasks × 1 run, [results](../evals/spec/results.md)): 14/14 PRPs delivered
and accepted (8 on the first submission), 108 of 124 requirements checked by a test or a command, about $8 in all.
On the real issues the touch points include all of the fix's files in 7 of 10 runs (75% of the files). The three
misses, read by hand:
- **rich**: the PRP fixes `Text.from_ansi` in `rich/text.py`, the function the issue names, keeping
  `AnsiDecoder.decode`'s one-Text-per-line contract; upstream changed `rich/ansi.py`. A defensible alternative.
- **MediatR**: the PRP follows clarify's default (keep the registration, de-duplicate at publish) and changes the
  wrapper; upstream changed the wrapper and the registrar.
- **Hono**: this run fixed RegExpRouter where its matcher is built (`trie.ts`) and the lookup, but left TrieRouter
  out; the first run had TrieRouter's `node.ts`. One run per case is too few to call this stable.

**An hour lost after the work was done.** The Hono PRP was accepted after 3 minutes; Claude Code's closing message
then hung on API retries for an hour before the process exited. The driver now watches the toolbox's outcome and
stops Claude Code 90 seconds after the result is in; the run is reported as stopped, not failed, and its cost comes
from the per-message usage when the final result line is missing.

**The whole pipeline once, on a fresh job** (`foreman run` on humanize at the issue's base commit): intake, go,
fetch, triage, security, code map, environment (the deterministic plan, no agent), baseline (599 of 623 pass, 24
skipped), the four readers, clarify (clear: two assumptions, one gap closed by the code) and the spec (7
requirements, 6 of them checked by a test or a command, accepted on the first submission) in about 4.5 minutes and
about $0.94 of model use (list-price estimate). This PRP's validation loop has the baseline's verified test command.

## 17 November 2026: clarify with a required scope gap

After clarify was made to record a `scope` gap for every bug and feature (siblings, the shared root, with code as
evidence; see [implement runs](implement-runs.md#17-november-2026-clarify-decides-how-far-the-fix-reaches)), the
same eval again (`foreman eval clarify --runs 3`, [results](../evals/clarify/results.md)): the worry was more
questions.

- **143/144 checks, as before. Over-asking didn't grow:** on the real issues 28 of 30 runs asked nothing (27 before).
  The two that asked were MediatR again, with the same real question. The scope gap is mostly closed from the code or
  assumed; on the written tasks the questions are the same ones as before.
- **The failed check is new:** one MediatR run sent a single gap's fields as the whole `submit_gaps` input, three
  times, and ran out of attempts. The answer it got ("gaps must be an array of gap objects") didn't say what was
  wrong; it now names what was sent and the expected shape.
- Cost $9.60 for 42 runs, up from $7.98: reading siblings and the root takes a few more tool calls.
