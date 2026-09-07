#!/usr/bin/env python3
"""Embed a `capture` report into the dashboard template.

    dotnet run --project src/InspectAzureAI.Sample -- capture --include-failed --params all --out docs/dashboard/calls.json
    scripts/build-dashboard.py            # writes docs/dashboard/index.html

The result is one self-contained HTML file (no fetches), so it can be opened from disk or published as-is.
"""
import json
import pathlib
import sys

root = pathlib.Path(__file__).resolve().parent.parent / "docs" / "dashboard"
calls = pathlib.Path(sys.argv[1]) if len(sys.argv) > 1 else root / "calls.json"
out = pathlib.Path(sys.argv[2]) if len(sys.argv) > 2 else root / "index.html"

SECRET_HEADERS = ("authorization", "api-key", "x-api-key")


def guard(node, path):
    """Refuse to embed any object, anywhere in the report, whose `headers` dict carries an unredacted credential.

    Walks the whole document rather than the known check exchanges, so probe exchanges
    (deployments[].params[].exchanges[]) and any future shape are covered too.
    """
    if isinstance(node, dict):
        headers = node.get("headers")
        if isinstance(headers, dict):
            for name, value in headers.items():
                if name.lower() in SECRET_HEADERS and "redacted" not in str(value):
                    where = "/".join(str(p) for p in path + ["headers", name])
                    sys.exit(f"refusing to embed an unredacted {name} header from {calls} (at {where})")
        for key, value in node.items():
            guard(value, path + [key])
    elif isinstance(node, list):
        for index, value in enumerate(node):
            guard(value, path + [index])


data = json.loads(calls.read_text())
guard(data, [])

payload = json.dumps(data, ensure_ascii=False, separators=(",", ":")).replace("</", "<\\/")
template = (root / "dashboard.template.html").read_text()
assert "__CALLS_JSON__" in template
out.write_text(template.replace("__CALLS_JSON__", payload))
print(f"wrote {out} ({out.stat().st_size:,} bytes) from {calls}")
