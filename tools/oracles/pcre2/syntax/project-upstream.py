"""Argument-free projection of independent retained native compile observations."""
import gzip
import hashlib
import json
from pathlib import Path
import tarfile

root = Path(__file__).resolve().parents[4]
archive = root / "test/corpora/pcre2/oracle/native-evidence.tar.gz"
output = root / "test/corpora/pcre2/syntax/upstream-compile-inputs.jsonl.gz"
rows = []
with tarfile.open(archive) as source:
    for member in source:
        if (not member.name.startswith("indexed/") or
                not member.name.endswith(".jsonl") or ".pre-" in member.name):
            continue
        current = None
        for line_number, raw in enumerate(source.extractfile(member), 1):
            value = json.loads(raw)
            if value.get("kind") == "compileInput":
                current = value
            elif value.get("kind") == "compileResult" and current:
                rows.append({"member": member.name, "line": line_number,
                             "input": current, "result": value})
                current = None
with gzip.GzipFile(filename=str(output), mode="wb", mtime=0) as destination:
    for row in rows:
        destination.write((json.dumps(row, separators=(",", ":")) + "\n").encode())
print(json.dumps({"sourceSha256": hashlib.sha256(archive.read_bytes()).hexdigest(),
                  "projectionSha256": hashlib.sha256(output.read_bytes()).hexdigest(),
                  "records": len(rows), "nativeAccepted": sum(r["result"]["accepted"] for r in rows),
                  "bytes": output.stat().st_size}, sort_keys=True))
