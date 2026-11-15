# Clarify eval

Does clarify ask the right questions, and only those? 14 tasks, each run 3 times (intake on Claude Haiku 5.5, then
clarify on Claude Opus 5.5 with the repository's code map; no dossier).

- `tasks/` holds four tasks **written for this eval** against real repositories, where the right behaviour is
  known: a guard the library already has (GuardClauses' `StringTooLong`: the code answers it, and the question is
  whether it covers the need), a rate-limiting middleware for Hono (real open choices: storage across runtimes,
  docs living in another repository), Russian file sizes for humanize (a Russian client; 1000 or 1024 bytes,
  locale or option), and a "stronger algorithm" for itsdangerous (the digest is already configurable; the real
  question is the default).
- The other ten are the real issues of the [localization eval](../localization/README.md). They were fixed as
  written, so asking much there is over-asking: the failure Ambig-SWE (ICLR 2026) found most models make, telling
  clear tasks from unclear ones.

```bash
dotnet run --project src/Foreman.Cli -- eval clarify --runs 3
```

Results: [results.md](results.md), with every question asked. The checks are limits (at least or at most so many
questions, at least one gap closed by the code, the verdict, a clean acceptance); whether a question is a good one
is in the list for a human to judge.
