"""Definitions and references per source file, from tree-sitter tags queries (the queries the grammars ship).

Reads /work/repo/files.json, a list of [path, language], and the files under /work/repo/src.
Writes one JSON line per file to the path given as the only argument:
  {"path", "lang", "lines", "defs": [[name, kind, line, end_line, signature]], "refs": {name: count}, "error"}

References are every identifier leaf except the names being defined, not only the query's reference captures:
the queries mostly capture calls, and imports, type annotations and plain uses are what connect files.
"""
import json
import sys

from tree_sitter import Query, QueryCursor
import tree_sitter_language_pack as pack
from tree_sitter_language_pack.options import PackConfig

CACHE = "/opt/tslp"
LANGUAGES = ["python", "javascript", "typescript", "tsx", "go", "rust", "java", "kotlin", "ruby", "php", "c", "cpp", "swift", "scala", "csharp"]
# TypeScript's tags query builds on JavaScript's, as in the upstream grammar ("; inherits: javascript").
INHERITS = {"typescript": ["javascript"], "tsx": ["javascript", "typescript"]}
IDENTIFIER_LEAVES = {
    "identifier", "type_identifier", "field_identifier", "property_identifier", "shorthand_property_identifier",
    "shorthand_property_identifier_pattern", "constant", "simple_identifier", "name", "package_identifier",
    "namespace_identifier", "statement_identifier",
}
MAX_SIGNATURE = 160

pack.configure(PackConfig(cache_dir=CACHE))


def query_for(lang, cache={}):
    if lang not in cache:
        language = pack.get_language(lang)
        sources = [pack.get_tags_query(l) or "" for l in INHERITS.get(lang, [])] + [pack.get_tags_query(lang) or ""]
        try:
            cache[lang] = Query(language, "\n".join(sources))
        except Exception:
            cache[lang] = Query(language, sources[-1]) if sources[-1] else None
    return cache[lang]


def signature_at(lines, row):
    """The declaration line, continued while its parentheses are open (a parameter list over several lines)."""
    parts, depth = [], 0
    for line in lines[row:row + 6]:
        text = line.decode("utf-8", "replace").strip()
        parts.append(text)
        depth += text.count("(") - text.count(")")
        if depth <= 0 or len(" ".join(parts)) > MAX_SIGNATURE:
            break
    first = lines[row].decode("utf-8", "replace") if row < len(lines) else ""
    indent = first[: len(first) - len(first.lstrip())]
    return indent + " ".join(p for p in parts if p).replace("( ", "(").replace(" )", ")").replace(",)", ")")


def extract(path, lang, data):
    tree = pack.get_parser(lang).parse(data)
    lines = data.split(b"\n")
    defs, def_nodes = [], set()
    query = query_for(lang)
    if query is not None:
        for _, caps in QueryCursor(query).matches(tree.root_node):
            names = caps.get("name") or []
            for key, nodes in caps.items():
                if not key.startswith("definition.") or not names:
                    continue
                name_node, node = names[0], nodes[0]
                signature = signature_at(lines, node.start_point[0])
                defs.append([name_node.text.decode("utf-8", "replace"), key.split(".", 1)[1],
                             name_node.start_point[0] + 1, node.end_point[0] + 1, signature[:MAX_SIGNATURE]])
                def_nodes.add((name_node.start_byte, name_node.end_byte))

    refs = {}
    cursor = tree.walk()
    visited_children = False
    while True:
        node = cursor.node
        if not visited_children:
            if node.child_count == 0 and node.type in IDENTIFIER_LEAVES and (node.start_byte, node.end_byte) not in def_nodes:
                name = node.text.decode("utf-8", "replace")
                if name:
                    refs[name] = refs.get(name, 0) + 1
            if cursor.goto_first_child():
                continue
        if cursor.goto_next_sibling():
            visited_children = False
            continue
        if not cursor.goto_parent():
            break
        visited_children = True

    defs.sort(key=lambda d: d[2])
    return {"path": path, "lang": lang, "lines": len(lines), "defs": defs, "refs": refs, "error": None}


def main():
    if sys.argv[1:] == ["--download"]:
        pack.download(LANGUAGES)
        for lang in LANGUAGES:
            query_for(lang)
        return
    out_path = sys.argv[1]
    with open("/work/repo/files.json", encoding="utf-8") as f:
        files = json.load(f)
    with open(out_path, "w", encoding="utf-8") as out:
        for path, lang in files:
            try:
                with open("/work/repo/src/" + path, "rb") as f:
                    record = extract(path, lang, f.read())
            except Exception as e:  # one bad file must not lose the map
                record = {"path": path, "lang": lang, "lines": 0, "defs": [], "refs": {}, "error": f"{type(e).__name__}: {e}"[:300]}
            out.write(json.dumps(record, ensure_ascii=False) + "\n")
    versions = {"tree-sitter": "0.25.2", "tree-sitter-language-pack": "1.22.1"}
    with open(out_path + ".versions.json", "w", encoding="utf-8") as f:
        json.dump(versions, f)


if __name__ == "__main__":
    main()
