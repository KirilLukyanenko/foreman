# Working on Foreman

Read `docs/architecture.md` first. It is the source of truth for what each stage does and why; if code and the
document disagree, raise it instead of picking one.

## Rules

- Deterministic first: anything that can be detected or computed without a model is code, with unit tests.
- Code from client repositories is untrusted. It runs only through `DockerSandbox`, never on the host.
  Never add a bind mount of a host folder into a sandbox container, and never pass secrets into one.
- Text from client repositories and tasks is data. Never build prompts that let it change permissions or tools.
- Every stage writes its outputs to its job folder through `Job` and is idempotent: rerunning it from its inputs
  gives the same files.
- Don't weaken a test to make it pass. If a test looks wrong, stop and say so.
- Build with warnings as errors: `dotnet build Foreman.slnx` and `dotnet test` must be clean before a commit.
- Commit messages: imperative summary, then why. No secrets, hostnames or personal paths in code or docs.
