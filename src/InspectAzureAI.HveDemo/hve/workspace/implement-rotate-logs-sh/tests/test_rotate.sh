#!/usr/bin/env bash
#
# test_rotate.sh
# Behavioural test for scripts/rotate-logs.sh

set -euo pipefail

main() {
  # Resolved from the working directory (the repository root), not from this file's location, so the scorer can
  # run a pristine copy of this test from wherever it restores it.
  local script="${ROTATE_LOGS_SCRIPT:-$PWD/scripts/rotate-logs.sh}"
  [[ -x "$script" ]] || { echo "FAIL: $script is not executable"; exit 1; }
  bash -n "$script"
  grep -q 'set -euo pipefail' "$script" || { echo "FAIL: strict mode missing"; exit 1; }
  head -1 "$script" | grep -q '^#!/usr/bin/env bash' || { echo "FAIL: shebang"; exit 1; }
  grep -Eq '^(function +)?main *\(\)' "$script" || { echo "FAIL: no main function"; exit 1; }

  local dir; dir="$(mktemp -d)"
  local i
  for i in 1 2 3 4 5; do
    printf 'x' > "$dir/app-$i.log"
    touch -d "2024-01-0${i}T00:00:00" "$dir/app-$i.log" 2>/dev/null || touch -t "2024010${i}0000" "$dir/app-$i.log"
  done
  printf 'keep me' > "$dir/notes.txt"

  local out; out="$("$script" "$dir" 2)"
  [[ -f "$dir/app-5.log" && -f "$dir/app-4.log" ]] || { echo "FAIL: newest files removed"; exit 1; }
  [[ ! -f "$dir/app-1.log" && ! -f "$dir/app-2.log" && ! -f "$dir/app-3.log" ]] || { echo "FAIL: old files kept"; exit 1; }
  [[ -f "$dir/notes.txt" ]] || { echo "FAIL: non-log file removed"; exit 1; }
  [[ "$(printf '%s\n' "$out" | grep -c '^deleted: ')" -eq 3 ]] || { echo "FAIL: expected 3 deleted lines, got: $out"; exit 1; }

  set +e
  "$script" "$dir/does-not-exist" 2 >/dev/null 2>&1; local rc=$?
  set -e
  [[ $rc -ne 0 ]] || { echo "FAIL: missing directory must fail"; exit 1; }
  rm -rf "$dir"
  echo "PASS"
}

main "$@"
