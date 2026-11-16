#!/usr/bin/env bash
# Sets up foreman serve on a fresh Ubuntu 24.04 VM, or upgrades it. Run as root from the unpacked bundle:
#
#   tar -xzf foreman-linux-x64.tar.gz && sudo bash foreman-linux-x64/install.sh
#
# What it does: Docker Engine from Docker's apt repository; a system user `foreman` with its home in
# /var/lib/foreman (jobs in /var/lib/foreman/home); Foreman in /opt/foreman; Claude Code for that user; a settings
# file /etc/foreman/foreman.env (mode 600) with a new random FOREMAN_TOKEN; the systemd unit, enabled.
# What it doesn't: open any port, install Tailscale, or put a model credential anywhere. It asks you to do those.
# Running it again upgrades Foreman and keeps the jobs, the settings and Claude Code's sign-in.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
[ "$(id -u)" = 0 ] || { echo "run as root: sudo bash $0" >&2; exit 1; }
# -f, not -x: a bundle packed on Windows has no executable bits; they are set below.
[ -f "$here/bin/foreman" ] || { echo "no bin/foreman next to this script: run it from the unpacked bundle" >&2; exit 1; }
. /etc/os-release
[ "${ID:-}" = ubuntu ] || echo "warning: written for Ubuntu 24.04, this is ${PRETTY_NAME:-unknown}" >&2

echo "== packages"
apt-get update -qq
apt-get install -y -qq ca-certificates curl git >/dev/null
if ! command -v docker >/dev/null; then
  install -m 0755 -d /etc/apt/keyrings
  curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
  chmod a+r /etc/apt/keyrings/docker.asc
  echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/ubuntu ${VERSION_CODENAME} stable" \
    > /etc/apt/sources.list.d/docker.list
  apt-get update -qq
  apt-get install -y -qq docker-ce docker-ce-cli containerd.io >/dev/null
fi
systemctl enable --now docker >/dev/null

echo "== the foreman user"
id foreman >/dev/null 2>&1 || useradd --system --home-dir /var/lib/foreman --create-home --shell /bin/bash foreman
# Membership in docker is root-equivalent on this machine: it is why Foreman gets a machine of its own.
usermod -aG docker foreman
install -d -o foreman -g foreman -m 0750 /var/lib/foreman /var/lib/foreman/home

echo "== Foreman in /opt/foreman"
was_running=false
systemctl is-active --quiet foreman 2>/dev/null && { was_running=true; systemctl stop foreman; }
rm -rf /opt/foreman.new
cp -a "$here/bin" /opt/foreman.new
chmod 0755 /opt/foreman.new/foreman; [ -f /opt/foreman.new/createdump ] && chmod 0755 /opt/foreman.new/createdump
rm -rf /opt/foreman.old
[ -d /opt/foreman ] && mv /opt/foreman /opt/foreman.old
mv /opt/foreman.new /opt/foreman

echo "== Claude Code for the foreman user"
if [ ! -x /var/lib/foreman/.local/bin/claude ]; then
  sudo -u foreman -H bash -c 'curl -fsSL https://claude.ai/install.sh | bash' >/dev/null
fi

echo "== settings in /etc/foreman/foreman.env"
install -d -m 0755 /etc/foreman
if [ ! -f /etc/foreman/foreman.env ]; then
  umask 077
  cat > /etc/foreman/foreman.env <<EOF
# foreman serve's settings. Mode 600, root only: systemd reads it and passes it to the service.
FOREMAN_HOME=/var/lib/foreman/home
# Tower sends this token; put the same value in Tower's ForemanServe:Token.
FOREMAN_TOKEN=$(openssl rand -hex 32)
# Where the service listens. Keep localhost if Tower runs on this machine; otherwise this machine's Tailscale
# address, e.g. http://100.101.102.103:8095. Never a public address.
FOREMAN_URLS=http://127.0.0.1:8095
# Remove idle jobs' Docker volumes and images after this many days (0: never).
FOREMAN_GC_DAYS=14
CLAUDE_BIN=/var/lib/foreman/.local/bin/claude
# The model credential, one of the two (fill it in yourself; this script never writes one):
#   a Claude subscription: run "claude setup-token" on your own computer and paste the token here;
#   or an API key from the Anthropic Console.
CLAUDE_CODE_OAUTH_TOKEN=
# ANTHROPIC_API_KEY=
EOF
  chmod 600 /etc/foreman/foreman.env
  echo "   new settings file with a random FOREMAN_TOKEN"
else
  echo "   kept the existing settings file"
fi

echo "== systemd"
cp "$here/foreman.service" /etc/systemd/system/foreman.service
systemctl daemon-reload
systemctl enable foreman >/dev/null

if grep -qE '^(CLAUDE_CODE_OAUTH_TOKEN|ANTHROPIC_API_KEY)=.+' /etc/foreman/foreman.env; then
  systemctl restart foreman
  sleep 3
  systemctl is-active --quiet foreman && echo "== foreman serve is running" || { echo "== foreman serve didn't start: journalctl -u foreman -n 50" >&2; exit 1; }
else
  $was_running && echo "   (it was running before; it starts again once a model credential is set)"
  cat <<'EOF'
== almost there. Three things only you can do:
  1. The model credential: on your computer run `claude setup-token`, then on this server
     sudo nano /etc/foreman/foreman.env    (CLAUDE_CODE_OAUTH_TOKEN=...)
  2. Private access: install Tailscale and join your tailnet
     curl -fsSL https://tailscale.com/install.sh | sh && sudo tailscale up
     then set FOREMAN_URLS to this machine's Tailscale address if Tower runs elsewhere.
  3. Start it: sudo systemctl restart foreman && journalctl -u foreman -f
EOF
fi
