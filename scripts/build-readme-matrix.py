#!/usr/bin/env python3
"""Regenerate the "Parameters by model" table in README.md from a `capture --params` report.

    scripts/build-readme-matrix.py [docs/dashboard/calls.json] [README.md]

Rows are the deployments that carry `params` probes; deployments with identical verdicts (and the same
reasoning visibility) are merged into one row, like the verified table does for gpt-5.6-*. The table is
written between `<!-- params-matrix:start -->` and `<!-- params-matrix:end -->`; when the markers are
absent a `## Parameters by model` section carrying them is inserted before `## Claude deployments`.
Running it twice on the same input is a no-op.
"""
import json
import pathlib
import sys

START, END = "<!-- params-matrix:start -->", "<!-- params-matrix:end -->"
SECTION_HEADING = "## Parameters by model"
ANCHOR_HEADING = "## Claude deployments"

root = pathlib.Path(__file__).resolve().parent.parent
calls = pathlib.Path(sys.argv[1]) if len(sys.argv) > 1 else root / "docs" / "dashboard" / "calls.json"
readme = pathlib.Path(sys.argv[2]) if len(sys.argv) > 2 else root / "README.md"


def has_params(deployment):
    return isinstance(deployment.get("params"), list) and len(deployment["params"]) > 0


def cell(probe):
    """One verdict cell; absent probe means the field was not probed for this deployment."""
    if probe is None:
        return "-"
    verdict = probe.get("verdict")
    if verdict == "accepted":
        return "ok" if probe.get("observable", True) is not False else "ok*"
    return {"ignored": "ign", "rejected": "rej", "error": "err", "n/a": "n/a"}.get(verdict, str(verdict or "?"))


def reasoning_of(deployment):
    """(visibility label, reasoning token count or None) from the last check named "reasoning"."""
    checks = [c for c in deployment.get("checks") or [] if c.get("check") == "reasoning"]
    if not checks:
        return None, None
    check = checks[-1]
    if not check.get("ok"):
        return "fail", None
    output = check.get("output") or {}
    usage = output.get("usage") or {}
    tokens = usage.get("reasoningTokens")
    return output.get("reasoningVisibility") or "n/a", (tokens if isinstance(tokens, int) and tokens > 0 else None)


def reasoning_cell(label, counts):
    if label is None:
        return "-"
    counts = sorted(c for c in counts if c is not None)
    if not counts:
        return label
    span = str(counts[0]) if counts[0] == counts[-1] else f"{counts[0]}–{counts[-1]}"
    return f"{label} · {span} tok"


def md(text):
    return str(text).replace("|", "\\|")


def build_table(data):
    deployments = [d for d in data.get("deployments", []) if has_params(d)]
    if not deployments:
        return None
    ids = []
    for d in deployments:
        for p in d["params"]:
            if p.get("id") not in ids:
                ids.append(p.get("id"))
    rows = []  # (key, names, reasoning label, token counts, cells)
    for d in deployments:
        by_id = {p.get("id"): p for p in d["params"]}  # last probe with an id wins, like the dashboard
        cells = [cell(by_id.get(i)) for i in ids]
        label, tokens = reasoning_of(d)
        key = (label, tuple(cells))
        for row in rows:
            if row[0] == key:
                row[1].append(d["name"])
                row[3].append(tokens)
                break
        else:
            rows.append((key, [d["name"]], label, [tokens], cells))
    lines = ["| Deployment | reasoning | " + " | ".join(f"`{md(i)}`" for i in ids) + " |",
             "|---|---|" + "---|" * len(ids)]
    for _, names, label, tokens, cells in rows:
        lines.append("| " + md(", ".join(names)) + " | " + md(reasoning_cell(label, tokens)) + " | " + " | ".join(md(c) for c in cells) + " |")
    resource = (data.get("resource") or {}).get("name") or "the resource"
    lines += ["", "`ok` accepted with a visible effect · `ok*` accepted, no visible effect · `ign` HTTP 200 but no effect · "
              "`rej` HTTP 400 · `err` other failure · `n/a` the provider derives nothing for this family · `-` not probed for this route.",
              "", f"Probed on {data.get('capturedAt', 'an unknown date')} against {resource}."]
    return "\n".join(lines), len(rows)


def splice(text, block):
    """Replace the marker block, or insert a fresh section before the anchor heading. Returns (text, what)."""
    body = f"{START}\n{block}\n{END}"
    if START in text and END in text:
        head, rest = text.split(START, 1)
        _, tail = rest.split(END, 1)
        return head + body + tail, "replaced"
    if START in text or END in text:
        sys.exit(f"{readme}: found one of the params-matrix markers without the other; fix the file by hand")
    section = f"{SECTION_HEADING}\n\n{body}\n\n"
    lines = text.splitlines(keepends=True)
    for i, line in enumerate(lines):
        if line.startswith(ANCHOR_HEADING):
            return "".join(lines[:i]) + section + "".join(lines[i:]), f"inserted before {ANCHOR_HEADING!r}"
    return text.rstrip("\n") + "\n\n" + section.rstrip("\n") + "\n", "appended (no anchor heading found)"


data = json.loads(calls.read_text())
built = build_table(data)
if built is None:
    sys.exit(f"{calls}: no deployment carries `params`; run `capture --include-failed --params all --out {calls}` first")
table, nrows = built
before = readme.read_text()
after, what = splice(before, table)
if after == before:
    print(f"{readme}: params matrix already up to date from {calls}")
else:
    readme.write_text(after)
    print(f"{readme}: {what} the params matrix ({nrows} rows) from {calls}")
