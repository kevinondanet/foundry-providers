"""Rotate log files: keep the newest N *.log files in a directory and delete the rest.

Usage: python3 tools/rotate.py <directory> [--keep N] [--dry-run]
Exit codes: 0 success, 1 failure, 2 bad arguments.
"""

import argparse
import logging
import sys
from pathlib import Path

EXIT_SUCCESS = 0
EXIT_FAILURE = 1
EXIT_ERROR = 2
LOGGER = logging.getLogger(__name__)


def rotate(directory: Path, *, keep: int, dry_run: bool = False) -> list[Path]:
    """Delete all but the ``keep`` newest ``*.log`` files and return the deleted paths."""
    if not directory.is_dir():
        raise FileNotFoundError(f"{directory} is not a directory")
    logs = sorted(directory.glob("*.log"), key=lambda p: p.stat().st_mtime, reverse=True)
    doomed = logs[keep:]
    for path in doomed:
        LOGGER.info("deleting %s", path)
        if not dry_run:
            path.unlink()
    return doomed


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    parser.add_argument("--keep", type=int, default=5)
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args(argv)
    if args.keep < 0:
        parser.error("--keep must be >= 0")
    logging.basicConfig(level=logging.INFO, format="%(message)s")
    try:
        rotate(args.directory, keep=args.keep, dry_run=args.dry_run)
    except FileNotFoundError as exc:
        LOGGER.error("%s", exc)
        return EXIT_FAILURE
    return EXIT_SUCCESS


if __name__ == "__main__":
    sys.exit(main())
