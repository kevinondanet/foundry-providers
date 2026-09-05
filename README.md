# InspectAzureAI (lite) — Azure AI Foundry model providers for .NET, `az login` only

This branch is the **lite** cut of the .NET 10 port of the
[Inspect AI](https://inspect.aisi.org.uk) `azureai` model provider — the adapter for
[Azure AI Foundry](https://ai.azure.com/) model-inference endpoints — plus its companion provider for
Claude deployments on the Anthropic Messages route. It keeps everything needed to drive the deployments
that were verified on a live Foundry resource (below) with a developer sign-in, and drops the rest:

| Kept | Dropped (available on `main`) |
|---|---|
| Entra ID authentication through `DefaultAzureCredential`, which picks up `az login` (and managed identity when hosted) | API keys (`AZURE_API_KEY` / `AZUREAI_API_KEY` / `AZUREAI_ANTHROPIC_API_KEY`), the api-key override hook, the `AZUREAI_CREDENTIAL` selector and `--auth` |
| **Native tool calling** (`tools` / `tool_choice` on the wire, `tool_calls` parsed back), on both routes | Llama detection and `<tool_call>` prompt-format tool emulation (`emulate_tools`, `Llama31Handler`) |
| Streaming with `on_stream` events, images, `ModelCall` capture, retry classification, content-filter stop details | The YAML fallback for non-JSON tool arguments (native function calling always returns JSON) |
| The Mistral rules, the gpt-5 / o-series `max_completion_tokens` rule, `-M max_completion_tokens=true` for MAI-Thinking-1 | |
| **Reasoning controls** (`--reasoning-effort`, `--reasoning-tokens` mapped to each vendor's field), reasoning text and token counts parsed back, Claude thinking replayed on later turns | |
| A `params` probe that discovers which request parameters each deployment accepts (the matrix below) | |
| Deployment discovery (`models`) and the `test-all` smoke matrix | |
| `AnthropicFoundryModelApi` for `claude-*` deployments (bearer token, `tool_use`, streaming, images) | |

The Python source of truth is `src/inspect_ai/model/_providers/azureai.py`; every C# type still cites the
Python location it ports. The port targets **net10.0** on
[`Azure.AI.Inference`](https://www.nuget.org/packages/Azure.AI.Inference) and
[`Azure.Identity`](https://www.nuget.org/packages/Azure.Identity).

## Solution layout

```
InspectAzureAI.sln
├── src/InspectAzureAI.Provider     class library — the providers
│   ├── Core/                       minimal Inspect types (messages, content, tools, config, output, ModelCall, stream events)
│   ├── Util/                       azure_hosting.py (DefaultAzureCredential + audience), http retry, _openai.py helpers, images, JSON
│   ├── Tools/                      parse_tool_call, tool/message conversion (native tools only)
│   ├── Testing/                    CannedTransport — an offline HttpPipelineTransport
│   ├── Foundry/                    FoundryCatalog — deployment discovery through Azure Resource Manager
│   ├── Anthropic/                  AnthropicFoundryModelApi — Claude deployments on the Anthropic Messages route
│   ├── AzureAIModelApi.cs          port of AzureAIAPI (Entra ID only)
│   ├── AzureAIStreamAccumulator.cs port of azureai_completion_from_stream
│   ├── AzureChatCompletions.cs     dict-backed response view (raw JSON)
│   └── SseParser.cs                server-sent-events reader
├── src/InspectAzureAI.Sample       console app: chat, stream, tools, image, token, models, test-all, capture, ...
├── docs/dashboard                  the wire dashboard: template + build script output (see below)
└── tests/InspectAzureAI.Tests      xunit suite (offline, canned transport, fake token credential)
```

## Architecture

```mermaid
flowchart LR
    subgraph Caller
        S[Sample / your code]
    end
    subgraph Provider["InspectAzureAI.Provider"]
        API[AzureAIModelApi]
        ANT[AnthropicFoundryModelApi<br/>claude-* deployments]
        CRED[AzureHosting<br/>DefaultAzureCredential + AZUREAI_AUDIENCE]
        MC[AzureMessageConversion<br/>AzureToolConversion]
        ACC[AzureAIStreamAccumulator<br/>+ SseParser]
        OBS[ModelStreamObserver<br/>on_stream events]
        CALL[ModelCall<br/>request/response capture]
        RET[ShouldRetry / IsAuthFailure]
    end
    subgraph Azure
        EP[/models/chat/completions]
        AEP[/anthropic/v1/messages]
    end
    S -->|ChatMessage, ToolInfo, ToolChoice, GenerateConfig| API
    S --> ANT
    CRED -->|Authorization: Bearer| API
    CRED -->|Authorization: Bearer| ANT
    API --> MC -->|ChatCompletionsClient| EP
    ANT -->|HttpClient| AEP
    EP -->|JSON / SSE| ACC --> OBS --> S
    API --> CALL
    API -.->|thrown RequestFailedException| RET
    API -->|GenerateResult| S
```

## Environment variables

| Variable | Meaning |
|---|---|
| `AZURE_ENDPOINT_URL`, `AZUREAI_ENDPOINT_URL`, `AZUREAI_BASE_URL` | endpoint, consulted in that order (e.g. `https://<resource>.services.ai.azure.com/models`) |
| `INSPECT_EVAL_MODEL_BASE_URL` | last-resort endpoint fallback |
| `AZUREAI_AUDIENCE` | Entra ID token scope (default `https://cognitiveservices.azure.com/.default`) |
| `AZURE_TENANT_ID`, `AZURE_CLIENT_ID` | *(Azure.Identity standard, read by `DefaultAzureCredential` itself)* tenant pin; client id of a user-assigned managed identity |
| `AZUREAI_ANTHROPIC_BASE_URL`, `AZURE_ANTHROPIC_BASE_URL` | Anthropic route base URL; when unset it is derived from `AZUREAI_BASE_URL` (`…/models` → `…/anthropic`) |
| `AZUREAI_RESOURCE_ID`, `AZURE_SUBSCRIPTION_ID` | `models` / `test-all`: the Foundry resource id (skips discovery) or the subscription to search; otherwise every readable subscription is searched for the account whose endpoints include the `AZUREAI_BASE_URL` host |
| `INSPECT_AZUREAI_MODEL` | *(sample only)* default model name (`gpt-5.4-mini` when unset) |

No API-key variable is read. Both providers resolve `DefaultAzureCredential` at construction and send the
token only as `Authorization: Bearer` (fidelity note 16). A missing endpoint raises `PrerequisiteError`
with the Python message.

### Signing in with `az login`

```bash
az login                                   # or: az login --tenant <tenant-id>
az account set --subscription <name|id>    # the subscription that owns the endpoint
export AZUREAI_BASE_URL=https://<resource>.services.ai.azure.com/models

dotnet run --project src/InspectAzureAI.Sample -- token      # who does the credential resolve to?
dotnet run --project src/InspectAzureAI.Sample -- chat "hello"
```

`token` acquires a token for `AZUREAI_AUDIENCE` and prints the identity, tenant, audience and expiry
from its claims (never the token itself), so a wrong tenant or an expired login shows up before the
first model call. `DefaultAzureCredential` probes managed identity before the Azure CLI, which can add
a few seconds on a developer machine; a host that wants the CLI directly can pass any `TokenCredential`
(for example `AzureCliCredential`) through `AzureAIClientSettings.TokenCredential`.

**A data-plane role is required.** Owner or Contributor on the subscription is not enough; the endpoint
answers `401 ... lacks the required data action Microsoft.CognitiveServices/accounts/MaaS/chat/completions/action`.
Assign **Cognitive Services User** on the resource, then allow several minutes for propagation:

```bash
az role assignment create --role "Cognitive Services User" \
  --assignee-object-id "$(az ad signed-in-user show --query id -o tsv)" --assignee-principal-type User \
  --scope "$(az cognitiveservices account show -n <resource> -g <rg> --query id -o tsv)"
```

The same role covers the Azure Resource Manager reads that `models` and `test-all` need.

## Building and running

```bash
dotnet build InspectAzureAI.sln -warnaserror
dotnet test  InspectAzureAI.sln
```

Every sample command accepts `--model <name>`, `--streaming auto|true|false`, `--temperature <n>`,
`--max-tokens <n|none>`, `--reasoning-effort <none|minimal|low|medium|high|xhigh|max>`, `--reasoning-tokens <n>`,
`--model-arg key=value` (repeatable, the Python `-M` args; JSON values such as `thinking={"type":"enabled"}` are
parsed), `--route models|anthropic` and `--fake` (answer from an in-memory canned endpoint with a dummy token —
no network, no sign-in). `params` and `capture --params` also take `--params all|a,b` and `--parallel <n>`.

```bash
export AZUREAI_BASE_URL=https://<resource>.services.ai.azure.com/models
S="dotnet run --project src/InspectAzureAI.Sample --"

$S --help                          # list everything
$S config                          # resolved endpoint / credential / names / token-limit parameter, no call made
$S token                           # acquire a token via az login and print its identity
$S models                          # discover the resource behind AZUREAI_BASE_URL via ARM and list its deployments
$S test-all                        # chat + stream + native tools against every healthy deployment; exit 1 on a chat failure
$S test-all --only gpt-5.4-mini,DeepSeek-V4-Flash --skip-tools --json
$S capture --include-failed --params all --out docs/dashboard/calls.json   # record every HTTP exchange + the parameter probes
$S params --only gpt-5.4-mini,Kimi-K2.6            # which parameters does each deployment accept? (verdicts + evidence)
$S chat --model gpt-5.4-mini --reasoning-effort high "Is 10403 a prime number?"   # reasoning_effort on the wire, reasoning_tokens back
$S chat --model Kimi-K2.6 --reasoning-effort none "hi"                            # thinking {type: disabled} (Kimi ignores it, see the matrix)
$S chat --route anthropic --model claude-sonnet-4-6 --reasoning-effort medium "Is 10403 prime?"   # adaptive thinking + effort
$S stream --model Kimi-K2.6 "Is 10403 a prime number?"                            # reasoning deltas stream dimmed before the answer
$S chat --model DeepSeek-V4-Flash --model-arg 'thinking={"type":"enabled"}' "hi"  # any vendor object as a JSON model arg
scripts/test-all-models.sh <resource> <resource-group>   # same, with the deployment list taken from the Azure CLI
$S naming                          # service / canonical names and which token-limit field each verified model gets
$S chat "What are you?"            # non-streaming completion
$S stream "Tell me a joke"         # streaming: deltas printed as they arrive
$S tools "Weather in Paris?"       # native function-calling loop with get_weather (see below)
$S tools --route anthropic --model claude-sonnet-4-6 "Weather in Oslo?"
$S image ./photo.png "What is in this picture?"
$S chat --model MAI-Thinking-1 --model-arg max_completion_tokens=true "hello"   # reasoning models reject max_tokens
$S chat --model gpt-5.4-mini --temperature 0 "hello"   # temperature is optional; gpt-5 deployments accept only 1
$S retry-demo                      # ShouldRetry / IsAuthFailure / HandleAzureError decisions
$S tools --fake                    # any command, offline
```

Each generating command prints the captured `ModelCall` request and response JSON (with base64 image
payloads redacted to `<base64-data-removed>`), the output, tool calls, stop details and token usage.

### Tool calling

Tool calling is native on every supported deployment: the `ToolInfo` list is sent as OpenAI-style
`tools` with the `tool_choice`, the model answers with `tool_calls` (or `tool_use` blocks on the
Anthropic route), the provider parses each call's JSON arguments into a `ToolCall` (recording a
`ParseError` instead of throwing when the arguments are malformed), and the caller executes the tool and
appends a `ChatMessageTool` carrying the `tool_call_id`. The sample's `tools` command runs that loop with a
local `get_weather` tool until the model answers in plain text:

```
== turn 1 (native tool_calls, model-inference route) ==
   request  ... "tools":[{"type":"function","function":{"name":"get_weather", ...}}], "tool_choice":"auto"
   response ... "finish_reason":"tool_calls", "tool_calls":[{"id":"call_…","function":{"name":"get_weather","arguments":"{\"city\": \"Paris\"}"}}]
== turn 2 (native tool_calls, model-inference route) ==
   request  ... {"role":"assistant","tool_calls":[…]}, {"role":"tool","tool_call_id":"call_…","content":"{\"city\":\"Paris\",\"temperature_c\":21, …}"}
final answer: The weather in Paris is 21C and sunny with a light breeze.
```

Mistral deployments get the Python `mistral_message_reducer` treatment (a user message that directly
follows a tool message is folded into it); OpenAI-format deployments require the `tool` message to follow
the assistant `tool_calls` message, which is the shape the loop produces.

### The wire dashboard

`docs/dashboard/index.html` is a self-contained page that shows one real call to every deployment on the
resource exactly as it went over HTTP (plus the parameter matrix and the probe exchanges behind it): the request line, every request header (the bearer token is
replaced by its length), the JSON body, the response status and headers (rate limits, region, served
model, request ids), the body as JSON or as the individual server-sent events, and the output the provider
parsed from it. Each header and body field carries a plain-language note, so the page doubles as a tour of
how the provider talks to Foundry and how the Anthropic route differs. To refresh it against your own
resource:

```bash
$S capture --include-failed --params all --out docs/dashboard/calls.json   # chat + stream + tools + reasoning per deployment, then the probes
scripts/build-dashboard.py                                                 # embeds calls.json into docs/dashboard/index.html
scripts/build-readme-matrix.py                                             # regenerates the "Parameters by model" table above
open docs/dashboard/index.html
```

`capture` records the traffic with an Azure.Core pipeline policy on the model-inference route and a
delegating handler on the Anthropic route; the build script refuses to embed a report that still contains a
credential header.

### Thinking and reasoning parameters

Inspect names the controls `reasoning_effort` (`none|minimal|low|medium|high|xhigh|max`) and `reasoning_tokens`
(a budget); the port keeps those names on `GenerateConfig` and maps them to each vendor's wire field
(`Util/ReasoningParams.cs`, keyed on the deployment's ARM `Format` when the sample knows it, else on the
name; `config` and `naming` print the family and the derived fields):

| Family (ARM `Format`) | `--reasoning-effort` becomes | `--reasoning-tokens` becomes | What comes back |
|---|---|---|---|
| OpenAI gpt-5.x and model-router, xAI (grok-4.6), Microsoft (MAI-Thinking-1) | `reasoning_effort: <level>` verbatim (`none` included; grok and MAI keep reasoning regardless) | ignored | no reasoning text; `usage.completion_tokens_details.reasoning_tokens` → `ModelUsage.ReasoningTokens` (model-router streams the routed model's `reasoning_content`) |
| DeepSeek V4 | `reasoning_effort: <level>` (thinking is off by default; the `thinking` object is ignored on Foundry) | ignored | `reasoning_content` → a leading `ContentReasoning`; stream `usage.reasoning_tokens` |
| MoonshotAI (Kimi K2) | `thinking: {type: enabled}`, or `disabled` for `none` (accepted but ignored: Kimi reasons regardless) | ignored | `reasoning_content` → `ContentReasoning`; stream `usage.reasoning_tokens` |
| Cohere | `thinking: {type: enabled}` / `disabled` (`disabled` is ignored) | `thinking.token_budget` | `reasoning_content` → `ContentReasoning` |
| OpenAI without reasoning (gpt-4o), Mistral AI, unknown | nothing (gpt-4o answers HTTP 400 to `reasoning_effort`; Mistral rejects every reasoning field) | nothing | nothing |
| Anthropic (Claude, Messages route) | `thinking: {type: adaptive}` + `output_config.effort` (`minimal` → `low`); `none` omits `thinking` | `thinking: {type: enabled, budget_tokens}` (the deprecated 4.6 form; `max_tokens` raised above it) | `thinking` blocks → `ContentReasoning` with `signature`; no separate token count |

`--model-arg` still wins over the derived field (model args are applied last), so any vendor object can be
tried verbatim: `--model-arg 'thinking={"type":"enabled","budget_tokens":2048}'`. Two reserved model args
exist: `model_format=<vendor>` names the family when the deployment name does not, and `anthropic_beta=<list>`
becomes the `anthropic-beta` header on the Anthropic route.

Reasoning text arrives as `ContentReasoning` items placed first on the assistant message (`Text` and
`Completion` stay text-only); `ModelUsage.ReasoningTokens` and `InputTokensCacheRead` are filled from
`completion_tokens_details` / `prompt_tokens_details` (Kimi, DeepSeek and model-router report the count only
in stream mode, as a top-level `usage.reasoning_tokens`); streamed reasoning is delivered as
`StreamReasoningEvent` (the sample prints it dimmed before the answer). On the Anthropic route the thinking
blocks are replayed unchanged, with their signature, ahead of text and `tool_use` on later turns — the
Messages API rejects a turn without them. On the model-inference route reasoning is **not** replayed
(DeepSeek rejects an echoed `reasoning_content`). The `reasoning` smoke check in `test-all` reports `text`
(reasoning text came back), `hidden` (only a token count), `none` (a reasoning field was sent, nothing came
back) or `n/a` (the family has no control). Streamed requests that carry pass-through fields now get the
`extra-parameters: pass-through` header the SDK only sets on non-streaming calls (fidelity note 21).

### Using the provider from code

```csharp
var api = new AzureAIModelApi("gpt-5.4-mini");                    // AZUREAI_BASE_URL + DefaultAzureCredential (az login)
var input = new List<ChatMessage> { new ChatMessageUser("What is the weather in Paris?") };
var config = new GenerateConfig { MaxTokens = api.MaxTokens() };

while (true)
{
    var result = await api.GenerateAsync(input, [weatherTool], ToolChoice.Auto, config,
        onStream: e => { if (e is StreamTextEvent t) Console.Write(t.Text); return Task.CompletedTask; });
    var output = result.OutputOrThrow();          // ModelOutput; result.Call is the ModelCall record
    input.Add(output.Message);
    if (output.Message.ToolCalls is not { Count: > 0 } calls) break;
    foreach (var call in calls)                   // call.Function, call.Arguments (JsonObject), call.ParseError
        input.Add(new ChatMessageTool(RunWeather(call.Arguments), call.Id, call.Function));
}
```

Claude deployments use `new AnthropicFoundryModelApi("claude-sonnet-4-6")` behind the same `IModelApi`
contract. `GenerateAsync` mirrors the Python return contract: a `GenerateResult` carrying either the
`ModelOutput` or, for a terminal HTTP 400, the exception (which Inspect wraps without retrying); every
other Azure failure is recorded on the `ModelCall` and **thrown** — already normalised to
`RequestFailedException` / `ServiceResponseException` (fidelity note 3) — so the caller can consult
`ShouldRetry(ex)` / `IsAuthFailure(ex)`. Non-Azure failures (an empty stream, a malformed SSE chunk,
caller cancellation) propagate unrecorded and unretried, exactly as non-`AzureError` exceptions do in Python.

## Verified against a live Foundry resource

Checked against an Azure AI Foundry resource (kind `AIServices`, eastus2) using `az login` only, endpoint
`https://<resource>.services.ai.azure.com/models`. `models` discovered the resource through Azure Resource
Manager with the same identity and listed twenty-one deployments; `test-all` then ran chat, streaming, a
native tool call and a reasoning call (`--reasoning-effort medium` mapped per family) against each:

| Deployment | Format | chat | stream | tools | reasoning | Note |
|---|---|---|---|---|---|---|
| gpt-5.6-sol, gpt-5.6-luna, gpt-5.6-luna-2, gpt-5.6-terra, gpt-5.4-mini | OpenAI | ok | ok | ok | hidden · 33–52 tok | `max_completion_tokens` sent (gpt-5 rule); usage not reported in stream mode (as in Python) unless `--model-arg stream_options={"include_usage":true}`, which the probe found accepted |
| gpt-4o | OpenAI | ok | ok | ok | n/a | `max_tokens` sent (the gpt-5 / o-series rule does not apply) |
| model-router | OpenAI | ok | ok | ok | text | answered from a routed model (`grok-4-1-fast-reasoning` in one run) |
| DeepSeek-V4-Pro, DeepSeek-V4-Flash, DeepSeek-V4-Flash-0731 | DeepSeek | ok | ok | ok | text | |
| Mistral-Large-3 | Mistral AI | ok | ok | ok | n/a | Mistral naming rules apply (`max_tokens()` is null, user-after-tool messages folded) |
| Ministral-3B | Mistral AI | ok | ok | ok | n/a | the name does not contain `mistral`, so — exactly as in Python — the Mistral rules do not apply and `max_tokens` 2048 is sent, which the deployment accepts; capacity 1, ~60 s for its three checks |
| MAI-Thinking-1 | Microsoft | ok | ok | ok | hidden · 667 tok | rejects `max_tokens`; `test-all` retried with `max_completion_tokens=true` automatically (fidelity note 13) |
| Kimi-K2.7-Code, Kimi-K2.6 | MoonshotAI | ok | ok | ok | text | |
| Cohere-command-a-plus-05-2026 | Cohere | ok | ok | ok | text | |
| grok-4.6 | xAI | ok | ok | ok | hidden · 238 tok | ~20–28 s for the three checks |
| claude-sonnet-4-6 | Anthropic | ok | ok | ok | text | the model-inference route answers `Requested API is currently not supported` for Anthropic deployments; `test-all` sends them through the Anthropic Messages route (`/anthropic/v1/messages`, same bearer token) via the companion provider. `image` on this route also described a test picture correctly |
| gpt-5.4-pro | OpenAI | — | — | — | — | not a chat-completions deployment (`models` shows chat = no): HTTP 400 `The requested operation is unsupported.`, returned as the terminal error |
| Cohere-parse-v5 | Cohere | — | — | — | — | document-parsing model (`models` shows chat = no): HTTP 404 `Requested API is currently not supported` |
| FLUX.2-pro | Black Forest Labs | — | — | — | — | image-generation model: HTTP 404 `Service request failed.` on `/chat/completions`. The ARM capability metadata still marks it chat-capable, so a bare `test-all` includes it and exits 1; use `--only` to pick the chat deployments |

The first twelve deployments were recorded with the full port on 2 September 2026 and re-run on
this lite branch on 3 September 2026 with the same `az login` identity (`12/12 tested deployments answered
chat; 0 skipped`). The nine deployments added to the resource later that day were checked on the lite
branch the same afternoon with `test-all --include-failed --only …`: `6/9`, the three non-chat models
failing at the endpoint as noted. MAI-Thinking-1 goes through the automatic `max_completion_tokens=true`
retry and claude-sonnet-4-6 through the Anthropic Messages route. Slowest of the whole set: Ministral-3B
(~61 s), DeepSeek-V4-Pro (~33 s) and grok-4.6 (~28 s). The `reasoning` column comes from the run later that
day with the reasoning work in place (`18/19 tested deployments answered chat; 2 skipped`, FLUX.2-pro being
the one failure): `text` = reasoning text came back as `ContentReasoning`, `hidden` = only a token count,
`n/a` = the family has no reasoning control (gpt-4o rejects `reasoning_effort`, Mistral rejects every
reasoning field).

Do not send `temperature` to gpt-5 deployments unless it is 1; the sample leaves it unset by default and
`--temperature <n>` sets it explicitly.

## Parameters by model

Measured by `params` (and `capture --params all`): every chat deployment gets a baseline call, a streamed
baseline, then one call per candidate parameter sent alone on top of the baseline, and each is classified
from the HTTP status and the response. `ok` = accepted with a visible effect (two choices for `n`, logprobs
present, a JSON object for `response_format`, the count stopping before "4" for `stop`, usage appearing on
the stream, a reasoning signal appearing or disappearing); `ok (no visible effect)` = HTTP 200 for a parameter
the probe cannot observe (temperature, top_p, seed, penalties, top_k, verbosity, effort); `ignored` = HTTP 200
but no effect; `rejected` = HTTP 400 (the evidence in the dashboard quotes the service); `-` = not probed for
that route. The dashboard's "Parameter matrix" view shows the same data with the evidence and the recorded
exchange behind every cell.

<!-- params-matrix:start -->
| Deployment | reasoning | `baseline` | `stream` | `temperature` | `top_p` | `seed` | `frequency_penalty` | `presence_penalty` | `stop` | `n` | `logprobs` | `parallel_tool_calls` | `response_format.json_object` | `response_format.json_schema` | `max_tokens` | `stream_options` | `reasoning_effort=none` | `reasoning_effort=low` | `reasoning_effort=high` | `reasoning_effort=xhigh` | `verbosity=low` | `reasoning_tokens` | `sampling-extras` | `max_completion_tokens` | `thinking.enabled` | `thinking.disabled` | `top_k` | `metadata` | `thinking.adaptive` | `output_config.effort=low` | `thinking.budget` |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| gpt-5.6-sol, gpt-5.6-luna, gpt-5.6-luna-2, gpt-5.6-terra | hidden · 30–53 tok | ok* | ok | rej | rej | ok* | rej | rej | rej | ok | rej | ok* | ok | ok | rej | ok | ok | ok | ok | ok | ok* | n/a | - | - | - | - | - | - | - | - | - |
| model-router | text | ok* | ok | ok* | ok* | - | - | - | ign | ok | ok | ok* | ok | rej | - | ok* | ign | ok | ok | ok | ok* | n/a | ok* | ok* | ok | ign | - | - | - | - | - |
| DeepSeek-V4-Pro, DeepSeek-V4-Flash, DeepSeek-V4-Flash-0731 | text | ok* | ok | ok* | ok* | - | - | - | ok | ok | ok | ok* | ok | ok | - | ok* | ok* | ok | ok | - | - | n/a | ok* | ok* | ign | ok* | ok* | - | - | - | - |
| gpt-5.4-mini | hidden · 37 tok | ok* | ok | ok* | ok* | - | - | - | rej | ok | ok | ok* | ok | ok | rej | ok | ok* | ok | ok | ok | ok* | n/a | ok* | - | - | - | - | - | - | - | - |
| claude-sonnet-4-6 | text | ok* | ok | ok* | ok* | - | - | - | ok | - | - | - | - | - | - | - | - | - | ok | - | - | ok | - | - | ok | ok* | ok* | ok* | ok | ok* | - |
| Mistral-Large-3 | n/a | ok* | ok | ok* | ok* | - | - | - | ok | ok | rej | ok* | ok | ok | - | ok* | - | - | rej | - | - | n/a | ok* | rej | rej | rej | rej | - | - | - | rej |
| MAI-Thinking-1 | hidden · 889 tok | ok* | ok | ok* | ok* | - | - | - | ign | ign | ign | ok* | rej | rej | rej | ok* | ign | ok* | ok* | - | - | n/a | ok* | - | ok* | ign | ok* | - | - | - | - |
| Kimi-K2.7-Code, Cohere-command-a-plus-05-2026, Kimi-K2.6 | text | ok* | ok | ok* | ok* | - | - | - | ign | ok | ok | ok* | ok | ok | - | ok* | - | - | ok | - | - | ok | ok* | ok* | ok | ign | ok* | - | - | - | ok |
| grok-4.6 | hidden · 227 tok | ok* | ok | ok* | ok* | ok* | rej | rej | rej | rej | ok | ok* | ok | ok | - | ok* | - | ok | ok | - | - | n/a | - | ok* | ok | ign | ok* | - | - | - | - |
| gpt-5.4-pro | fail | rej | - | - | - | - | - | - | - | - | - | - | - | - | - | - | - | - | - | - | - | - | - | - | - | - | - | - | - | - | - |
| Cohere-parse-v5, FLUX.2-pro | fail | err | - | - | - | - | - | - | - | - | - | - | - | - | - | - | - | - | - | - | - | - | - | - | - | - | - | - | - | - | - |
| Ministral-3B | n/a | ok* | ok | ok* | ok* | rej | ok* | ok* | ok | ok | rej | ok* | ok | ok | - | rej | - | - | rej | - | - | n/a | - | rej | rej | rej | rej | - | - | - | rej |
| gpt-4o | n/a | ok* | ok | ok* | ok* | - | - | - | ok | ok | ok | ok* | ok | ok | - | ok | - | - | rej | - | - | n/a | ok* | ok* | rej | rej | rej | - | - | - | rej |

`ok` accepted with a visible effect · `ok*` accepted, no visible effect · `ign` HTTP 200 but no effect · `rej` HTTP 400 · `err` other failure · `n/a` the provider derives nothing for this family · `-` not probed for this route.

Probed on 2026-09-03 22:09:38Z against myfoundry0406.
<!-- params-matrix:end -->

## Claude deployments: the Anthropic Messages route

Anthropic models on Foundry are not served on the model-inference route; Inspect reaches them with its
`anthropic` provider (`anthropic/azure/<deployment>`, Messages API). `Anthropic/AnthropicFoundryModelApi.cs`
follows the Azure path of that provider (same base-URL variables, `max_tokens` rule, stop-reason and usage
mapping, `input_schema` tools, `tool_result` blocks, base64 image sources, SSE streaming with `text_delta`
and `input_json_delta`) behind the same `IModelApi` contract, sends `anthropic-version: 2023-06-01`, and
differs in two ways: it authenticates with the same Entra ID credential as the main provider (Python
requires `AZUREAI_ANTHROPIC_API_KEY`), and it derives the base URL from the inference endpoint. Extended
thinking is mapped from `ReasoningEffort` / `ReasoningTokens` (adaptive thinking plus `output_config.effort`,
or the deprecated `budget_tokens` form) and thinking blocks are parsed, streamed (`thinking_delta`,
`signature_delta`) and replayed with their signature; model args become top-level fields. Not ported:
prompt caching, citations, batch mode, server-side tools, `thinking.display`, the Python client's retry
policy. Errors follow the same contract as the main provider (400 returned, 408/429/5xx thrown for
`ShouldRetry`, 401 as `IsAuthFailure`).

## Python → C# mapping

| Python (inspect_ai) | C# |
|---|---|
| `model/_providers/azureai.py` `AzureAIAPI.__init__` (Entra branch only) | `AzureAIModelApi` constructor |
| `AzureAIAPI.generate` / `completion_params` / `resolve_streaming` | `AzureAIModelApi.GenerateAsync` / `CompletionParams` / `ResolveStreaming` |
| `AzureAIAPI.max_tokens` / `should_retry` / `is_auth_failure` / `collapse_user_messages` / `connection_key` | same-named methods on `AzureAIModelApi` |
| `AzureAIAPI.service_model_name` / `canonical_name` / `is_mistral` / `_is_mistral_model` / `_is_openai_model` | `ServiceModelName` / `CanonicalName` / `IsMistral` / `IsMistralModel` / `IsOpenAIModelName` |
| `AzureAIAPI.handle_azure_error`; `except AzureError` | `HandleAzureError`; `AsAzureError` (fidelity note 3) |
| `_StreamChoice` / `azureai_completion_from_stream` | `StreamChoice` / `AzureAIStreamAccumulator.CompletionFromStreamAsync` |
| `chat_request_messages` / `chat_request_message` / `chat_content_item` / `mistral_message_reducer` / `fold_user_message_into_tool_message` | `Tools/AzureMessageConversion` |
| `chat_tools` / `chat_tool_definition` / `chat_tool_choice` / `chat_tool_call` | `Tools/AzureToolConversion` |
| `chat_completion_choices` / `chat_complection_choice` / `chat_completion_assistant_message` / `chat_completion_stop_reason` | same-named static methods on `AzureAIModelApi` |
| `azure.ai.inference.models.ChatCompletions` (dict-backed `as_dict()`) | `AzureChatCompletions` / `AzureChatChoice` / `AzureChatResponseMessage` (raw `JsonObject`) |
| `_call_tools.py` `parse_tool_call` (JSON branch), `tool_parse_error_message`, `_object_with_trailing_quotes` | `Tools/ToolCallParsing.cs` |
| `util/_json.py` `json_schema_dump`, `JSON_SCHEMA_EXTENDED_FIELDS`, `JSONSchema` | `Tools/JsonSchemaDump.cs`, `Core/Tools.cs` (`ToolParam.ToJson`) |
| `util/util.py` `normalize_stream_arg`, `model_base_url`, `environment_prerequisite_error` | `Util/ProviderUtil.cs` |
| `util/azure_hosting.py` `resolve_azure_token_provider`, `DEFAULT_AZURE_AUDIENCE` | `Util/AzureHosting.cs` (`ResolveAzureCredential`, `AudienceTokenCredential`) |
| *(none)* Entra token diagnostics for the `token` command | `Util/EntraTokenInfo.cs` |
| *(none)* deployment discovery for `models` / `test-all` | `Foundry/FoundryCatalog.cs` |
| `_providers/anthropic.py` (Azure path: `max_tokens`, `message_stop_reason`, usage, tools, streaming) | `Anthropic/AnthropicFoundryModelApi.cs` (companion) |
| `_util/http.py` `is_retryable_http_status`, `parse_retry_after(_from_exception)`, `status_code_of` | `Util/HttpRetryUtil.cs` |
| `_openai.py` `needs_max_completion_tokens`, `openai_stop_details`, `openai_media_filter` | `Util/OpenAIUtil.cs` |
| `_model_output.py` `collect_stop_details`; `ModelOutput`, `ChatCompletionChoice`, `ModelUsage`, `StopReason`, `StopDetails` | `Util/ModelOutputUtil.cs`; `Core/ModelOutput.cs` |
| `_util/images.py` `inline_media_data_uri`, `_util/url.py` data-URI helpers | `Util/InlineMedia.cs` |
| `_util/logger.py` `warn_once` | `Util/ProviderLogger.WarnOnce` |
| `_model.py` `RetryDecision`; `_model_call.py` `ModelCall`, `ModelCallFilter` | `Core/RetryDecision.cs`; `Core/ModelCall.cs` |
| `_chat_message.py` `ChatMessage*`; `_util/content.py` `Content*`; `tool/_tool_*.py`; `_generate_config.py` (subset) | `Core/ChatMessage.cs`, `Core/MessageContent.cs`, `Core/Content.cs`, `Core/Tools.cs`, `Core/GenerateConfig.cs` |
| `_stream.py` `Stream*Event`, `StreamHandler`, `ModelStreamObserver`, `model_stream_requested`, `report_model_stream_*` | `Core/Streaming.cs` |
| `_util/error.py` `PrerequisiteError`; `azure.core.exceptions.ServiceResponseError` | `Core/Errors.cs` (`PrerequisiteError`, `ServiceResponseException`) |
| `json.dumps` / `json.loads`, `shortuuid.uuid`, Python truthiness | `Util/PythonJson.cs`, `Util/ShortUuid.cs`, `Util/PythonSemantics.cs` |
| `tests/model/providers/test_azureai.py`, `test_canonical_names.py::TestAzureAICanonicalName`, `test_parse_tool_call.py` | `tests/InspectAzureAI.Tests` — tests keep the Python test names where the feature survived |

## Intentionally out of scope

Beyond the lite cut above, these Inspect pieces are **not** ported (same as `main`):

- the eval loop, `Model.generate` wrapper and its input transforms (collapsing consecutive user
  messages, moving tool-result images into user messages, reasoning-history filtering, `max_tokens`
  defaulting — the sample passes `api.MaxTokens()` explicitly);
- the tenacity retry loop, adaptive concurrency and connection pooling (`ShouldRetry` /
  `ConnectionKey` / `MaxConnections` are exposed for a host to drive);
- transcript / `ModelEvent` recording (a `ModelCall` is returned instead of registered on an event);
- sample limits, token/time limits, caching, the model-info registry (`model_family()` therefore
  always equals `service_model_name()`), tool execution and approval, and `stream_idle_timeout`.

## Fidelity notes

Places where the port deliberately deviates from the Python implementation, and why:

1. **Raw JSON instead of the SDK's typed response models.** The .NET `ChatCompletions` /
   `StreamingChatCompletionsUpdate` types drop undeclared fields, notably the per-choice
   `content_filter_results` and the tool-call `index` on streamed fragments. The provider therefore
   reads `Response.Content` for non-streamed calls and parses the raw SSE body
   (`StreamingResponse.GetRawResponse().ContentStream`, via `SseParser`) for streamed calls, exposing a
   dict-backed `AzureChatCompletions` view — the same shape the Python SDK's mapping-backed models give.
   The SDK still builds and sends the request (auth headers, `api-version`, `extra-parameters`).
2. **`max_completion_tokens` is actually sent.** The Python provider passes it through `**request` into
   the SDK's `**kwargs`, which azure-ai-inference forwards to the HTTP pipeline rather than the body —
   so for gpt-5 / o-series families Python effectively sends no limit. The port sends it as a
   pass-through extra (`AdditionalProperties`), which is the evident intent; this adds the
   `extra-parameters: pass-through` header on those requests.
3. **Azure error types.** Python catches the whole `AzureError` family; the .NET SDK throws a wider
   mix, which `AzureAIModelApi.AsAzureError` normalises on both the streaming and the non-streaming
   path before the `ModelCall` is error-marked and `HandleAzureError` runs:
   - `azure.core.HttpResponseError` ↔ `RequestFailedException` with `Status > 0`;
   - `ServiceRequestError` (connection failure, not retried) ↔ `RequestFailedException` with
     `Status == 0` (Azure.Core wraps `HttpRequestException` this way);
   - `ServiceResponseError` (response could not be read, retried as transient) ↔ the provider's
     `ServiceResponseException`, into which the port wraps the raw `IOException` and the
     `TaskCanceledException` Azure.Core's network timeout (`ClientOptions.Retry.NetworkTimeout`, 100 s)
     raises — Azure.Core does not wrap either — while an `OperationCanceledException` caused by the
     caller's own token propagates untouched;
   - anything else (`JsonException` from a malformed SSE chunk ↔ the SDK's `json.JSONDecodeError`,
     the empty-stream `InvalidOperationException`) is not an Azure error and propagates unrecorded and
     unretried, like Python.
   `str(ex.message)` ↔ the leading line of `RequestFailedException.Message` (Azure.Core appends a
   status/content/header dump).
4. **SDK-level retries stay on.** Both SDK pipelines retry underneath Inspect's `should_retry` loop; the
   defaults differ (azure-core: 10 attempts, Azure.Core: 3). When Azure.Core exhausts its attempts on
   thrown failures it raises an `AggregateException` ("Retry failed after N tries"); `AsAzureError`
   unwraps it to the *last* attempt's exception (azure-core re-raises the last `AzureError`) and maps
   that by the rules in note 3. `AzureAIClientSettings.ConfigureClientOptions` can change or disable the
   SDK retries (the tests run with 0, and with 2 for the exhaustion case).
5. **JSON parse-error text.** `ToolCall.ParseError` embeds `System.Text.Json` messages rather than
   Python's `json` messages (e.g. `Expecting value: line 1 column 1 (char 0)`); the surrounding
   `Error parsing the following tool call arguments: … Error details: …` framing, middle-truncation at
   16 KiB (decoding split multibyte sequences with `errors="ignore"` semantics), trailing-quote recovery
   and the depth-100 limit are identical. Tool-call JSON is parsed with `PythonJson.Loads`, which keeps
   `json.loads`' last-wins handling of duplicated object keys (`JsonNode.Parse` would throw). The one
   acceptance difference: the non-standard `NaN` / `Infinity` / `-Infinity` tokens `json.loads` accepts
   are reported as a parse error (a `JsonNode` cannot hold them; models essentially never emit them).
6. **No YAML fallback (lite).** Python `yaml.safe_load`s tool arguments that are not a JSON object into
   the tool's first parameter; that branch only fires for prompt-emulated tool calls, so the lite port
   drops it and such arguments yield an empty argument object with no parse error.
7. **Empty stream** raises `InvalidOperationException` (Python: a plain `RuntimeError`), with the same
   message; like Python it is not retried.
8. **Streaming observer.** `ModelStreamObserver` ports `on_stream` delivery, handler detachment on
   exception, usage/heartbeat progress and the "choice 0 only" gating; stall scopes
   (`stream_idle_timeout`) and partial-output flushing are not ported, so `ModelStreamRequested()` is true
   only when a handler is installed.
9. **Numeric precision.** The SDK stores `temperature`, `top_p` and the penalties as `float`
   (single precision) and `seed` as `long`; `0.0` therefore serialises as `0`.
10. **`streaming` argument.** `null` is accepted as a synonym for `"auto"` (C# cannot default an
    `object` parameter to a string).
11. **`model_family()`** has no model-info registry to consult and always returns `service_model_name()`.
12. **Refusal text is not a stop detail.** `openai_stop_details` reads `message.refusal` with
    `getattr`, and azure.ai.inference's dict-backed `ChatResponseMessage` exposes no such attribute, so
    for this provider Python never produces a `refusal` explanation; the port reproduces that by not
    reading the raw `refusal` key (a refusal with no filtered category yields no stop details).
13. **`max_completion_tokens=true` model arg (port-only).** Python emits `max_completion_tokens` only for
    gpt-5 and o-series names; reasoning models under other names (MAI-Thinking-1) reject `max_tokens`. The
    port pops a boolean `max_completion_tokens` model arg and, when true, sends `config.MaxTokens` as
    `max_completion_tokens` for any family. A non-boolean value is left in `model_extras` as a body field,
    exactly as Python would forward it. `test-all` applies the arg automatically when a deployment answers
    400 asking for it.
14. **Deployment discovery (`Foundry/FoundryCatalog.cs`) is port-only.** Inspect takes the model name from
    the CLI; the sample's `models` and `test-all` commands resolve the account behind the endpoint through
    Azure Resource Manager (`https://management.azure.com/.default` scope on the same credential) and list
    its deployments. Cognitive Services User includes the read actions this needs.
15. **Anthropic companion (`Anthropic/AnthropicFoundryModelApi.cs`) is a separate provider, not part of the
    `azureai` port.** It exists so `test-all` can cover every deployment on the resource. It mirrors
    Inspect's `anthropic/azure` path where the sample needs it (see the section above) and diverges by
    using Entra ID and deriving the base URL; Python requires `AZUREAI_ANTHROPIC_API_KEY` and
    `AZUREAI_ANTHROPIC_BASE_URL`.
16. **Entra ID tokens are sent as `Authorization: Bearer` only.** Python feeds the Entra token into
    `AzureKeyCredential`, so azure-ai-inference sends it in both `Authorization` and `api-key`. Azure AI
    Services and Azure OpenAI gateways validate `api-key` first when it is present and reject the JWT with
    401, which is why `az login` can look broken. The port hands the credential to the SDK's
    `TokenCredential` constructor instead: only the bearer header is sent and the SDK caches and refreshes
    the token. `AudienceTokenCredential` keeps the Python `AZUREAI_AUDIENCE` semantics (default
    `https://cognitiveservices.azure.com/.default`) because that constructor would otherwise request
    `https://ml.azure.com/.default`. The credential is always `DefaultAzureCredential`, as in Python;
    `Azure.Identity` is a hard dependency, so Python's `ImportError` → `PrerequisiteError` branch cannot
    occur (credential construction failures surface with the same message). The `token` command is an
    addition with no Python counterpart.
17. **`connection_key`** is `f"{api_key}:{model_name}"` in Python; with no key in play the port returns the
    model name, which yields the same one-pool-per-model behaviour.
18. **Per-family reasoning mapping (port-only).** Python forwards `reasoning_effort` only in providers whose
    vendor API defines it (openai-compatible, anthropic); the Foundry route fronts several vendors, so
    `ReasoningParams` maps `ReasoningEffort` / `ReasoningTokens` per family (table above), seeded from vendor
    documentation and corrected by the `params` probe. Unknown families get nothing, like Inspect's gating of
    `reasoning_effort` to gpt-5 / o-series. The derived fields are part of `completion_params`, so they appear
    in the recorded `ModelCall` request; a model arg with the same key overrides them on the wire while the
    snapshot keeps the derived value (the Python snapshot excludes `model_extras`).
19. **Reasoning content.** `reasoning_content` (and the `reasoning` / `thinking` spellings) becomes a leading
    `ContentReasoning` on the assistant message and `StreamReasoningEvent` deltas; it is not replayed on the
    model-inference route. Anthropic `thinking` / `redacted_thinking` blocks keep their `signature` / `data`
    and are replayed first on later turns; a reasoning item without a signature is dropped with a warning
    (Inspect raises). Anthropic reports no separate reasoning token count, so `ReasoningTokens` stays null there.
20. **Cohere text markers.** Cohere command deployments wrap the answer in `<|START_TEXT|>…<|END_TEXT|>` on the
    model-inference route; the parsed text drops the markers (a missing end marker is tolerated), the raw
    response on the `ModelCall` keeps them.
21. **`extra-parameters: pass-through` on streamed calls.** The .NET SDK sets the header only on the
    non-streaming path; the gateway rejects unknown body fields without it, so `PassThroughExtraParametersPolicy`
    adds it to streamed requests that carry pass-through fields, matching what azure-ai-inference does for
    `model_extras` on both paths.
22. **JSON model args and reserved names (port-only).** `-M key=value` values that start with `{` or `[` are
    parsed as JSON (Inspect's CLI parses YAML scalars only); `model_format` and `anthropic_beta` are reserved
    (family hint and `anthropic-beta` header) and never reach the body; the Anthropic companion now accepts
    model args as top-level fields.
23. **Parameter probes (port-only).** `params` sends each candidate alone on top of a baseline and classifies
    it from the status and the response with the heuristics listed under "Parameters by model"; a grouped
    probe (`seed` + penalties) is split into single-field probes only when rejected. Verdicts are evidence
    of what the gateway did on that day, not a vendor contract.

## SWE showcase

`src/InspectAzureAI.Eval` and `src/InspectAzureAI.Swe` port Inspect AI's eval components (datasets, tasks,
solvers, scorers, agents, tools, Docker/local sandboxes, the `Model` wrapper, the sandbox agent bridge, the eval
runner and JSON logs) and two Inspect SWE agents (mini-swe-agent as a native C# loop, and the real Claude Code
CLI bridged to the Azure providers) to .NET. `src/InspectAzureAI.SweShowcase` runs them on your Foundry
deployments; see [docs/swe-showcase.md](docs/swe-showcase.md) for the architecture, every flag, the Python → C#
mapping and the fidelity notes.

```bash
az login && export AZUREAI_BASE_URL=https://<resource>.services.ai.azure.com/models
dotnet run --project src/InspectAzureAI.SweShowcase -- run --fake --sandbox local --task hello-swe --agent mini-swe   # offline smoke test
dotnet run --project src/InspectAzureAI.SweShowcase -- run --task hello-swe --agent mini-swe                          # Docker sandbox, real model
dotnet run --project src/InspectAzureAI.SweShowcase -- run --task pytest-fix --agent claude-code --model claude-sonnet-4-6
dotnet run --project src/InspectAzureAI.SweShowcase -- show logs/<timestamp>_hello-swe_<id>.eval
dotnet run --project src/InspectAzureAI.ModelMatrix -- --parallel 3 --markdown docs/model-matrix-results.md   # Claude Code × every deployment
```
