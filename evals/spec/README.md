# Spec eval

Does the spec stage write a PRP an implementer and a test writer can hold to? The 14 tasks of the
[clarify eval](../clarify/README.md): clarify runs again, its defaults are accepted (no client here), then the spec
stage writes the PRP (Claude Opus 5.5).

Code already enforces the done contract before a PRP is accepted (every requirement has a check, coverage of the
brief and of clarify's answers, examples, files and symbols against the code), so this eval reports what code
can't:

- how many requirements are checked by something executable (a test or a command) rather than by review;
- on the 10 real issues, whether the PRP's touch points include the files the real fix changed. A PRP can be right
  with another file (a fix one level up, or the alternative clarify's default chose), so this is a check, not the
  only answer, and every miss is read by hand.

```bash
dotnet run --project src/Foreman.Cli -- eval spec --runs 1
```

Results: [results.md](results.md). The PRPs stay in the job folders (`spec/prp.md`).
