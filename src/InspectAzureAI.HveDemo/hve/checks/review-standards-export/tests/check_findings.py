"""Deterministic check for a code-review findings file written per the hve-core code-review skill.

usage: check_findings.py <findings.json> <expected-skill-or-null> <planted.json>
Exit 0 when the file parses, uses only the skill's severity/verdict vocabulary, carries the expected
verdict, and covers at least the minimum number of planted defects listed in planted.json.

A planted defect counts as covered only by a finding that (1) names the defect's file, (2) cites a line
range overlapping the defect's lines (plus a small slack; a defect without lines is anywhere in the file),
(3) quotes in current_code at least one line that is present in the review diff (the planted patch), and
(4) matches one of the defect's keyword regexes (word-bounded, case-insensitive) in its title, problem or
suggested fix. Each finding covers at most one defect and each defect is covered by at most one finding,
so one vague catch-all finding cannot pass the review.
"""

import json
import re
import sys
from pathlib import Path

SEVERITIES = {"Critical", "High", "Medium", "Low"}
VERDICTS = {"approve", "approve_with_comments", "request_changes"}
LINE_SLACK = 3


def parse_lines(value):
    """'27-29', '27', 27 or [27, 29] -> (27, 29); None when unparseable."""
    if isinstance(value, int):
        return value, value
    if isinstance(value, (list, tuple)) and len(value) == 2:
        try:
            return int(value[0]), int(value[1])
        except (TypeError, ValueError):
            return None
    match = re.match(r"^\s*L?(\d+)\s*(?:[-–:]\s*L?(\d+))?\s*$", str(value or ""))
    if not match:
        return None
    start = int(match.group(1))
    end = int(match.group(2)) if match.group(2) else start
    return (start, end) if start <= end else (end, start)


def diff_lines(patch_text):
    """The stripped content of every added and context line of a unified diff (removed lines are not current code)."""
    lines = set()
    for raw in patch_text.splitlines():
        if raw.startswith(("+++", "---", "@@", "diff ", "index ")):
            continue
        if raw.startswith("+") or raw.startswith(" "):
            stripped = raw[1:].strip()
            if stripped:
                lines.add(stripped)
    return lines


def quotes_the_diff(current_code, diff):
    quoted = [line.strip() for line in str(current_code or "").splitlines() if line.strip()]
    return bool(quoted) and any(any(q in d for d in diff) for q in quoted)


def same_file(finding_file, defect_file):
    f = str(finding_file or "").replace("\\", "/").lstrip("./")
    return f == defect_file or f.endswith("/" + defect_file)


def matches(finding, defect, diff):
    if not same_file(finding.get("file"), defect["file"]):
        return False
    if defect.get("lines"):
        cited = parse_lines(finding.get("lines"))
        if cited is None:
            return False
        low, high = defect["lines"]
        if cited[1] < low - LINE_SLACK or cited[0] > high + LINE_SLACK:
            return False
    if not quotes_the_diff(finding.get("current_code"), diff):
        return False
    text = " ".join(str(finding.get(key) or "") for key in ("title", "problem", "suggested_fix", "category"))
    return any(re.search(pattern, text, re.IGNORECASE) for pattern in defect["patterns"])


def main() -> int:
    findings_path, expected_skill, planted_path = sys.argv[1], sys.argv[2], sys.argv[3]
    if not Path(findings_path).exists():
        print(f"FAIL: {findings_path} not written")
        return 1
    data = json.loads(Path(findings_path).read_text(encoding="utf-8"))
    findings = data.get("findings") or []
    problems = []
    if data.get("verdict") not in VERDICTS:
        problems.append(f"verdict {data.get('verdict')!r} not in {sorted(VERDICTS)}")
    for f in findings:
        if f.get("severity") not in SEVERITIES:
            problems.append(f"finding {f.get('number')} severity {f.get('severity')!r} invalid")
        skill = f.get("skill")
        if expected_skill == "null" and skill is not None:
            problems.append(f"finding {f.get('number')} skill must be null, got {skill!r}")
        if expected_skill != "null" and skill != expected_skill:
            problems.append(f"finding {f.get('number')} skill must be {expected_skill!r}, got {skill!r}")
        for key in ("file", "lines", "problem", "category", "title", "current_code"):
            if not f.get(key):
                problems.append(f"finding {f.get('number')} missing {key}")
    planted = json.loads(Path(planted_path).read_text(encoding="utf-8"))
    diff = diff_lines(Path(planted["patch"]).read_text(encoding="utf-8")) if Path(planted["patch"]).exists() else set()
    used = set()
    hit = []
    for defect in planted["defects"]:
        for index, finding in enumerate(findings):
            if index in used:
                continue
            if matches(finding, defect, diff):
                used.add(index)
                hit.append(defect["id"])
                break
    print(f"planted defects matched: {hit} ({len(hit)}/{len(planted['defects'])}, minimum {planted['minimum']})")
    if len(hit) < planted["minimum"]:
        problems.append("too few planted defects covered")
    if data.get("verdict") != planted["expected_verdict"]:
        problems.append(f"expected verdict {planted['expected_verdict']}, got {data.get('verdict')}")
    for p in problems:
        print("FAIL:", p)
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
