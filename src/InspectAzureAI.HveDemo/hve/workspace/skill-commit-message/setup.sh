#!/usr/bin/env bash
#
# setup.sh
# Create the git history for the commit-message sample and stage the retry change.

set -euo pipefail

main() {
  cd "$(dirname "${BASH_SOURCE[0]}")"
  git init -q .
  git config user.email "demo@example.com"
  git config user.name "Demo"
  git add README.md notify .github 2>/dev/null || git add README.md notify
  git commit -q -m "chore: initial import"
  cp staged/notify/sender.py notify/sender.py
  mkdir -p tests && cp staged/tests/test_sender.py tests/test_sender.py
  rm -rf staged
  git add notify/sender.py tests/test_sender.py
  git --no-pager diff --staged > staged.diff
  echo "staged: $(git diff --staged --name-only | tr '\n' ' ')"
}

main "$@"
