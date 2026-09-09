# Using providers with Microsoft Foundry models

## Providers keep application code stable while translating each model’s API

A **provider is an adapter** between your application’s conversation objects and a model service’s request and response format. Microsoft Foundry supplies access to models from different publishers. Your provider chooses the right endpoint, encodes messages and tools, applies supported settings, and converts the reply into a common result.

In this repository, `AzureAIModelApi` and `AnthropicFoundryModelApi` both implement `IModelApi`. An evaluation can therefore keep the same task, tools, and scoring logic while changing the adapter and deployment. That shared contract does not make every Foundry model a chat model, or make every model accept the same settings.

**The design rule is: reuse an adapter for another model with the same protocol; add an adapter for a different protocol; use a different operation contract when the task itself changes.** Embedding vectors, generated images, document extraction, and real-time audio need more than changing the model name in a chat request.

There is also a migration decision. Microsoft lists **August 26, 2026** as the retirement date for the Azure AI Inference beta SDK. The reusable library here still references `Azure.AI.Inference` 1.0.0-beta.5. Plan new OpenAI-compatible integration around the current v1 APIs, while retaining a native Messages adapter where required. The successful September 7 `/models` requests establish what this resource did then; they do not reverse the SDK retirement notice. [Microsoft migration guidance](https://learn.microsoft.com/en-us/azure/foundry/how-to/navigate-from-classic)

This guide explains how to use the code that exists, what its adapters translate, and where additional support is needed.

## Read this as a guide to two implementations and a changing model catalog

**Audience:** developers using the C# project. **Documentation checked:** September 8, 2026. **Live evidence:** the September 7 expenses rerun on one configured Foundry resource. This report made no new inference calls.

Two implementations appear throughout:

- **Reusable library:** `InspectAzureAI.Provider`, with the `InspectAzureAI.Eval` runtime around it. It supports richer message types, streaming, reasoning controls, schema translation, discovery, and diagnostics.
- **Teaching demo:** `InspectAzureAI.LayersDemo`, a separate, self-contained application. It has its own `ModelAPI` base class, two small HTTP providers, a toy shell, and a simpler retry loop. This is the application used in the rerun.

The protocol map covers the major communication families rather than claiming a tested inventory of every catalog entry. Catalog availability, deployment region, model version, hosting option, and API support can differ. Use the deployment’s model card and endpoint details for the exact combination you intend to call.

**Keep these names separate:** the publisher is OpenAI, Anthropic, Microsoft, Mistral, or another model maker; the deployment name is the identifier on your resource; the SDK is a client library; the provider is this project’s translation code; the protocol is the wire contract, such as Chat Completions or Messages.

## Choose the operation and API before choosing the provider

The table is a routing guide. Its route paths are relative to the appropriate service endpoint; use the hostname documented for that deployment. “Existing support” refers to this repository’s two reusable chat providers, not to the overall Foundry platform. A model catalog entry is evidence that the model is offered, not that `/chat/completions` accepts it.

Microsoft’s current guidance separates resource-level OpenAI access, the Anthropic endpoint, and project APIs for Foundry features. A project endpoint is not a drop-in base URL for this library’s `/models` provider. [SDK and endpoint overview](https://learn.microsoft.com/en-us/azure/foundry/how-to/develop/sdk-overview)

| Operation / family | Wire route or contract | Existing support | What to use |
| --- | --- | --- | --- |
| Chat through legacy inference | models/chat/completions?api-version=2024-05-01-preview in the demo | AzureAIModelApi; demo AzureAIModelAPI | Current project route; track the SDK migration separately. |
| Chat through OpenAI v1 | openai/v1/chat/completions | No dedicated v1 adapter in the reviewed code | An OpenAI-compatible client/adapter for deployments exposing this operation. |
| Claude conversation | anthropic/v1/messages | AnthropicFoundryModelApi and demo companion | Native Messages adapter; check the specific Claude version’s features. |
| Document extraction / reranking | Model-specific parsing or reranking contract | Not implemented by these chat providers | Cohere Parse is image-to-Markdown with no tool calling; use its documented operation. Reranking is a separate task. |
| Embeddings | openai/v1/embeddings for compatible deployments | No embedding operation in IModelApi | An embedding client and vector result type. |
| Foundry project / agent operations | api/projects/<project> and its SDK-exposed APIs | Not supplied by these two providers | Foundry project SDK for project features and platform-managed tools. |
| Image generation / editing | Image API or vendor API; FLUX.2 uses providers/blackforestlabs/v1/flux-2-pro | Not implemented | An image adapter with image inputs/outputs and the documented endpoint, version, and authentication. |
| Managed compute / custom models | managed-deployments/<deployment>/; openai/v1 for compatible chat runtimes | No general managed-compute adapter | Inspect the model runtime contract; compatible chat can share a protocol adapter. |
| Realtime audio | WebSocket openai/v1/realtime, or WebRTC/SIP integration | Not implemented | A session API with audio events and bidirectional transport. |
| Responses | openai/v1/responses | No Responses adapter in the reviewed code | Add item-based input/output and continuation support; verify model and region support. |

FLUX.2 [pro] has a documented BFL provider route; the 404 observed when it was sent to the demo’s chat route is not evidence that image generation is unavailable. Likewise, Microsoft documents `Cohere-parse-v5` as an image-to-text extraction model without tool calling. [FLUX API guide](https://learn.microsoft.com/en-us/azure/foundry/foundry-models/how-to/use-foundry-models-flux) · [Model capabilities](https://learn.microsoft.com/en-us/azure/foundry/foundry-models/concepts/models-sold-directly-by-azure)

Managed compute can share OpenAI-compatible routing when its runtime implements that contract; other runtimes have a managed-deployment route. Real-time audio adds a different transport and session lifecycle. These are separate integration decisions, not new names for the existing chat loop. [Managed compute routes](https://learn.microsoft.com/en-us/azure/foundry/concepts/managed-compute-overview) · [Realtime communication](https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/realtime-audio-websockets)

## Start with one deployment and an explicit route

The commands below run from the repository’s `src` directory. They use the existing implementation and your Azure CLI sign-in. Replace the resource and deployment names with your own; `chat`, `stream`, `tools`, and `params` send billable inference requests when run without `--fake`.

```sh
az login
export AZUREAI_BASE_URL=https://YOUR-RESOURCE.services.ai.azure.com/models

# Inspect settings, then verify identity and list deployments.
dotnet run --project InspectAzureAI.Sample -- config
dotnet run --project InspectAzureAI.Sample -- token
dotnet run --project InspectAzureAI.Sample -- models

# OpenAI-compatible chat on the existing /models route.
dotnet run --project InspectAzureAI.Sample -- chat "Hello" --model gpt-5.4-mini

# Claude uses the Messages route on the same resource.
dotnet run --project InspectAzureAI.Sample -- chat "Hello" \
  --model claude-sonnet-4-6 --route anthropic

# Verify streaming and a complete native tool loop separately.
dotnet run --project InspectAzureAI.Sample -- stream "Hello" --model gpt-5.4-mini
dotnet run --project InspectAzureAI.Sample -- tools --model gpt-5.4-mini
```

The providers use `DefaultAzureCredential` and bearer tokens. The existing default audience is `https://cognitiveservices.azure.com/.default`; `AZUREAI_AUDIENCE` overrides it. `AZUREAI_ANTHROPIC_BASE_URL` can supply the Claude base explicitly. Deployment discovery uses an Azure Resource Manager token and requires read access; successful inference does not by itself establish that discovery is authorized.

Use the audience documented for the endpoint you integrate. Current Microsoft project/SDK examples also use `https://ai.azure.com/.default`; do not assume that changing an endpoint and retaining every authentication setting is sufficient. This branch of the library does not read API-key variables, even where the service itself offers key authentication. [Current SDK authentication examples](https://learn.microsoft.com/en-us/azure/foundry/how-to/develop/sdk-overview)

The selector syntax depends on the application. LayersDemo uses `--model azureai/<deployment>` or `anthropic/<deployment>`. The provider-only Sample uses the bare deployment name and `--route models|anthropic`. The deployment field sent to Foundry must match the deployed identifier.

## Your C# call stays the same after the adapter is selected

This helper compiles against the current reusable provider library. Supply the resource root without `/models` or `/anthropic`, the bare deployment name, and the route decision derived from configuration or deployment metadata.

```csharp
using InspectAzureAI.Provider;
using InspectAzureAI.Provider.Anthropic;
using InspectAzureAI.Provider.Core;

public static class ProviderExample
{
    public static async Task<string> AskAsync(
        string resourceRoot,
        string deployment,
        bool useMessages,
        CancellationToken cancellationToken = default)
    {
        string root = resourceRoot.TrimEnd('/');
        IModelApi provider = useMessages
            ? new AnthropicFoundryModelApi(deployment, baseUrl: root + "/anthropic")
            : new AzureAIModelApi(deployment, baseUrl: root + "/models");

        GenerateResult result = await provider.GenerateAsync(
            [new ChatMessageUser("Reply with a short greeting.")],
            [],
            ToolChoice.Auto,
            new GenerateConfig { MaxTokens = 1024 },
            cancellationToken);

        if (result.Error is { } error) throw error;
        return result.Output?.Completion ?? "";
    }
}

```

This is one generation call. It does not execute returned tool calls or add the evaluation runtime’s retry policy. For evaluation workloads, `FoundryModels.Create(...)` wraps an adapter in `InspectAzureAI.Eval.Model.Model`, which owns retries, concurrency, logging, and related runtime behavior. Preserve the provider result’s structured content and tool calls when building an agent; reading only `Completion` is suitable for this text-only example.

The helper was compile-checked with .NET 10; it was not run against the service for this report.

## The provider translates meaning, not just the endpoint URL

The existing adapters accept common messages, tool definitions, tool choice, and `GenerateConfig`. Their serializers and parsers perform the conversions below. The Responses column describes the additional contract a new adapter would need, rather than an implementation already present.

For example, a calculator call becomes an assistant function call on Chat Completions, a `tool_use` content block on Messages, and a `function_call` item on Responses. The local engine executes the calculator and returns its result in the corresponding format. [Responses input and output contract](https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/responses)

| Common concept | Chat Completions | Anthropic Messages | Responses — proposed adapter |
| --- | --- | --- | --- |
| Assistant tool request | tool_calls; function.arguments is JSON encoded as a string | content block: tool_use; input is a JSON object | function_call item; arguments and call_id |
| Completion text | choices[].message.content | Text blocks within content[] | Output message items containing text blocks |
| Generation limit | max_tokens or max_completion_tokens, selected by model/profile | max_tokens; thinking budget must fit | max_output_tokens, with model-specific semantics |
| Instructions | system message in messages[] in this adapter | Top-level system in this adapter | instructions and/or supported input message roles |
| Stop / finish | finish_reason: stop, tool_calls, length, etc. | stop_reason: end_turn, tool_use, max_tokens, etc. | Response status and incomplete details; inspect output items |
| Stream framing | SSE choice deltas; assemble text and tool arguments | SSE message/content-block events and deltas | SSE response/item/content events |
| Tool definition | tools[].function.parameters | tools[].input_schema | Function tool schema in the Responses tools list |
| Tool result | role: tool with tool_call_id | user content block: tool_result with tool_use_id | function_call_output item with call_id |
| Usage | prompt_tokens / completion_tokens; optional details | input_tokens / output_tokens and cache fields | input_tokens / output_tokens and API-specific details |

A single tool result illustrates why a URL substitution is insufficient. These are illustrative fragments after a calculator has returned `38.5`; the call IDs stand in for IDs supplied by the model.

```json
{"role":"tool","tool_call_id":"call_example","content":"38.5"}
```

```json
{"role":"user","content":[
  {"type":"tool_result","tool_use_id":"toolu_example","content":"38.5"}
]}
```

The first fragment is Chat Completions; the second is Messages. Both represent the same result. The adapter must also preserve preceding assistant tool calls, keep result IDs matched, and assemble streamed argument fragments before execution. The providers parse and translate local function requests; the tool executor performs the functions.

Ordinary streaming is **server-sent events (SSE)** over an HTTP response. It is not a bidirectional audio session. The reusable library turns vendor events into common text, reasoning, and tool-argument stream events, then returns a final normalized output. Reported usage can be absent in a stream; the Azure adapter warns about that case rather than proving zero token use.

Native server tools form another boundary. The full Anthropic adapter has explicit handling for server-side web search and remote MCP declarations. That does not make those capabilities automatically available on every other route.

## A shared protocol still needs model-specific settings

The table describes **the mappings currently encoded in this repository**, not a universal promise from each publisher. It includes code comments recording earlier probes; those reasoning probes were not repeated for this report. `GenerateConfig` is a common vocabulary, and each adapter supports only a subset of it.

In the fuller library, `model_format` can supply the publisher for reasoning-family selection. Deployment names remain part of several token-limit and message-dialect heuristics. Explicit model arguments are applied after derived settings and can override them. Treat that flexibility as an escape hatch that needs verification.

| Family / examples | Current code mapping | Boundary to verify |
| --- | --- | --- |
| Anthropic / Claude | Separate Messages adapter; adaptive thinking and output_config.effort, or an explicit thinking budget | Version-specific accepted efforts and schemas; preserve thinking signatures on replay. |
| Cohere Command | thinking object; ReasoningTokens maps to token_budget | Code notes say disabling thinking was accepted but ignored in an earlier probe. Parse and Rerank are different operations. |
| DeepSeek | reasoning_effort for the current V4 mapping | Earlier code notes distinguish an honored effort field from an ignored thinking toggle; do not generalize across versions. |
| Microsoft / MAI-Thinking-1 | reasoning_effort; explicit max_completion_tokens=true override in the library | The teaching demo learns the token field from a 400; the library does not use that same learning loop. |
| Mistral | Message reducer; no default token limit for detected Mistral models | The reducer folds a following user message into a preceding tool result; model capability remains deployment-specific. |
| Moonshot / Kimi | thinking enabled/disabled object; reasoning content can be returned | Code notes record that a disabled setting did not suppress thinking in an earlier Foundry probe. |
| OpenAI GPT-5 / o-series | max_completion_tokens and reasoning_effort | Accepted effort levels and sampling controls vary. Current token detection does not cover every future GPT family. |
| OpenAI legacy chat / GPT-4o | max_tokens; no derived reasoning control | A raw unsupported reasoning parameter can still be rejected. |
| Router | reasoning_effort forwarded by the current mapping | Record the requested deployment and the actual returned model; routing can change what behavior is observed. |
| xAI / Grok | reasoning_effort; reasoning token usage where returned | The mapping does not promise that effort=none disables reasoning or that internal reasoning text is exposed. |

**Accepted does not mean honored.** A server can return HTTP 200 while ignoring an extra field. The `params` command sends a baseline and isolated probes, then distinguishes accepted, ignored, rejected, error, and not-applicable outcomes using available evidence.

```sh
dotnet run --project InspectAzureAI.Sample -- params \
  --only gpt-5.4-mini,MAI-Thinking-1 \
  --params temperature,stop,reasoning_effort=high --parallel 1

dotnet run --project InspectAzureAI.Sample -- chat "Explain briefly" \
  --model MAI-Thinking-1 --model-arg max_completion_tokens=true
```

Check the emitted request as well as the response. For example, the current Azure adapter derives `response_format` from `ResponseSchema`, but some other `GenerateConfig` properties are not translated at all. Its general configuration comments also lag some newer mappings; the implementation and captured request are stronger evidence than the comment alone.

The full Anthropic adapter currently emits `output_format` for response schemas. That is a description of this code path, not a guarantee that the same field and beta settings apply to every newer Claude release. Test schema enforcement with the exact deployed version before relying on it.

Two more boundaries matter. The Azure message converter accepts text and images but rejects audio and video inputs, even though the common data model defines those types. Also, an effort label such as `high` is a vendor-specific setting, not a standardized compute budget. A model returning only final text has not thereby proved that no internal reasoning took place.

## Keep translation, execution, and resilience in their owning layers

These responsibilities explain what can stay stable when another adapter is added. The fuller library and the teaching demo have the same broad separation, but not identical implementations or policies.

| Owner | Responsibility | Important boundary |
| --- | --- | --- |
| 1. Task and scorer | Define the question, tools, expected answer, and grading | A grading error can occur even when communication is correct. |
| 2. Evaluation/model runtime | Coordinate calls, retry budgets, concurrency, transcript events, and usage | Do not assume a direct IModelApi call includes the full runtime. |
| 3. Provider adapter | Serialize requests, parse replies, classify failures, translate streams | Do not execute a local function merely because a tool call was parsed. |
| 4. Transport and credential | Send HTTP/SSE, acquire and refresh bearer tokens | Endpoint, audience, version, and headers must match the service. |
| 5. Foundry deployment | Serve the selected model and enforce operation support, filters, and quota | An adapter cannot create model capabilities or additional quota. |
| 6. Tool runtime / sandbox | Execute permitted local tool requests and return results | The demo shell is simulated; a command failure need not be a model failure. |

In **LayersDemo**, each HTTP request has a 180-second timeout, surfaced as a non-retried 408. The model layer retries selected transient failures with exponential backoff and jitter, uses `Retry-After` as a floor, and permits five retries by default. The Azure demo provider also learns specific token-limit or temperature incompatibilities from a 400 and resends. This bounded repair is not an instruction to retry arbitrary bad requests.

In the **full library**, the provider classifies retryable failures and the `Model` wrapper applies retry counts, budgets, attempt/stream-idle timeouts, and concurrency management. Its HTTP classification includes 408, and its wait calculation uses a supplied `Retry-After` value or backoff. Do not copy the demo’s timeout or backoff description onto that runtime.

Troubleshoot by failure type: a 400 can indicate an unsupported field or operation; a 404 can indicate the wrong route or deployment; 401/403 concerns credentials or permission; 429 requires quota-aware scheduling; timeouts and selected 5xx failures require bounded retries and health checks. Preserve the actual error body because the status code alone rarely identifies the cause.

## The rerun tested tool-based chat, not the entire Foundry catalog

The September 7 run exercised three samples per model: an expense total and named maximum item, an equal bill split, and a missing file. `gpt-5.4-mini` graded every completed sample. The main run used four concurrent model evaluations and up to three concurrent samples per evaluation.

All 16 main evaluations completed and the automated scoreboard awarded 48/48 correct grades in about 80 seconds. Manual inspection found one false positive: GPT-4o gave the largest price but omitted “taxi,” a fact explicitly required by the target. The table keeps successful protocol execution distinct from task correctness and endpoint failures.

The bars count deployment-level outcomes across the 21 checks: 16 evaluations completed, three failed on the chosen route, one timed out, and one failed after rate-limit retries. This shows where integration stopped. It does not score answer quality, and it does not generalize beyond this resource and test. The detailed outcomes follow.

**Deployment outcomes:** Completed: 16 of 21; Route error: 3 of 21; Timeout: 1 of 21; Rate limit: 1 of 21.

| Deployment group | Observed outcome | Provider implication |
| --- | --- | --- |
| 15 other main deployments | 3/3 automated and the required answer facts were present | The chosen route supported the tested text/tool workflow; this does not validate every feature. |
| Cohere-parse-v5 | 404 api_not_supported on the chat route | A parsing model needs the appropriate task-specific integration. |
| DeepSeek-V4-Flash-0731 | Some replies and one correct sample, then 180-second request timeouts; eval failed with 408 | Protocol compatibility and deployment reliability are separate. |
| FLUX.2-pro | 404 on the chat route | Image generation needs an image/vendor adapter. |
| GPT-4o | Automated 3/3; required-fact review 2/3 because taxi was omitted | The grader accepted an incomplete answer; transport success did not prove task success. |
| Ministral-3B | 429 after retries even with one sample at a time; missing-file sample succeeded | The provider honored waits but could not remove a capacity constraint. |
| gpt-5.4-pro | 400 unsupported operation on the legacy chat route | Check a supported API such as Responses rather than treating the model as universally unusable. |

The 15 other main deployments were GPT-5.4-mini; GPT-5.6 Terra, Luna, Luna-2, and Sol; Kimi-K2.6 and Kimi-K2.7-Code; Grok-4.6; Cohere-command-a-plus-05-2026; Mistral-Large-3; MAI-Thinking-1; DeepSeek-V4-Flash and Pro; model-router; and Claude Sonnet 4.6. Their exact requested identifiers are preserved in the local run manifest.

Five additional deployment checks bring the rerun to **21 distinct deployments**, not 20. Failed evaluations are error outcomes, not zero-accuracy scores. The small three-sample task is useful integration evidence, not a model ranking or evidence of general catalog support.

The raw scoreboard also includes the grader’s calls in GPT-5.4-mini’s row because it identifies calls by model name. Its 13 displayed calls comprise 10 solver calls plus three grading calls. Solver-only usage was 3,944 input and 383 output tokens; the saved request audit separates the roles. This is an accounting limitation in the demo, not a vendor tokenization difference.

## Add coverage by protocol and verified capabilities

**For a new deployment on an existing protocol**, start with its deployment metadata and model card. Verify readiness, operation support, accepted settings, and output types. Then run plain text, streaming, a complete tool round trip, schema enforcement, and modality checks for the features you need. A successful “hello” is only the first check.

The repository already has useful building blocks: `FoundryCatalog` retrieves model, publisher format, version, state, and advertised capabilities; the matrix uses publisher format to choose Anthropic and filters non-chat deployments. Its current fallback treats a missing `chatCompletion` capability as supported. For wider catalog coverage, an explicit **unknown** state is safer than treating missing metadata as proof.

**For a new wire protocol**, implement a separate adapter. A Responses adapter would need item-based input/output, function-call continuation, event assembly, status handling, and appropriate retention of continuation state. Repointing `AzureAIModelApi` to `/openai/v1` does not implement those behaviors. Microsoft lists `gpt-5.4-pro` and `gpt-6-astra` among supported Responses models; that makes Responses an integration candidate, not a claim that those deployments have been tested here. [Responses model support](https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/responses)

**For a different operation**, introduce a suitable contract alongside chat: embeddings return vectors; image generation returns image artifacts; parsing returns document structure/text; real-time audio manages sessions and events. Share credentials, transport infrastructure, telemetry, and error handling where useful without forcing these outputs into a text completion.

Prefer a deployment profile keyed by resource, deployment, model version, and API route. Record supported modalities, tools, schemas, effort values, token-limit field, and required headers. Keep raw overrides visible in logs, and use explicit “unsupported” or “unknown” states. A publisher-wide boolean is too coarse for a growing catalog.

## Recommended implementation order

1. **Preserve the current evidence.** Keep the LayersDemo rerun as an integration example, including its grader and accounting caveats. Record adapter version, deployment identity, and normalized plus raw request/response details with each future run.
2. **Add OpenAI v1 Chat Completions and Responses deliberately.** Keep them separately selectable where their semantics differ. Replace reliance on the retired inference beta SDK through a tested migration, not an endpoint-string edit. Existing `/models` results remain historical evidence. [Migration guidance](https://learn.microsoft.com/en-us/azure/foundry/how-to/navigate-from-classic)
3. **Keep Claude’s native adapter version-aware.** Check accepted thinking controls, schema fields, content blocks, and required headers for the chosen deployment. Microsoft documents Messages for Claude hosting variants; feature availability can differ. [Claude API overview](https://learn.microsoft.com/en-us/azure/foundry/foundry-models/concepts/claude-models)
4. **Replace fragile name assumptions with reviewed profiles.** The current `NeedsMaxCompletionTokens` recognizes GPT-5 and o-series names. New families and arbitrary deployment aliases need verified metadata or explicit settings. Publisher hints alone do not replace every name-dependent check.
5. **Test observable behavior.** Include returned tool IDs, multi-turn replay, split stream events, missing usage, cancellation, 429 waits, structured outputs, and unsupported-operation errors. Validate grading separately from model communication.
6. **Add non-chat adapters when the application needs them.** A provider abstraction should expose supported operations honestly; it should not imply every Foundry catalog model can run the expenses task.

The remaining product choices are which operations the application needs beyond chat, whether it requires Foundry-managed agent/tool state, and which exact deployments must share a common capability baseline. Answering those determines the next adapter to build.
