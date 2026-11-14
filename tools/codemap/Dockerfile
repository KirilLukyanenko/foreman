# Foreman's code map extractor: tree-sitter definitions and references for every language except C#
# (C# goes through Roslyn on the host, which parses without building or running anything).
# The container gets a copy of the selected source files, no network and no privileges.
FROM python:3.12-slim-bookworm
ENV PIP_DISABLE_PIP_VERSION_CHECK=1 PYTHONDONTWRITEBYTECODE=1
RUN pip install --no-cache-dir tree-sitter==0.25.2 tree-sitter-language-pack==1.22.1
COPY extract.py /opt/codemap/extract.py
# Grammars are downloaded at build time: extraction runs with --network none.
RUN python /opt/codemap/extract.py --download && chmod -R a+rX /opt/tslp /opt/codemap
ENTRYPOINT []
