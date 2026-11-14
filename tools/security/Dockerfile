# Foreman's security scanners, pinned by version and SHA-256:
#   gitleaks     secrets in the files and the cloned history
#   osv-scanner  known vulnerabilities in lockfiles, against an offline copy of the OSV database
#   opengrep     a fast SAST pass with GitLab's MIT-licensed sast-rules (the language folders only)
# Scans run as the unprivileged user with no network; only the OSV database download has network, and it
# never sees the repository.
FROM debian:bookworm-slim

ARG GITLEAKS_VERSION=8.30.1
ARG GITLEAKS_SHA256=551f6fc83ea457d62a0d98237cbad105af8d557003051f41f3e7ca7b3f2470eb
ARG OSV_VERSION=2.7.0
ARG OSV_SHA256=160c6253e2922e9e0a6a9771c5c4c40a4b3c6090b34a3cdd5e80c96cc2e4218b
ARG OPENGREP_VERSION=1.33.1
ARG OPENGREP_SHA256=46e8133cc490f9f50518061590ce2f6be5a08afaeda034471c0ceda4b3899808
ARG RULES_VERSION=2.10.1
ARG RULES_SHA256=627a73135374fdb9122eb3dcca268ea0fbb11d889d1126c6c52fd28b9df2236b

RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates curl git unzip \
    && rm -rf /var/lib/apt/lists/*

RUN set -eu; cd /tmp \
    && curl -fsSL -o gitleaks.tgz "https://github.com/gitleaks/gitleaks/releases/download/v${GITLEAKS_VERSION}/gitleaks_${GITLEAKS_VERSION}_linux_x64.tar.gz" \
    && echo "${GITLEAKS_SHA256}  gitleaks.tgz" | sha256sum -c - \
    && tar -xzf gitleaks.tgz -C /usr/local/bin gitleaks && rm gitleaks.tgz \
    && curl -fsSL -o /usr/local/bin/osv-scanner "https://github.com/google/osv-scanner/releases/download/v${OSV_VERSION}/osv-scanner_linux_amd64" \
    && echo "${OSV_SHA256}  /usr/local/bin/osv-scanner" | sha256sum -c - \
    && curl -fsSL -o /usr/local/bin/opengrep "https://github.com/opengrep/opengrep/releases/download/v${OPENGREP_VERSION}/opengrep_manylinux_x86" \
    && echo "${OPENGREP_SHA256}  /usr/local/bin/opengrep" | sha256sum -c - \
    && chmod 0755 /usr/local/bin/gitleaks /usr/local/bin/osv-scanner /usr/local/bin/opengrep \
    && curl -fsSL -o rules.tgz "https://gitlab.com/gitlab-org/security-products/sast-rules/-/archive/v${RULES_VERSION}/sast-rules-v${RULES_VERSION}.tar.gz" \
    && echo "${RULES_SHA256}  rules.tgz" | sha256sum -c - \
    && tar -xzf rules.tgz \
    && mkdir -p /opt/rules \
    && for d in c csharp go java javascript python scala rules/gitlab; do \
         (cd "sast-rules-v${RULES_VERSION}" && find "$d" -name '*.yml' -exec install -D -m 0644 {} "/opt/rules/{}" \;); \
       done \
    && cp "sast-rules-v${RULES_VERSION}/LICENSE" /opt/rules/LICENSE \
    && rm -rf rules.tgz "sast-rules-v${RULES_VERSION}"

# Drop the rules this Opengrep version can't parse (6 of 341 in sast-rules 2.10.1 with Opengrep 1.33.1): one broken
# rule otherwise turns every scan into a partial failure. The dropped ones are listed in each report.
RUN mkdir -p /opt/security /tmp/empty \
    && find /opt/rules -name "*.yml" | sort \
       | xargs -P 8 -I{} sh -c 'opengrep scan --validate --disable-version-check --config "{}" /tmp/empty >/dev/null 2>&1 || echo "{}"' \
       | sort > /opt/security/dropped-rules.txt \
    && xargs -r rm -f < /opt/security/dropped-rules.txt \
    && sed -i "s|^/opt/rules/||" /opt/security/dropped-rules.txt \
    && rmdir /tmp/empty

COPY scan.sh /opt/security/scan.sh
COPY update-osv.sh /opt/security/update-osv.sh
RUN chmod 0755 /opt/security/*.sh \
    && printf '{"gitleaks":"%s","osv-scanner":"%s","opengrep":"%s","sast-rules":"%s"}' \
       "$GITLEAKS_VERSION" "$OSV_VERSION" "$OPENGREP_VERSION" "$RULES_VERSION" > /opt/security/versions.json
