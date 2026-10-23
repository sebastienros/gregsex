#!/usr/bin/env python3
"""Builds the inputs of the Oniguruma corpus (test/corpora/oniguruma) from tm-grammars and a TextMateSharp trace.

Usage: python3 -I build_corpus.py <textmate-dir> <fences.json> <raw-trace.json> <corpus-dir>

  textmate-dir   Douze's Douze/Highlighting/textmate.zip extracted (tm-grammars 1.32.22: manifest.json,
                 grammars/, tm-grammars/LICENSE and NOTICE)
  fences.json    { language: [fence, ...] } from Douze's docs-heavy generate.mjs output
  raw-trace.json TextMateSharp.Benchmarks --trace output: { language: { scanners: [[pattern]], lines, calls } }

Writes grammar-cases.json.gz, trace.json.gz and LICENSE-tm-grammars.txt. Patterns are stored as TextMate hosts send
them to Oniguruma: Onigwrap's UnicodeCharEscape rewrite (\\xHH... to \\x{HH...}) applied. Expected outcomes come from
oracle/ (see README.md).
"""
import gzip
import json
import os
import re
import sys

LANGUAGES = {"bash": "shellscript", "ts": "typescript", "js": "javascript", "html": "html", "json": "json",
             "yaml": "yaml", "csharp": "csharp"}
LINES_PER_LANGUAGE = 6


def rewrite(pattern):
    pattern = re.sub(r"\\x[A-Fa-f0-9]{2,8}", lambda m: "\\x{" + m.group(0)[2:] + "}", pattern)
    return pattern.replace("\\x{7fffffff}", "\\x{7ffff}")


def walk(node, out):
    if isinstance(node, dict):
        for key, value in node.items():
            if key in ("match", "begin", "end", "while") and isinstance(value, str):
                out.append(value)
            else:
                walk(value, out)
    elif isinstance(node, list):
        for value in node:
            walk(value, out)


def dump(path, value):
    with gzip.open(path, "wt", encoding="utf-8", compresslevel=9) as f:
        json.dump(value, f, ensure_ascii=False, separators=(",", ":"))
    # A gzip header holds a timestamp; zero it so the bytes are reproducible.
    with open(path, "r+b") as f:
        f.seek(4)
        f.write(b"\0\0\0\0")


def main():
    textmate, fences_path, trace_path, out = sys.argv[1:5]
    manifest = json.load(open(os.path.join(textmate, "manifest.json"), encoding="utf-8"))
    entries = {g["name"]: g for lst in ("grammars", "injections") for g in manifest["tm-grammars"]["lists"][lst]}
    fences = json.load(open(fences_path, encoding="utf-8"))

    cases, used = [], set()
    for language, name in LANGUAGES.items():
        closure, pending = [], [name]
        while pending:
            grammar = pending.pop()
            if grammar in closure or grammar not in entries:
                continue
            closure.append(grammar)
            pending.extend(entries[grammar].get("embedded", []))
        used.update(closure)
        lines = []
        for fence in fences[language]:
            for line in fence.split("\n"):
                if line not in lines:
                    lines.append(line)
        subjects = [line + "\n" for line in lines[:LINES_PER_LANGUAGE]]
        patterns = []
        for grammar in closure:
            out_patterns = []
            walk(json.load(open(os.path.join(textmate, "grammars", grammar + ".json"), encoding="utf-8")), out_patterns)
            for p in out_patterns:
                p = rewrite(p)
                if p not in patterns:
                    patterns.append(p)
        for p in patterns:
            cases.append({"pattern": p, "subjects": subjects, "starts": [0, 7, 21]})
    dump(os.path.join(out, "grammar-cases.json.gz"), cases)

    raw = json.load(open(trace_path, encoding="utf-8"))
    trace = {}
    for language, node in raw.items():
        patterns, index = [], {}
        scanners = []
        for scanner in node["scanners"]:
            ids = []
            for p in scanner:
                p = rewrite(p)
                if p not in index:
                    index[p] = len(patterns)
                    patterns.append(p)
                ids.append(index[p])
            scanners.append(ids)
        trace[language] = {"patterns": patterns, "scanners": scanners, "lines": node["lines"],
                           "calls": [v for call in node["calls"] for v in call]}
    dump(os.path.join(out, "trace.json.gz"), trace)

    # tm-grammars' license, then the NOTICE sections of the grammars the corpus draws from.
    notice = open(os.path.join(textmate, "tm-grammars", "NOTICE"), encoding="utf-8").read()
    sections = re.split(r"\n(?==+\nFiles:)", notice)
    kept = [s for s in sections if re.search(r"Files:.*", s)
            and any(re.search(rf"(?:^|[\s,]){re.escape(g)}\.json(?:$|[\s,])", re.search(r"Files:(.*)", s).group(1)) for g in used)]
    with open(os.path.join(out, "LICENSE-tm-grammars.txt"), "w", encoding="utf-8", newline="\n") as f:
        f.write(open(os.path.join(textmate, "tm-grammars", "LICENSE"), encoding="utf-8").read())
        f.write("\n\nNOTICE sections of the grammars these fixtures draw patterns from (" + ", ".join(sorted(used)) + "):\n\n")
        f.write("\n".join(kept))
    print(f"{len(cases)} grammar cases from {sorted(used)}; trace {sum(len(v['calls']) // 3 for v in trace.values())} calls")


if __name__ == "__main__":
    main()
