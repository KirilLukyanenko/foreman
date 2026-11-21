#!/usr/bin/env bash
# Powers this machine off once foreman serve has had nothing to do for FOREMAN_IDLE_MINUTES (0 or unset: never).
# For a cloud VM that Tower starts on demand: Tower switches it on for a new job, an answer or a look at a result;
# with nothing queued or running and no call from a person for that long, it goes off again and costs only its disk.
# A job that waits for the human waits on disk, and runs on when the human answers and Tower starts the machine.
#
# Runs as root from foreman-idle.timer every few minutes. It never powers off while someone is logged in, in the
# first FOREMAN_IDLE_MINUTES after boot, or while the service is busy. If the service doesn't answer at all (an upgrade,
# a crash), it waits a whole idle period of silence before giving up on it.
set -uo pipefail

set -a; . /etc/foreman/foreman.env; set +a
minutes="${FOREMAN_IDLE_MINUTES:-0}"
case "$minutes" in ''|*[!0-9]*) exit 0 ;; esac
[ "$minutes" -gt 0 ] || exit 0
limit=$((minutes * 60))

up=$(cut -d. -f1 /proc/uptime)
[ "$up" -ge "$limit" ] || exit 0
[ -z "$(who)" ] || exit 0

url="${FOREMAN_URLS:-http://127.0.0.1:8095}"
url="${url%%;*}"
url="${url/0.0.0.0/127.0.0.1}"
down=/run/foreman-idle.down

if health=$(curl -fsS --max-time 10 "$url/health" 2>/dev/null); then
  rm -f "$down"
  case "$health" in *'"busy":false'*) ;; *) exit 0 ;; esac
  idle=$(printf '%s' "$health" | grep -o '"idleSeconds":[0-9]*' | cut -d: -f2)
  [ -n "$idle" ] && [ "$idle" -ge "$limit" ] || exit 0
  echo "foreman serve idle for ${idle}s (limit ${limit}s): powering off"
else
  now=$(date +%s)
  [ -f "$down" ] || { echo "$now" > "$down"; echo "foreman serve doesn't answer at $url; powering off if it stays silent for ${limit}s"; exit 0; }
  since=$(cat "$down")
  [ $((now - since)) -ge "$limit" ] || exit 0
  echo "foreman serve silent for $((now - since))s: powering off"
fi
systemctl poweroff
