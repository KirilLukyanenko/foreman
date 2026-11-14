#!/bin/sh
# Refreshes the local OSV database for the given ecosystems (NuGet npm PyPI ...) in /osv, the layout osv-scanner's
# offline mode reads. Runs with network but without the repository: only ecosystem names go out, never package lists.
set -eu
for eco in "$@"; do
  dir="/osv/osv-scalibr/$eco"
  mkdir -p "$dir"
  url="https://osv-vulnerabilities.storage.googleapis.com/$eco/all.zip"
  rm -f "$dir/all.zip.new"
  if [ -s "$dir/all.zip" ]; then
    # Only downloads when the published file is newer than ours.
    curl -fsSL --retry 3 -z "$dir/all.zip" -o "$dir/all.zip.new" "$url"
  else
    curl -fsSL --retry 3 -o "$dir/all.zip.new" "$url"
  fi
  if [ -s "$dir/all.zip.new" ]; then mv "$dir/all.zip.new" "$dir/all.zip"; date -u +%Y-%m-%dT%H:%M:%SZ > "$dir/downloaded"; fi
  rm -f "$dir/all.zip.new"
  date -u +%Y-%m-%dT%H:%M:%SZ > "$dir/checked"
done
chmod -R a+rX /osv
