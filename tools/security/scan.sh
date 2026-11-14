#!/bin/sh
# Runs in the sandbox as the unprivileged user with no network: the repository copy is in /work/repo, the OSV
# database (read-only) in /osv, results go to $OUT. Every tool writes JSON; exit codes are recorded, not trusted.
set -u
cd /work/repo

# Files a repository could use to hide its own findings from the scanners. This copy is Foreman's, so they are
# removed (and reported): the scan sees what an attacker would.
: > "$OUT/removed-ignore-files.txt"
for f in .gitleaks.toml .gitleaksignore .semgrepignore .opengrepignore; do
  if [ -e "$f" ]; then echo "$f" >> "$OUT/removed-ignore-files.txt"; rm -rf "$f"; fi
done
git rev-list --count HEAD > "$OUT/history-commits.txt" 2>/dev/null || echo 0 > "$OUT/history-commits.txt"

run() { # name, command...: exit code and seconds go to $OUT/<name>.run
  name=$1; shift
  start=$(date +%s.%N)
  "$@" > "$OUT/$name.log" 2>&1
  code=$?
  end=$(date +%s.%N)
  echo "$code $(echo "$end - $start" | awk '{print $1 - $3}')" > "$OUT/$name.run"
}

# Secret values never leave the scanner: --redact replaces them in the report.
run gitleaks-files gitleaks dir --redact --no-banner --exit-code 0 --report-format json --report-path "$OUT/gitleaks-files.json" --log-level warn .
run gitleaks-history gitleaks git --redact --no-banner --exit-code 0 --report-format json --report-path "$OUT/gitleaks-history.json" --log-level warn .

# --offline turns off every network feature (deps.dev resolution included); the vulnerability data is local.
OSV_SCANNER_LOCAL_DB_CACHE_DIRECTORY=/osv run osv osv-scanner scan source -r --offline --offline-vulnerabilities --format json --output "$OUT/osv.json" .

run opengrep opengrep scan --config /opt/rules --json --output "$OUT/opengrep.json" --disable-version-check \
  --timeout 10 --max-target-bytes 1000000 --jobs 4 --quiet .

cp /opt/security/versions.json "$OUT/versions.json"
cp /opt/security/dropped-rules.txt "$OUT/dropped-rules.txt"
exit 0
