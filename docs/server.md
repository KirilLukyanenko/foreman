# foreman serve

Foreman as a service: Tower (or anything with the token) starts a job from a task text and a repository, the job
runs on this machine until it needs a human, and goes on when Tower sends that. The laptop doesn't have to stay on.

```bash
export FOREMAN_TOKEN=<a random secret, 24+ characters>
foreman serve [--urls http://127.0.0.1:8095] [--workers 1] [--allow-local-repos] [--home dir]
```

It needs what the CLI needs (the .NET 10 runtime, Docker, git) and a model: Claude Code signed in, or
`CLAUDE_CODE_OAUTH_TOKEN` from `claude setup-token` on a server, or `ANTHROPIC_API_KEY` for the API driver.

## What a job goes through

`queued` → `running` (intake, go, recon, clarify, spec) → **`waiting_answers`** if clarify asked the client something →
**`waiting_ready`** until the human approves the PRP → `running` (acceptance tests, implement, verify, review) →
`verified` (a patch and a PR text) or `not_verified` / `gave_up` / `failed` with the reason. **`waiting_go`** if intake
said no-go. Each wait is a stage state (`Waiting`); answering, approving or overriding queues the job again, and the
pipeline skips everything already done. A job interrupted mid-stage (the service stopped) is queued again on start.

## API

Every `/api` call needs `Authorization: Bearer $FOREMAN_TOKEN`. `/health` doesn't.

| Call | Does |
|---|---|
| `POST /api/jobs` `{repo, task, commit?, ref?, go?, acceptance?}` | Creates a job and queues it. `repo` must be an https git URL without credentials (a local folder only with `--allow-local-repos`). |
| `GET /api/jobs` | The service's jobs, newest first, each with its phase. Jobs made by the CLI or the evals aren't listed. |
| `GET /api/jobs/{id}` | The phase, the stage, the reason; the client's questions while waiting for answers; the PRP while waiting for approval; the PR text; the files there are; the log's last lines. |
| `POST /api/jobs/{id}/answers` `{answers: {Q1: "..."}, defaults?}` | Records the client's answers (or accepts the defaults) and queues the job. |
| `POST /api/jobs/{id}/ready` | Marks the PRP Ready (its hash is recorded) and queues the job. |
| `POST /api/jobs/{id}/go` | Overrides intake's no-go and queues the job. |
| `POST /api/jobs/{id}/run` | Queues the job again (after a failure). |
| `POST /api/jobs/{id}/cancel` | Stops a running job (its stage fails as cancelled) or drops a queued one. |
| `GET /api/jobs/{id}/files/{name}` | One of `brief.md`, `questions.md`, `dossier.md`, `prp.md`, `pr.md`, `patch.diff`, `implement.json`, `verify.json`, `acceptance.json`, `serve.log`. Nothing else in the job folder is served. |

Nothing is pushed to the client's repository or sent to anyone: the human takes the patch and the PR text.

## On a server

A Linux VM of its own, not one that holds other secrets (Foreman runs client code, sandboxed): Ubuntu 24.04, 4–8 vCPU,
16 GB RAM, 150–200 GB disk. Docker, the .NET 10 runtime, git, gh (read-only), Claude Code. Foreman runs as its own
user under systemd (`deploy/foreman.service`), listens on localhost or the Tailscale address only, and is reached
through Tailscale; no port is open to the internet except SSH with a key. The token, the model credential and any
GitHub token go in `/etc/foreman/foreman.env` (mode 0600), never in the repository.

Not yet done: running it on Linux (it has run on Windows only), cleaning up old jobs' images and volumes, Tower's side.
