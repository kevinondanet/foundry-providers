#!/usr/bin/env bash
# Sweep the HVE demo across provider routes and harnesses for one sample per cell.
#
# The grid is (model, route) x harness, with --framework held at hve so the only variables are how the
# model is reached and what drives it:
#
#   claude-sonnet-4-6   foundry-anthropic   copilot | generic
#   claude-sonnet-4-6   direct-anthropic    copilot | generic
#   gpt-5.6-sol         foundry-responses   copilot | generic
#   gpt-5.6-sol         direct-openai       copilot | generic
#
# Foundry cells authenticate with `az login` and AZUREAI_BASE_URL. Direct cells need OPENAI_API_KEY or
# ANTHROPIC_API_KEY; because Azure endpoint variables are present, they also pin OPENAI_BASE_URL /
# ANTHROPIC_BASE_URL so the provider's Azure guard lets the direct call through. A cell whose credential
# is missing is skipped, not failed.
#
#   scripts/hve-matrix.sh [--task implement] [--limit 1] [--cells a,b] [--dry-run] [extra HveDemo args...]
#
# Results are appended to results.tsv in the output directory, one row per cell, and printed as a
# markdown table at the end.
set -uo pipefail

cd "$(dirname "$0")/.."
REPO=$(pwd)

TASK=implement
LIMIT=1
OUT_DIR="${HVE_MATRIX_OUT:-$REPO/logs/hve-matrix}"
CELLS=""
DRY_RUN=0
EXTRA=()

while [[ $# -gt 0 ]]; do
  case "$1" in
    --task)    TASK=${2:?--task needs a value}; shift 2 ;;
    --limit)   LIMIT=${2:?--limit needs a value}; shift 2 ;;
    --out)     OUT_DIR=${2:?--out needs a value}; shift 2 ;;
    --cells)   CELLS=${2:?--cells needs a comma-separated list}; shift 2 ;;
    --dry-run) DRY_RUN=1; shift ;;
    -h|--help) sed -n '2,25p' "$0"; exit 0 ;;
    *)         EXTRA+=("$1"); shift ;;
  esac
done

mkdir -p "$OUT_DIR"
RESULTS="$OUT_DIR/results.tsv"
[[ -f "$RESULTS" ]] || printf 'cell\tmodel\troute\tharness\tstatus\tscores\ttokens\tseconds\tnote\n' > "$RESULTS"

# One row per cell: name, the --model string, a human label for the route, and the harness.
CELL_NAMES=(
  claude-foundry-copilot claude-foundry-generic
  claude-direct-copilot  claude-direct-generic
  gpt-foundry-copilot    gpt-foundry-generic
  gpt-direct-copilot     gpt-direct-generic
)
CELL_MODELS=(
  claude-sonnet-4-6 claude-sonnet-4-6
  anthropic/claude-sonnet-4-6 anthropic/claude-sonnet-4-6
  gpt-5.6-sol gpt-5.6-sol
  openai/gpt-5.6-sol openai/gpt-5.6-sol
)
CELL_ROUTES=(
  foundry-anthropic foundry-anthropic
  direct-anthropic  direct-anthropic
  foundry-responses foundry-responses
  direct-openai     direct-openai
)
CELL_HARNESS=(copilot generic copilot generic copilot generic copilot generic)

wanted() {
  [[ -z "$CELLS" ]] && return 0
  [[ ",$CELLS," == *",$1,"* ]]
}

# A cell can only run if its credential is present. Foundry uses the signed-in Azure identity.
skip_reason() {
  case "$1" in
    foundry-*)        [[ -n "${AZUREAI_BASE_URL:-}" ]] || echo "AZUREAI_BASE_URL unset" ;;
    direct-anthropic) [[ -n "${ANTHROPIC_API_KEY:-}" ]] || echo "ANTHROPIC_API_KEY unset" ;;
    direct-openai)    [[ -n "${OPENAI_API_KEY:-}" ]] || echo "OPENAI_API_KEY unset" ;;
  esac
}

echo "HVE matrix: task=$TASK limit=$LIMIT out=$OUT_DIR"
echo

for i in "${!CELL_NAMES[@]}"; do
  name=${CELL_NAMES[$i]}
  model=${CELL_MODELS[$i]}
  route=${CELL_ROUTES[$i]}
  harness=${CELL_HARNESS[$i]}

  wanted "$name" || continue

  if reason=$(skip_reason "$route"); [[ -n "$reason" ]]; then
    printf '%-26s SKIPPED (%s)\n' "$name" "$reason"
    printf '%s\t%s\t%s\t%s\tskipped\t-\t-\t-\t%s\n' "$name" "$model" "$route" "$harness" "$reason" >> "$RESULTS"
    continue
  fi

  log="$OUT_DIR/$name.log"
  cmd=(dotnet run --project src/InspectAzureAI.HveDemo --no-build --
       --task "$TASK" --limit "$LIMIT" --model "$model" --harness "$harness"
       --framework hve --log-dir "$OUT_DIR/logs" "${EXTRA[@]+"${EXTRA[@]}"}")

  if [[ $DRY_RUN == 1 ]]; then
    printf '%-26s %s\n' "$name" "${cmd[*]}"
    continue
  fi

  printf '%-26s running ... ' "$name"
  start=$SECONDS

  # A direct cell pins its own endpoint so the provider's Azure guard admits it, and drops the Azure
  # endpoint variable so nothing falls back to Foundry. A Foundry cell keeps the environment as-is.
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
  # The per-sample line carries all four scorers: hve_check=C, artefact_reported=C, ... Keep them in that
  # order so a cell reads as check/reported/quality/evidence.
  scores=$(grep -oE 'hve_check=[A-Z], artefact_reported=[A-Z], artefact_quality=[A-Z], hve_artefact_used=[0-9.]+' "$log" \
           | head -1 | sed -E 's/[a-z_]+=//g; s/, /\//g')
  [[ -n "$scores" ]] || scores='-'
  tokens=$(grep -oE 'tokens   : [0-9]+' "$log" | head -1 | grep -oE '[0-9]+')
  [[ -n "$tokens" ]] || tokens='-'
  note=$(grep -oiE 'error: [^(]*' "$log" | head -1 | cut -c1-90 | tr -d '\t')
  [[ -n "$note" ]] || note='-'

  printf '%s (%ss, %s tokens)\n' "$status" "$elapsed" "$tokens"
  printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' \
    "$name" "$model" "$route" "$harness" "$status" "$scores" "$tokens" "$elapsed" "$note" >> "$RESULTS"
done

echo
echo "results: $RESULTS"
echo
awk -F'\t' 'NR==1 {printf "| %s | %s | %s | %s | %s | %s |\n|---|---|---|---|---|---|\n", $2, $3, $4, $5, $7, $8; next}
            {printf "| `%s` | %s | %s | %s | %s | %s |\n", $2, $3, $4, $5, $7, $8}' "$RESULTS"
