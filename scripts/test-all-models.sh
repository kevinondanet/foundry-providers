#!/usr/bin/env bash
# Discover every deployment on a Foundry resource with the Azure CLI, then smoke-test each one through the
# sample's `test-all` command (chat, stream, native tools) using the signed-in identity (az login).
#
#   scripts/test-all-models.sh <account-name> <resource-group> [extra test-all args...]
set -euo pipefail
ACCOUNT=${1:?usage: $0 <account-name> <resource-group> [test-all args]}
RG=${2:?usage: $0 <account-name> <resource-group> [test-all args]}
shift 2

INFERENCE=$(az cognitiveservices account show -n "$ACCOUNT" -g "$RG" --query 'properties.endpoints."Azure AI Model Inference API"' -o tsv)
export AZUREAI_BASE_URL="${INFERENCE%/}/models"
export AZUREAI_RESOURCE_ID=$(az cognitiveservices account show -n "$ACCOUNT" -g "$RG" --query id -o tsv)

echo "endpoint: $AZUREAI_BASE_URL"
az cognitiveservices account deployment list -n "$ACCOUNT" -g "$RG" \
  --query "[].{deployment:name, model:properties.model.name, format:properties.model.format, state:properties.provisioningState}" -o table

ONLY=$(az cognitiveservices account deployment list -n "$ACCOUNT" -g "$RG" \
  --query "[?properties.provisioningState=='Succeeded'].name" -o tsv | paste -sd, -)

cd "$(dirname "$0")/.."
exec dotnet run --project src/InspectAzureAI.Sample -- test-all --only "$ONLY" "$@"
