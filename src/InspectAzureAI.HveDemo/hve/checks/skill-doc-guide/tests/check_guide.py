"""Check docs/guides/rotate-logs.md against the hve-core documentation skill's guide template
and the markdown instructions (single H1, ATX headings, no level skips, fenced code with a
language, no trailing whitespace, no tabs)."""

import re
import sys
from pathlib import Path

REQUIRED_H2 = ["Purpose", "When to use this guide", "Prerequisites", "Steps", "Expected outcome", "Troubleshooting"]


def main() -> int:
    path = Path(sys.argv[1] if len(sys.argv) > 1 else "docs/guides/rotate-logs.md")
    if not path.exists():
        print(f"FAIL: {path} missing")
        return 1
    text = path.read_text(encoding="utf-8")
    lines = text.splitlines()
    problems = []
    # strip YAML frontmatter if present
    body = lines
    if lines and lines[0].strip() == "---":
        try:
            end = lines.index("---", 1)
            body = lines[end + 1:]
        except ValueError:
            problems.append("unterminated frontmatter")
    headings = [(len(m.group(1)), m.group(2).strip()) for l in body if (m := re.match(r"^(#{1,6})\s+(.*)$", l))]
    h1 = [h for h in headings if h[0] == 1]
    if len(h1) != 1:
        problems.append(f"expected exactly one H1, found {len(h1)}")
    h2 = [h[1] for h in headings if h[0] == 2]
    missing = [h for h in REQUIRED_H2 if h not in h2]
    if missing:
        problems.append(f"missing guide-template sections: {missing}")
    order = [h for h in h2 if h in REQUIRED_H2]
    if order != [h for h in REQUIRED_H2 if h in order]:
        problems.append(f"guide sections out of template order: {order}")
    prev = 0
    for level, title in headings:
        if prev and level > prev + 1:
            problems.append(f"heading level skips from {prev} to {level} at {title!r}")
        prev = level
    in_fence = False
    for n, l in enumerate(body, 1):
        if l.startswith("```"):
            if not in_fence and l.strip() == "```":
                problems.append(f"line {n}: fenced code block without a language")
            in_fence = not in_fence
        if l.rstrip() != l and not l.endswith("  "):
            problems.append(f"line {n}: trailing whitespace")
        if "\t" in l:
            problems.append(f"line {n}: tab character")
    if "rotate.py" not in text or "--keep" not in text:
        problems.append("guide must document tools/rotate.py and its --keep option")
    for p in problems:
        print("FAIL:", p)
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
