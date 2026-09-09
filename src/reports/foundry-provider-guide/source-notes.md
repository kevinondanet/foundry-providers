# Source and validation notes

Documentation checked September 8, 2026; local rerun evidence is from September 7.

## Source inventory

- `azure`: [Local AzureAIModelApi implementation — working tree reviewed September 8, 2026](/Users/kevinburrowes/Documents/code/inspect-azureai-dotnet/src/InspectAzureAI.Provider/AzureAIModelApi.cs)
- `anthropic`: [Local AnthropicFoundryModelApi implementation](/Users/kevinburrowes/Documents/code/inspect-azureai-dotnet/src/InspectAzureAI.Provider/Anthropic/AnthropicFoundryModelApi.cs)
- `contract`: [Local IModelApi generation contract](/Users/kevinburrowes/Documents/code/inspect-azureai-dotnet/src/InspectAzureAI.Provider/Core/IModelApi.cs)
- `reasoning`: [Local reasoning-family mappings and recorded probe notes](/Users/kevinburrowes/Documents/code/inspect-azureai-dotnet/src/InspectAzureAI.Provider/Util/ReasoningParams.cs)
- `names`: [Local token-limit family detection](/Users/kevinburrowes/Documents/code/inspect-azureai-dotnet/src/InspectAzureAI.Provider/Util/OpenAIUtil.cs)
- `messages`: [Local Azure message conversion and Mistral reducer](/Users/kevinburrowes/Documents/code/inspect-azureai-dotnet/src/InspectAzureAI.Provider/Tools/AzureMessageConversion.cs)
- `tools`: [Local native tool conversion](/Users/kevinburrowes/Documents/code/inspect-azureai-dotnet/src/InspectAzureAI.Provider/Tools/AzureToolConversion.cs)
- `auth`: [Local Entra credential and audience configuration](/Users/kevinburrowes/Documents/code/inspect-azureai-dotnet/src/InspectAzureAI.Provider/Util/AzureHosting.cs)
- `catalog`: [Local ARM deployment discovery and capability interpretation](/Users/kevinburrowes/Documents/code/inspect-azureai-dotnet/src/InspectAzureAI.Provider/Foundry/FoundryCatalog.cs)
- `selection`: [Local model matrix deployment selection](/Users/kevinburrowes/Documents/code/inspect-azureai-dotnet/src/InspectAzureAI.ModelMatrix/DeploymentSelection.cs)
- `factory`: [Local provider factory and model wrapper](/Users/kevinburrowes/Documents/code/inspect-azureai-dotnet/src/InspectAzureAI.Eval/Model/FoundryModels.cs)
- `runtime`: [Local full evaluation model runtime](/Users/kevinburrowes/Documents/code/inspect-azureai-dotnet/src/InspectAzureAI.Eval/Model/Model.cs)
- `probes`: [Local parameter probe specifications and verdicts](/Users/kevinburrowes/Documents/code/inspect-azureai-dotnet/src/InspectAzureAI.Provider/Foundry/ParameterProbes.cs)
- `sample`: [Local provider sample CLI](/Users/kevinburrowes/Documents/code/inspect-azureai-dotnet/src/InspectAzureAI.Sample/Program.cs)
- `demo`: [Local seven-layer teaching demo](/Users/kevinburrowes/Documents/code/inspect-azureai-dotnet/src/InspectAzureAI.LayersDemo/README.md)
- `demo-azure`: [Teaching demo parameter learning and HTTP timeout](/Users/kevinburrowes/Documents/code/inspect-azureai-dotnet/src/InspectAzureAI.LayersDemo/Layer6_Providers/AzureAIProvider.cs)
- `demo-model`: [Teaching demo backoff and retries](/Users/kevinburrowes/Documents/code/inspect-azureai-dotnet/src/InspectAzureAI.LayersDemo/Layer5_Model/Model.cs)
- `rerun`: [September 7, 2026 expenses rerun: 21 deployments, including failed checks](/Users/kevinburrowes/Documents/code/inspect-azureai-dotnet/src/logs/expenses-astra-2026-09-07/run-manifest.json)
- `rerun-audit`: [September 7 request and usage audit for the 16 completed model evaluations](/Users/kevinburrowes/Documents/code/inspect-azureai-dotnet/src/logs/expenses-astra-2026-09-07/main-audit.json)
- `rerun-summary`: [September 7 rerun summary, failed checks, and corrected role accounting](/Users/kevinburrowes/Documents/code/inspect-azureai-dotnet/src/logs/expenses-astra-2026-09-07/SUMMARY.md)
- `sdk`: [Microsoft Learn — SDKs and endpoints](https://learn.microsoft.com/en-us/azure/foundry/how-to/develop/sdk-overview)
- `endpoints`: [Microsoft Learn — Foundry model endpoints](https://learn.microsoft.com/en-us/azure/foundry/foundry-models/concepts/endpoints)
- `migration`: [Microsoft Learn — Migration from Foundry classic](https://learn.microsoft.com/en-us/azure/foundry/how-to/navigate-from-classic)
- `responses`: [Microsoft Learn — Azure OpenAI Responses API](https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/responses)
- `claude`: [Microsoft Learn — Claude APIs and model capabilities](https://learn.microsoft.com/en-us/azure/foundry/foundry-models/concepts/claude-models)
- `models`: [Microsoft Learn — Models sold by Azure](https://learn.microsoft.com/en-us/azure/foundry/foundry-models/concepts/models-sold-directly-by-azure)
- `flux`: [Microsoft Learn — FLUX model API](https://learn.microsoft.com/en-us/azure/foundry/foundry-models/how-to/use-foundry-models-flux)
- `realtime`: [Microsoft Learn — Realtime audio over WebSockets](https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/realtime-audio-websockets)
- `compute`: [Microsoft Learn — Managed compute endpoint routes](https://learn.microsoft.com/en-us/azure/foundry/concepts/managed-compute-overview)
- `inference-rest`: [Microsoft Learn — model-inference Chat Completions wire contract](https://learn.microsoft.com/en-us/rest/api/microsoftfoundry/model-inference/get-chat-completions/get-chat-completions?view=rest-microsoftfoundry-model-inference-2024-05-01-preview)
- `evidence-routes`: [Operations, routes, and existing adapter coverage — reviewed source synthesis](/Users/kevinburrowes/Documents/code/inspect-azureai-dotnet/src/reports/foundry-provider-guide/query-routes.sql)
- `evidence-wire`: [Conversation and result translation — reviewed source synthesis](/Users/kevinburrowes/Documents/code/inspect-azureai-dotnet/src/reports/foundry-provider-guide/query-wire.sql)
- `evidence-families`: [Current family mappings and their boundaries — reviewed source synthesis](/Users/kevinburrowes/Documents/code/inspect-azureai-dotnet/src/reports/foundry-provider-guide/query-families.sql)
- `evidence-ownership`: [Responsibility boundaries — reviewed source synthesis](/Users/kevinburrowes/Documents/code/inspect-azureai-dotnet/src/reports/foundry-provider-guide/query-ownership.sql)
- `rerun-outcomes`: [Rerun outcome counts — tabulated from the saved run manifest](/Users/kevinburrowes/Documents/code/inspect-azureai-dotnet/src/reports/foundry-provider-guide/query-deployment_outcomes.sql)
- `evidence-rerun-results`: [What the September 7 rerun establishes — reviewed source synthesis](/Users/kevinburrowes/Documents/code/inspect-azureai-dotnet/src/reports/foundry-provider-guide/query-rerun-results.sql)

## Evidence table provenance

- `routes`: azure, anthropic, sdk, endpoints, responses, models, flux, realtime, compute
- `wire`: azure, anthropic, tools, responses, inference-rest
- `families`: reasoning, azure, anthropic, messages, names, demo-azure
- `ownership`: contract, runtime, azure, anthropic, auth, demo
- `rerun-results`: rerun, rerun-audit

The exact hashes, structure mapping, table contracts, and limitations are in source-notes.json. The C# build receipt is in example-build.txt.
