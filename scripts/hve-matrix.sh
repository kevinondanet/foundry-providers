#!/usr/bin/env bash
# Sweep the HVE demo across three axes, one sample per cell: how the model is reached (route), what drives
# it (harness), and whether the HVE Core plugin is layered on (framework).
#
#   route      foundry-anthropic | direct-anthropic | foundry-responses | direct-openai
#   harness    copilot (the GitHub Copilot CLI in the sandbox) | generic (Inspect's own agent loop)
#   framework  hve (the plugin is provisioned and briefed) | none (nothing is provisioned)
#
# Cells are named <route>-<harness>-<framework>, e.g. direct-anthropic-copilot-hve.
#
#   scripts/hve-matrix.sh                                     # every route, both harnesses, framework hve
#   scripts/hve-matrix.sh --routes direct-anthropic,direct-openai --frameworks hve,none
#   scripts/hve-matrix.sh --cells direct-openai-generic-none --dry-run
#
# Foundry cells authenticate with `az login` and AZUREAI_BASE_URL. Direct cells need OPENAI_API_KEY or
# ANTHROPIC_API_KEY *exported*; a key that lives only in an interactive shell profile is not visible here.
# Because Azure endpoint variables are usually also present, a direct cell drops them and pins
# OPENAI_BASE_URL / ANTHROPIC_BASE_URL so the provider's Azure guard admits the call. A cell whose
# credential is missing is skipped, not failed.
#
# Results are appended to results.tsv in the output directory and printed as a markdown table at the end.
set -uo pipefail

cd "$(dirname "$0")/.."
REPO=$(pwd)

TASK=implement
LIMIT=1
OUT_DIR="${HVE_MATRIX_OUT:-$REPO/logs/hve-matrix}"
ROUTES="foundry-anthropic,direct-anthropic,foundry-responses,direct-openai"
HARNESSES="copilot,generic"
FRAMEWORKS="hve"
CELLS=""
DRY_RUN=0
EXTRA=()

while [[ $# -gt 0 ]]; do
  case "$1" in
    --task)       TASK=${2:?--task needs a value}; shift 2 ;;
    --limit)      LIMIT=${2:?--limit needs a value}; shift 2 ;;
    --out)        OUT_DIR=${2:?--out needs a value}; shift 2 ;;
    --routes)     ROUTES=${2:?--routes needs a comma-separated list}; shift 2 ;;
    --harnesses)  HARNESSES=${2:?--harnesses needs a comma-separated list}; shift 2 ;;
    --frameworks) FRAMEWORKS=${2:?--frameworks needs a comma-separated list}; shift 2 ;;
    --cells)      CELLS=${2:?--cells needs a comma-separated list}; shift 2 ;;
    --dry-run)    DRY_RUN=1; shift ;;
    -h|--help)    sed -n '2,21p' "$0"; exit 0 ;;
    *)            EXTRA+=("$1"); shift ;;
  esac
done

mkdir -p "$OUT_DIR"
RESULTS="$OUT_DIR/results.tsv"
[[ -f "$RESULTS" ]] || printf 'cell\tmodel\troute\tharness\tframework\tstatus\tscores\ttokens\tseconds\tresolved\n' > "$RESULTS"

# The --model string each route is reached by. A bare name lets the demo route it; a prefix picks the vendor.
model_for() {
  case "$1" in
    foundry-anthropic) echo "claude-sonnet-4-6" ;;
    direct-anthropic)  echo "anthropic/${ANTHROPIC_MODEL:-claude-sonnet-4-6}" ;;
    foundry-responses) echo "gpt-5.6-sol" ;;
    direct-openai)     echo "openai/${OPENAI_MODEL:-gpt-5.6-sol}" ;;
    *) echo "unknown route '$1'" >&2; return 1 ;;
  esac
}

# A cell can only run if its credential is present. Foundry uses the signed-in Azure identity.
skip_reason() {
  case "$1" in
    foundry-*)        [[ -n "${AZUREAI_BASE_URL:-}" ]] || echo "AZUREAI_BASE_URL unset" ;;
    direct-anthropic) [[ -n "${ANTHROPIC_API_KEY:-}" ]] || echo "ANTHROPIC_API_KEY not exported" ;;
    direct-openai)    [[ -n "${OPENAI_API_KEY:-}" ]] || echo "OPENAI_API_KEY not exported" ;;
  esac
}

wanted() {
  [[ -z "$CELLS" ]] && return 0
  [[ ",$CELLS," == *",$1,"* ]]
}

echo "HVE matrix: task=$TASK limit=$LIMIT out=$OUT_DIR"
echo "axes: routes=$ROUTES harnesses=$HARNESSES frameworks=$FRAMEWORKS"
echo

for route in ${ROUTES//,/ }; do
  model=$(model_for "$route") || exit 2
  for harness in ${HARNESSES//,/ }; do
    for framework in ${FRAMEWORKS//,/ }; do
      name="$route-$harness-$framework"
      wanted "$name" || continue

      if reason=$(skip_reason "$route"); [[ -n "$reason" ]]; then
        printf '%-38s SKIPPED (%s)\n' "$name" "$reason"
        printf '%s\t%s\t%s\t%s\t%s\tskipped\t-\t-\t-\t%s\n' \
          "$name" "$model" "$route" "$harness" "$framework" "$reason" >> "$RESULTS"
        continue
      fi

      log="$OUT_DIR/$name.log"
      cmd=(dotnet run --project src/InspectAzureAI.HveDemo --no-build --
           --task "$TASK" --limit "$LIMIT" --model "$model" --harness "$harness"
           --framework "$framework" --log-dir "$OUT_DIR/logs" "${EXTRA[@]+"${EXTRA[@]}"}")

      if [[ $DRY_RUN == 1 ]]; then
        printf '%-38s %s\n' "$name" "${cmd[*]}"
        continue
      fi

      printf '%-38s running ... ' "$name"
      start=$SECONDS

      # A direct cell pins its own endpoint so the provider's Azure guard admits it, and drops the Azure
      # endpoint variables so nothing falls back to Foundry. A Foundry cell keeps the environment as-is.
      case "$route" in
        direct-openai)    (unset AZUREAI_BASE_URL AZUREAI_ENDPOINT_URL AZURE_ENDPOINT_URL
                           export OPENAI_BASE_URL="${OPENAI_BASE_URL:-https://api.openai.com/v1}"
                           "${cmd[@]}") > "$log" 2>&1 ;;
        direct-anthropic) (unset AZUREAI_BASE_URL AZUREAI_ENDPOINT_URL AZURE_ENDPOINT_URL
                           export ANTHROPIC_BASE_URL="${ANTHROPIC_BASE_URL:-https://api.anthropic.com}"
                           "${cmd[@]}") > "$log" 2>&1 ;;
        *)                "${cmd[@]}" > "$log" 2>&1 ;;
      esac

      code=$?
      elapsed=$((SECONDS - start))
      status=$([[ $code == 0 ]] && echo ok || echo "failed($code)")

      # The per-sample line lists whatever scorers the cell ran: four under hve, three under none (the
      # framework's own evidence scorer is dropped). Keep them in order as check/reported/quality[/evidence].
      scores=$(sed -nE 's/.*completed: (.*) \([0-9]+ tokens.*/\1/p' "$log" | head -1 | sed -E 's/[a-z_]+=//g; s/, /\//g')
      [[ -n "$scores" ]] || scores='-'
      tokens=$(sed -nE 's/^tokens   : ([0-9]+).*/\1/p' "$log" | head -1)
      [[ -n "$tokens" ]] || tokens='-'
      resolved=$(sed -nE 's/^model    : ([^ ]+).*/\1/p' "$log" | head -1)
      [[ -n "$resolved" ]] || resolved=$(grep -oiE 'error: [^(]*' "$log" | head -1 | cut -c1-70 | tr -d '\t')
      [[ -n "$resolved" ]] || resolved='-'

      printf '%s (%ss, %s tokens, %s)\n' "$status" "$elapsed" "$tokens" "$scores"
      printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' \
        "$name" "$model" "$route" "$harness" "$framework" "$status" "$scores" "$tokens" "$elapsed" "$resolved" >> "$RESULTS"
    done
  done
done

echo
echo "results: $RESULTS"
echo
awk -F'\t' 'NR==1 {print "| route | harness | framework | status | scores | tokens | seconds |";
                   print "|---|---|---|---|---|---|---|"; next}
            {printf "| %s | %s | %s | %s | %s | %s | %s |\n", $3, $4, $5, $6, $7, $8, $9}' "$RESULTS"
