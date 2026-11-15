# Foreman architecture

Foreman takes a software task (a freelance job post, a ticket, an email) and a link to an unfamiliar repository,
and drives AI agents to a verified change: a branch, a PR description in the repository's own style, and a report
for the client, with the cost and time of every step. A human decides at three points: which questions go to the
client, whether the spec is ready, and whether the result is sent.

Status: design, 14 November 2026. Each capability below is marked **now** (in the first working version),
**later** (planned, with the reason it waits) or **no** (considered and rejected, with the reason).

## 1. Principles

1. **A pipeline, not a chat of agents.** The orchestrator is ordinary code: a state machine. Each stage is one
   agent (or none) with a narrow job, its own tools and permissions, and a file as its output. The next stage reads
   files, not the previous agent's conversation. Parallel agents only read; exactly one agent writes code at a time.
   Sources: Anthropic, [Building effective agents](https://www.anthropic.com/engineering/building-effective-agents);
   Cognition, [Don't build multi-agents](https://cognition.ai/blog/dont-build-multi-agents) and
   [Multi-agents working](https://cognition.com/blog/multi-agents-working).
2. **The agent never decides it is done.** Gates run by the orchestrator decide. A separate, skeptical reviewer
   with no shared context checks the work against the spec
   ([Anthropic harness design](https://www.anthropic.com/engineering/harness-design-long-running-apps)).
3. **The task text and the repository are data, not instructions.** Nothing read from them can widen permissions.
4. **Every number is measured and reproducible.** Model ids, CLI and SDK versions, image digests and prompt hashes
   are recorded with every run. Failed runs stay in the log.
5. **Deterministic first, model second.** Whatever can be detected or computed without a model is; the model gets
   the hard part with the facts already gathered.

## 2. Pipeline

```
 task text + repository
        │
 0  Intake ─────────────▶ brief.json         goal, deliverables, acceptance hints, task type, unknowns, go/no-go
    Go (no model) ──────▶ go.json            stops on a no-go unless the human says --go
 1  Recon
    1a Triage (no model) ─▶ triage.json        stacks, CI commands, instruction files, install scripts, git facts
                            security.json      secrets, vulnerable dependencies, SAST findings
    1b Environment ──────▶ setup.sh, test.sh   agent makes the repo build and run its tests (RepoLaunch-style)
    1c Baseline (no model) ▶ baseline.json     3 runs, network off: per-test pass/fail/flaky/known-red
    1d Code map (no model) ▶ repomap.md        tree-sitter + PageRank, personalised to the task; Roslyn for C#
    1e Dossier ──────────▶ dossier.md          4 read-only readers: architecture, conventions, task location, risks
                            rules/AGENTS.md     minimal agent rules, kept outside the client repository
 2  Clarify ────────────▶ questions.md ─▶ [human sends to client, records answers]
 3  Spec ───────────────▶ PRP with a "done" contract per item ─▶ [human: Ready]
 4  Acceptance ─────────▶ hidden tests       separate session, sees only the PRP; tests are themselves tested
        │  found gaps ─▶ back to 3
 5  Implement ──────────▶ N candidate branches   one writer per candidate, each in its own sandbox
 6  Verify (orchestrator) ▶ verify.json      pass-to-pass, fail-to-pass, lint, diff guard, scans, reviewer
        │  fails ─▶ back to 5 with the findings, at most 2 rounds; then stop and report
 7  Select + Deliver ───▶ pr.md, report.md ─▶ [human: send]
 8  Metrics ────────────▶ runs.jsonl + traces
```

## 3. Stages

### 0. Intake (built)
Model: Claude Haiku 5.5. Turns free text into `intake/brief.json` (`job.json` is the job's manifest): goal,
deliverables, constraints, deadline, budget, explicit and implied acceptance criteria, task type (bug, feature,
refactor, infrastructure, UI, docs), the client's language, unknowns, what Foreman can't do itself (calls,
production access, publishing), text in the task aimed at an AI or at the bidder, and a go/no-go estimate with
risk. Intake needs no repository, so it runs before recon spends anything.

- **Code first.** `TaskFacts` finds links, file paths, code spans, stack traces, invisible and bidirectional
  characters, and phrases aimed at an AI or at whoever replies ("if you are an AI, start your proposal with...",
  "ignore previous instructions", "you must run..."). The task goes to the model between a delimiter derived from
  its hash, so the text can't close its own tag.
- **One checked tool instead of structured output.** The brief is the input of a single `submit_brief` tool. A
  JSON schema guarantees only the shape; the gate needs the meaning: every explicit deliverable, constraint,
  deadline, budget and criterion must quote the task word for word (compared after folding case, quotes, dashes,
  Markdown and whitespace; `…` skips words), and every phrase code flagged must be classified (`bidder`: shown to
  the human writing the proposal; `ai_agent`: ignored; `none`: a false alarm). Problems go back to the model, as
  in the dossier; a third attempt is kept with warnings. The same tool works on both drivers.
- **Code has the last word on caution.** The model's verdict can only be raised: invisible characters or text
  aimed at an AI agent make a "go" a "review". The `go` stage stops the pipeline on a no-go unless the human
  passes `--go`.

Measured on 16 labelled tasks × 3 runs (`foreman eval intake`, [results](../evals/intake/results.md)): 237/237
checks, 41 of 48 runs accepted on the first submission, about $0.002 and 15 s per task.

### 1. Recon (built: 1a–1e and the security scans)

Recon is valuable on its own ("I understood your project in ten minutes") and is the first milestone.

**1a. Triage, no model.**
- Copy or clone the repository into the job's sandbox volume: shallow at the base commit, no future history
  (agents have been caught reading the fix from later commits), submodules off, hooks off. Remove `.claude/`,
  `.mcp.json` and other agent configuration from the copy the agents work in: Claude Code has had CVEs where a
  repository's settings ran code before the trust dialog (CVE-2025-59536, CVE-2026-21852).
- Detect stacks (.NET, Node, Python now; others reported as unsupported), SDK pins (`global.json`, `engines`,
  `requires-python`), package managers and lockfiles, test frameworks and runners.
- Read CI workflows: the `run:` steps are the best hint for the real build and test commands. Also
  `.devcontainer/devcontainer.json`, Dockerfiles, compose files, `.tool-versions`, `mise.toml`.
- Collect instruction files as data: `AGENTS.md`, `CLAUDE.md`, `.cursor/rules`, `.cursorrules`,
  `.github/copilot-instructions.md`, `CONTRIBUTING*`, `.editorconfig`, analyzer and lint configs. Flag invisible
  and bidirectional Unicode (the "rules file backdoor").
- Flag install-time code before anything is installed: npm `preinstall`/`install`/`postinstall`/`prepare`,
  `setup.py`, MSBuild `<Exec>`.
- Git facts: commit style (conventional or not), age, activity.
- **Security (built, the `security` stage).** gitleaks over the files and the cloned history (values redacted;
  only rule, file, line and commit are kept, never the match, author or email), `osv-scanner` over manifests and
  lockfiles in `--offline` mode, and Opengrep with GitLab's MIT-licensed `sast-rules` (the Semgrep registry
  rules and `opengrep-rules` are under licences that restrict commercial use, so they are not used). One image, `tools/security`, every
  binary and the rules pinned by version and SHA-256; rules that this Opengrep version can't parse are dropped at
  build and listed. The scan runs as the unprivileged user with no network. The OSV database is refreshed by a
  separate container that has network and no repository (only ecosystem names go out, never the dependency list)
  into a shared volume, with a conditional download. Ignore files a repository could use to hide its findings
  (`.gitleaksignore`, `.semgrepignore`...) are removed from the scan copy and reported. Findings are flagged by
  where they are (test paths, dev-only lockfiles, version ranges that can't be confirmed); the risks reader checks
  them against the code, and the rules file says what an implementer must not do (print a secret, add a
  vulnerable version). They are the "before" picture that Verify compares a change against.

**1b. Environment (built).** Following [RepoLaunch](https://arxiv.org/abs/2603.05026) (build success on Linux:
C# 92%, JS/TS 89%, Python 76%), with one change: verification is code, not a second model.

1. The deterministic plan from triage runs first, in a fresh copy. If it yields per-test results, no model is used.
2. Otherwise the environment agent gets four tools and nothing else:
   `run` (a command in a long-running sandbox container, root, network on), `use_image` (switch to another official
   image), `submit` (setup script + test script) and `give_up`. The model never touches the host.
3. `submit` is verification: Foreman runs the setup script from scratch in a fresh copy of the repository (root,
   network on), saves the container as an image (the "setup script, then snapshot" pattern of Codex, Jules and
   Copilot: system packages live outside the volume), then runs the test script on that snapshot as an
   unprivileged user with the network off, and parses the report. Accepted only with per-test results; if fewer
   than half pass, the agent must say why the failures are not about the environment. A setup that only worked
   because of something done by hand in the session fails here.
4. Two interchangeable model loops ("drivers") behind the same tools:
   - **api**: our own loop on the Anthropic C# SDK: prompt caching on the system prompt and tools, effort per
     stage, server-side refusal fallbacks, thinking blocks returned unchanged, OpenTelemetry GenAI spans.
   - **claude-code**: Claude Code headless on a subscription, with all built-in tools disabled (`--tools ""`, an
     explicit deny list, `dontAsk`), our system prompt instead of its own, no user or project settings, an empty
     working folder, and the four tools served by Foreman's own MCP server (`foreman mcp-environment`, stdio).
   The same task can run on both, which makes them an ablation, not a lock-in.
5. Every tool call is logged (`tools.jsonl`), the prompt is a file whose hash is recorded, and the run summary keeps
   turns, tool calls, tokens, cache reads and writes, cost (estimated for the subscription driver) and time.

A human-supplied setup script will override everything (later).

**1c. Baseline, no model (built).** The accepted environment is set up once more from scratch (which shows it is
reproducible), its last snapshot is kept as the job's environment image for the later stages, and the test command
runs 3 times with the network off. Results are parsed from TRX,
JUnit XML or Jest JSON into one record per test. A test whose result differs between runs is **flaky** and leaves
the pass-to-pass gate; a test that fails every time is **known-red** and the implementer is not blamed for it.
No runnable baseline means recon fails clearly, with the reason.

**1d. Code map, no model (built).** Aider's repository map, rebuilt in .NET: definitions and references per file,
a file graph weighted by Aider's rules (names the task mentions ×10, long specific names ×10, private names and names
defined in many files ×0.1), PageRank, rank flowing from files to the definitions they use, and the best definitions
printed with signatures and line numbers until a token budget (3,000 by default) is full. Two extractors:
- **C#: Roslyn**, with references resolved to the exact symbol (`T:Shop.Core.Order`, not every class named
  `Order`). Safe on untrusted code because nothing runs: project files are read as XML (no MSBuild evaluation),
  each project is a compilation of its own files against the framework and the projects it references, no
  analyzers or source generators are loaded, nothing is restored or emitted.
- **Everything else: tree-sitter** in the sandbox (`tools/codemap`, tree-sitter-language-pack with the grammars'
  own tags queries, 15 languages), as the unprivileged user with no network: parsers are native code fed untrusted
  input, so they stay off the host.

Generated files (by name and by marker) and vendored folders are left out of the map and listed for the rules file;
test definitions rank at a quarter. With a task, a second map is personalised to it, and BM25 of the task over the
files is saved as the task reader's first candidates. No vector index in v1: agentic search is a strong baseline
(Claude Code dropped RAG for it); an embedding index is a later ablation.

Measured on 10 real fixed issues (`foreman eval localization`, [results](../evals/localization/results.md)): for
*finding the files a fix changes*, BM25 beats the map, and personalising the map (exact mentions, task words, BM25
restart points) doesn't close the gap. The map is therefore context for agents (what exists and how it connects),
not the localizer; localization is the task reader's job, starting from BM25's candidates.

**1e. Dossier (built).** Four read-only readers in parallel (Claude Sonnet 5.5 by default), each writing one
section: architecture, conventions, where the task lives (files and symbols, localized from the inside out as in
[Agentless](https://arxiv.org/abs/2407.01489)), risks. Their tools: `list_files`, `read_file`, `search` (the
non-backtracking regex engine, so a model's pattern can't hang it), `code_map` (re-ranked for any query),
`find_symbol` (from the index) and `submit`. Paths are confined to the repository's files. `submit` is checked by
code: every cited file and line, every `path:line` in the text, and every symbol named as a task location must
exist; otherwise the problems go back to the reader (a third attempt is kept, with the problems shown as warnings).
The readers run on either driver, through the same toolbox.

The rules file for the implementer (`dossier/AGENTS.md`, outside the client repository) is **built by code, not
written by a model**: the verified test command, the pass count, known-red and flaky tests, generated and vendored
files and lockfiles not to edit, conventions from configuration (`.editorconfig`, `Directory.Build.props`,
linters, commit style), and the task's top locations. An ETH study (arXiv 2602.11988, ICLR 2026) found long
generated context files lowered success and raised cost by over 20%; the rules file stays at a few hundred tokens,
and the long dossier is a document the agent reads on demand, not auto-loaded context.

### 2. Clarify (built)
A separate under-specification detector, not a "please ask questions" prompt: in
[Ask or Assume?](https://www.alphaxiv.org/abs/2603.26233) (EMNLP 2026 Findings) a separate detector raised the
resolve rate on under-specified SWE-bench tasks from 61.2% to 69.4%, while forcing the agent to ask hurt some
models; [Ambig-SWE](https://arxiv.org/abs/2502.13069) (ICLR 2026) found interaction helps a lot on
under-specified tasks but most models can't tell those from well-specified ones. Model: Claude Opus 5.5.

Clarify runs after recon, so it reads the code before asking. Its tools are the dossier readers' read-only tools
and `submit_gaps`. Every gap gets one of three resolutions:
- `repository`: the code answers it (existing behaviour, conventions, tests, config), with files and lines as
  evidence, checked by code;
- `assume`: a sensible default, or a wrong guess is cheap to fix in review; the default is stated;
- `ask`: different answers lead to different deliverables and a wrong guess means a rework. At most 5, high or
  medium impact only, each with a default in the client's language so the client can answer "yes".

Code checks the rules: the question limit, defaults present, evidence that exists, a verdict that agrees with the
questions, and questions in the client's language (by script for Cyrillic languages and English). Outputs:
`questions.md` (a draft in the client's language for the human to edit and send; Foreman never sends it),
`clarify.md` (every gap, how it was closed, the evidence) and `questions.json`. The human records the replies with
`foreman answer <job> --set Q1="..."` or accepts the defaults with `--defaults`; the spec stage reads
`answers.json`.

Measured with `foreman eval clarify` ([results](../evals/clarify/results.md)): tasks written against real
repositories where the right behaviour is known, and the 10 real issues, which were fixed as written, so asking a
lot there is over-asking. 143/144 checks over 42 runs; on the real issues 27 of 30 runs asked nothing (the three
that asked were on MediatR, where the question is real); about $0.19 and under a minute per task.

During implementation the agent will ask through an `ask_client` MCP tool; the orchestrator answers from the spec
and defaults or pauses the job for the human (later).

### 3. Spec (later)
The comp-ledger PRP template, extended with "what the repository already has" from the dossier, and a
**done contract**: every item states how it will be verified.

### 4. Acceptance tests (later)
A separate session sees only the PRP and public interfaces and writes black-box tests outside the implementer's
reach. The tests are tested: they must fail on the base commit, and they must catch a few deliberately broken
versions of the code (mutants), so that they can't pass trivially. Their checksums are recorded.

### 5. Implement (later)
Claude Code headless (`claude -p --output-format stream-json`) in the sandbox, with explicit settings
(the repository's own `.claude/` is not loaded), `--permission-mode dontAsk` and an allowlist, `--max-budget-usd`,
`--max-turns`, and Foreman's MCP server (repo map, dossier, baseline, `ask_client`) through `--mcp-config`.
LSP plugins give it go-to-definition and references. It writes its own visible reproduction test first, and the
prompt tells it to stop and report if a test looks wrong (ImpossibleBench: this cut test hacking from 93% to 1%).

**Parallel candidates (later, behind a flag):** N = 3–5 independent attempts, each with its own writer and
sandbox. Claude 4's launch reported SWE-bench Verified 72.5% → 79.4% from parallel attempts filtered by regression
tests and picked by a scorer.

### 6. Verify (later)
Run by the orchestrator, never by the agent:
- **pass-to-pass:** every baseline test that passed (and isn't flaky) still passes;
- **fail-to-pass:** the hidden acceptance tests pass;
- the repository's own linter and formatter;
- **diff guard:** edits to existing tests, CI config, lockfiles or test config fail the gate unless the spec allows
  them; changes outside the located area need a reason in the PR;
- gitleaks on the diff, osv-scanner if dependencies changed;
- **reviewer** with fresh context, given only the spec and the diff, rule by rule (comp-ledger's reviewer). Its
  findings pass through a scope filter before going back, so review can't pull in scope creep.

At most 2 fix rounds; after that, restart in parallel or stop with an honest report.
UI tasks (later): Playwright checks with screenshots attached to the PR; SWE-Lancer grades freelance work end to end.

### 7. Select and deliver (later)
Selection order: fail-to-pass, then pass-to-pass, then reviewer verdict, then an LLM judge as tie-break.
Outputs: the PR description in the repository's style and a report for the client in their language (what was
done, how it was checked, what is open, cost, time). Nothing is pushed or sent automatically.

### 8. Metrics (now, grows with each stage)
Every stage appends events (time, tokens, cache hits, cost, outcome) to the job's audit log; every run appends one
line to `metrics/runs.jsonl`. Definitions are written before the first run, as in comp-ledger.

## 4. Cross-cutting capabilities

| Area | Decision | When |
|---|---|---|
| State | Own state machine. Each stage is idempotent and writes `jobs/<id>/<stage>/`; `state.json` records stage status; a stage reruns from its inputs. | now |
| Durable workflow engine | Microsoft Agent Framework workflows (GA in .NET since April 2026, checkpointing and human-in-the-loop) is the upgrade path. Temporal: strongest durability, but heavy for one developer and no official .NET AI integration. Dapr Workflow: no. | later / no |
| Sandbox | Docker, one named volume per job (the untrusted code never writes to the host), non-root user, dropped capabilities, `no-new-privileges`, CPU/memory/PID limits, wall-clock timeout. Network on only for setup, off for build and tests. | now |
| Egress allowlist | A proxy that lets only package registries and the model API through during setup and implementation. | later |
| Secrets | No keys, tokens or git credentials in the sandbox except the model credential the coding agent needs; the orchestrator applies the final patch. Traces record metadata, not content, by default. | now |
| Tracing | OpenTelemetry with the GenAI semantic conventions (still "Development" status, version pinned): one trace per job, a span per stage; Anthropic C# SDK spans (`com.anthropic.sdk.csharp*`); Claude Code stream-json events turned into child spans. Local backend: .NET Aspire dashboard. | now (spans), later (exporters) |
| Cost | Usage including cache reads and writes on every call; cost per completed task, not per request. | now |
| Prompts | Prompts are files in the repository; their content hash is recorded on spans and in eval results. | now |
| Models | Pinned ids. Defaults: Opus 5.5 for clarify, spec and review; Sonnet 5.5 for readers; Haiku 5.5 for intake and reports; Claude Code's default for implementation. Effort set per stage. "Stronger model at lower effort" is measured before any cascade. | now |
| Claude API features | Structured outputs, prompt caching (stable prefix: system prompt, tool list, dossier), adaptive thinking with explicit effort, refusal handling with server-side fallbacks, Batch API for eval sweeps. Context editing and compaction only if an API-side loop gets long. No sampling parameters exist any more, so reproducibility comes from repeated runs and confidence intervals. | now / later |
| Budgets and kill switch | Per stage and per job: dollars, turns, wall clock; a global cap; a flag that disables the coding stage. | now |
| MCP | Foreman's own MCP server (C# SDK 2.x, spec 2026-07-28) gives the coding agent recon tools and `ask_client`. | later |
| A2A | No: no agents from other vendors to talk to; worth a mention, not the complexity. | no |
| Claude Managed Agents | No for now: the point is to show our own sandbox and loop. Mention as the hosted alternative. | no |
| Evals | Per-stage offline harness with fixed inputs; code graders first (tests), model graders second, calibrated against human labels; 3–5 runs per case; pass@1, pass@k, pass^k with Wilson intervals; paired comparison between versions; a smoke set on every PR and the full set nightly. Ablations: reviewer on/off, N candidates, repo map on/off. | milestone 4 |
| Eval data | SWE-bench Verified is retired as a headline (OpenAI's audit, February 2026). Use fresh, decontaminated tasks (SWE-rebench), SWE-bench Pro public, and SWE-Lancer-style tasks graded end to end, in .NET, TypeScript and Python. | milestone 4 |
| Red team | Prompt-injection cases in the eval set: instructions planted in README, issue text and rules files. | milestone 4 |

## 5. Milestones

1. **Recon**: triage, environment, baseline, code map, dossier, security scans, on five real repositories.
2. **Intake, clarify, spec**: from task text to a Ready PRP with a done contract.
3. **Implement and verify**: one candidate, the full gate, the reviewer, the fix loop.
4. **Acceptance tests, parallel candidates, evals**: hidden tests with mutants, N candidates with selection, the
   eval harness with ablations, README with the numbers.
5. **Deliver and Tower**: PR and client report, approvals from the phone, one real task end to end.

## 6. Defaults chosen without the owner (change any of them)

- Name: Foreman (working name).
- Orchestrator: own state machine in .NET 10; Microsoft Agent Framework later if needed.
- Sandbox: Docker on the owner's PC (Docker Desktop on WSL 2); a VM later.
- First stacks: .NET, TypeScript/Node, Python.
- Repository private until the first milestone is done and scanned with gitleaks.
- Code and docs in English (public portfolio); progress notes for the owner in Russian on the Tower map.
