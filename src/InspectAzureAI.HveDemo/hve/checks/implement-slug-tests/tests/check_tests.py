"""Check that tests/test_slug.py follows python-tests.instructions.md and covers slugify.

Requires: at least 5 test functions/methods whose names start with test_, Arrange/Act/Assert
comments present, no use of unittest.mock imported directly (mocker or monkeypatch preferred,
mocking is not needed here), and the suite passes under unittest discovery.
"""

import re
import subprocess
import sys
from pathlib import Path


def main() -> int:
    path = Path("tests/test_slug.py")
    if not path.exists():
        print("FAIL: tests/test_slug.py missing")
        return 1
    src = path.read_text(encoding="utf-8")
    problems = []
    tests = re.findall(r"^\s*def (test_[a-z0-9_]+)\(", src, re.M)
    if len(tests) < 5:
        problems.append(f"expected at least 5 test functions, found {len(tests)}")
    for marker in ("Arrange", "Act", "Assert"):
        if f"# {marker}" not in src:
            problems.append(f"missing '# {marker}' section comment")
    if "from unittest.mock import" in src or "import unittest.mock" in src:
        problems.append("direct unittest.mock import is discouraged by python-tests.instructions.md")
    for needle in ("max_length", "TypeError"):
        if needle not in src:
            problems.append(f"tests do not cover {needle}")
    run = subprocess.run([sys.executable, "-m", "unittest", "discover", "-s", "tests", "-p", "test_slug.py"], capture_output=True, text=True)
    if run.returncode != 0:
        problems.append("test suite fails:\n" + run.stderr[-1500:])
    for p in problems:
        print("FAIL:", p)
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
