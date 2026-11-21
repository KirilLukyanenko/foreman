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
| `GET /api/jobs/{id}/files/{name}` | One of `brief.md`, `questions.md`, `dossier.md`, `prp.md`, `pr.md`, `report.md`, `patch.diff`, `implement.json`, `verify.json`, `acceptance.json`, `serve.log`. Nothing else in the job folder is served. |

Nothing is pushed to the client's repository or sent to anyone: the human takes the patch and the PR text.

## On a server

A Linux VM of its own, not one that holds other secrets (Foreman runs client code, sandboxed, and the `foreman` user
is in the docker group, which is root-equivalent): Ubuntu 24.04, 4–8 vCPU, 16 GB RAM, 150–200 GB disk.

1. On the laptop, `.\deploy\publish.ps1` builds `artifacts/foreman-linux-x64.tar.gz` (about 55 MB): Foreman
   self-contained for linux-x64 (no .NET on the server), `install.sh` and the systemd unit.
2. On the server, `tar -xzf foreman-linux-x64.tar.gz && sudo bash foreman-linux-x64/install.sh`. It installs Docker
   Engine from Docker's repository, creates the `foreman` user (home `/var/lib/foreman`, jobs in
   `/var/lib/foreman/home`), puts Foreman in `/opt/foreman`, installs Claude Code for that user, writes
   `/etc/foreman/foreman.env` (mode 600) with a new random `FOREMAN_TOKEN`, and enables the unit. It opens no port
   and writes no model credential. Running it again upgrades Foreman and keeps the jobs, the settings and the sign-in.
3. By hand: the model credential (`claude setup-token` on your own computer, the token into
   `CLAUDE_CODE_OAUTH_TOKEN`; or `ANTHROPIC_API_KEY`), Tailscale (`FOREMAN_URLS` to the machine's Tailscale address
   if Tower runs elsewhere, never a public address), then `sudo systemctl restart foreman`.

Tried on a clean Ubuntu 24.04 container with systemd stubbed: the install, the layout, Claude Code, the settings
file and the upgrade. The whole test suite passes on Linux, the Docker tests included (against a separate Docker
daemon).

### On demand

A machine that runs only for jobs costs a fraction of one left on (e2-standard-4: about $0.17 an hour, $126 a month
round the clock; the disk is billed either way). Install with `sudo env FOREMAN_IDLE_MINUTES=30 bash install.sh` and
the `foreman-idle` timer powers the machine off once nothing is queued or running and no person has called for that
long. It never does while someone is logged in or in the first minutes after boot; if the service doesn't answer at
all, it waits a whole idle period first. `/health` reports `busy` and `idleSeconds`; calls with an
`X-Foreman-Background` header (a dashboard's refresh) don't count as activity.

Whoever needs the machine starts it. A job waiting for the human waits on disk and goes on when the human answers
and the machine is started again. In Tower this is `ForemanMachine`: it starts the VM through the Compute API
(a service account allowed only to get and start that one instance), waits for `/health`, then passes the request
on; the job list is served from Tower's last copy while the machine sleeps, so it never wakes it. From stopped to
answering takes about a minute.

How it is laid out in Google Cloud: the VM has no service account (client code on it gets no Google credentials from
the metadata server), a reserved internal address that Tower calls, and a firewall rule denying it any connection
into the project's internal network, so client code can't reach the other machines; replies to Tower pass, the
firewall being stateful.

## Disk

Each job keeps a volume with its set-up repository and an image of its environment, often gigabytes.
`foreman serve` removes them once a day for jobs idle longer than `FOREMAN_GC_DAYS` (14; 0 turns it off), and
anything named after a job that no longer exists. The job folders stay, and a cleaned job's environment and baseline
are set up again if it runs. `foreman gc [--older-than-days 14] [--dry-run]` does the same by hand. Shared volumes
and images (the OSV database, the tool images) are never touched.

Tower runs on another VM in the same project and reaches this one over the internal network, no Tailscale needed.

A model token copied out of a terminal can carry its line wraps as spaces; `claude` then says "Not logged in". The
token has no whitespace: remove it before it goes into `foreman.env`.
