#!/usr/bin/env bash
#
# rotate-logs.sh
# Delete all but the newest <keep> *.log files in <dir>

set -euo pipefail

usage() {
  echo "usage: $0 <dir> <keep>" >&2
  exit 2
}

main() {
  [[ $# -eq 2 ]] || usage
  local dir="$1" keep="$2"
  [[ -d "$dir" ]] || { echo "error: $dir is not a directory" >&2; exit 1; }
  [[ "$keep" =~ ^[0-9]+$ ]] || usage
  local -a logs=()
  while IFS= read -r f; do logs+=("$f"); done < <(ls -1t "$dir"/*.log 2>/dev/null || true)
  local i
  for ((i = keep; i < ${#logs[@]}; i++)); do
    rm -f -- "${logs[$i]}"
    echo "deleted: ${logs[$i]}"
  done
}

main "$@"
